using System;
using System.IO;
using System.Text.Json;

namespace Backspace.Services.Platform.Windows;

/// <summary>One request on the background service's run pipe.</summary>
internal sealed record ServiceHostRequest(int Version, string Command, string? Revision = null,
    string? BackupId = null, string? Operation = null, string? RunId = null, string? Path = null,
    string? Filters = null, bool SkipCloudOnly = false, string? VaultId = null, string? Key = null, bool WaiveConditions = false,
    bool WaiveBattery = false, string? OnlyTargets = null)
{
    // A size estimate carries the backup's exclusion rules (U123), which can
    // run to tens of kilobytes; every other request stays small.
    internal const int MaxMeasureRequestBytes = 512 * 1024;

    internal static ServiceHostRequest Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaxMeasureRequestBytes
            || bytes.Length > 4096 && bytes.IndexOf("\"command\":\"measure\""u8) < 0) throw new InvalidDataException("Invalid run request size.");
        var reader = new Utf8JsonReader(bytes);
        var names = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) if (reader.TokenType == JsonTokenType.PropertyName && !names.Add(reader.GetString()!))
            throw new JsonException("Duplicate property.");
        var request = JsonSerializer.Deserialize<ServiceHostRequest>(bytes, ServiceRunAuthorization.Json)
            ?? throw new JsonException("Empty request.");
        // Only the owner's own run may skip its conditions (U226).
        if ((request.WaiveConditions || request.WaiveBattery || request.OnlyTargets is not null) && request.Command != "start-cli")
            throw new InvalidDataException("Unsupported run request.");
        // The destinations left after a pause (U226): "id/storage|…", checked against the backup by the worker.
        if (request.OnlyTargets is { } only && (only.Length is 0 or > 2048 || request.Operation != "backup"
                || System.Linq.Enumerable.Any(only, c => char.IsControl(c))))
            throw new InvalidDataException("Unsupported run request.");
        if (request.Command != "unlock" && (request.VaultId is not null || request.Key is not null))
            throw new InvalidDataException("Unsupported run request.");
        if (request.Version != 1 || (request.Command is not ("measure") && (request.Path is not null || request.Filters is not null
                || request.SkipCloudOnly || bytes.Length > 4096)) || (request.Command switch
        {
            // U106: a size estimate with the service's rights; totals only.
            "measure" => !ServiceSizeMeasurements.Acceptable(request.Path) || request.Revision is not null || request.BackupId is not null || request.Operation is not null || request.RunId is not null,
            "measure-result" => !Guid.TryParseExact(request.RunId, "N", out _) || request.Revision is not null || request.BackupId is not null || request.Operation is not null,
            // U153: the vault key for the service's own copy; both or neither.
            "unlock" => request.Revision is not null || request.BackupId is not null || request.Operation is not null || request.RunId is not null
                || (request.VaultId is null) != (request.Key is null)
                || (request.VaultId is not null && (!VaultFile.IsId(request.VaultId) || !IsKey(request.Key))),
            "status" or "activity" => request.Revision is not null || request.BackupId is not null || request.Operation is not null || request.RunId is not null,
            // U224: the activity once it differs from the one the desktop holds (its sequence).
            // U246: "-1" (nothing heard yet) is accepted and answered at once: a desktop
            // that knows nothing is told now, never refused.
            "activity-wait" or "state-wait" => request.Revision is not { Length: > 0 and <= 20 } known
                || !System.Linq.Enumerable.All(known.StartsWith('-') ? known[1..] : known, char.IsAsciiDigit) || known is "-"
                || request.BackupId is not null || request.Operation is not null || request.RunId is not null,
            "cancel" or "run-status" => !Guid.TryParseExact(request.RunId, "N", out _) || request.Revision is not null || request.BackupId is not null || request.Operation is not null,
            "start" or "start-cli" => !Guid.TryParseExact(request.Revision, "N", out _) || string.IsNullOrWhiteSpace(request.BackupId)
                || request.Operation is not ("backup" or "check" or "prune") || request.RunId is not null,
            _ => true
        })) throw new InvalidDataException("Unsupported run request.");
        return request;
    }

    private static bool IsKey(string? text)
    {
        if (text is not { Length: 44 }) return false;
        Span<byte> buffer = stackalloc byte[33];
        var ok = Convert.TryFromBase64String(text, buffer, out var written) && written == VaultFile.KeyBytes;
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
        return ok;
    }
}
