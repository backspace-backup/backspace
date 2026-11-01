using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backspace.Models;

namespace Backspace.Services.Platform.Windows;

/// <summary>Ignore only known desktop presentation and run-result fields.
/// New configuration fields remain significant until explicitly reviewed.</summary>
internal static class ServiceConfigurationComparison
{
    internal static AppConfig ReadDesktop(string installation)
    {
        var path = Path.Combine(ServicePortableLayout.Config(installation), "config.json");
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length is <= 0 or > ServiceRunAuthorization.MaximumApprovalBytes)
            throw new InvalidDataException("The saved backup settings could not be checked.");
        SkipByteOrderMark(source);
        return JsonSerializer.Deserialize<AppConfig>(source, ServiceRunAuthorization.Json)
            ?? throw new InvalidDataException("The saved backup settings are missing.");
    }

    /// <summary>Settings saved by an editor that writes a UTF-8 byte order mark
    /// load in Backspace; the service refused them ("'0xEF' is an invalid start
    /// of a value", lab VM, 1 November 2026).</summary>
    internal static void SkipByteOrderMark(Stream source)
    {
        Span<byte> head = stackalloc byte[3];
        var read = source.Read(head);
        if (read != 3 || head[0] != 0xEF || head[1] != 0xBB || head[2] != 0xBF) source.Position = 0;
    }

    internal static bool Equivalent(AppConfig approved, AppConfig current)
        => JsonNode.DeepEquals(ExecutionSettings(approved), ExecutionSettings(current));

    private static JsonNode ExecutionSettings(AppConfig config)
    {
        var node = JsonSerializer.SerializeToNode(config, ServiceRunAuthorization.Json)!;
        var preferences = node["preferences"]!.AsObject();
        foreach (var key in new[] { "theme", "accent", "openToMainWindow", "showTrayIcon", "startAtSignIn", "minimizeToTrayOnClose",
            "updateCheckFrequency", "lastUpdateCheckUtc", "skippedUpdateVersion" }) preferences.Remove(key);
        foreach (var backup in node["backups"]!.AsArray())
            foreach (var key in new[] { "lastEditMode", "lastRunStartUtc", "lastRunEndUtc", "lastRunStatus", "lastRunSummary",
                "lastSuccessUtc", "lastNotificationFiredUtc", "lastNotifiedStatus", "sourceLastSizeBytes",
                "lastVerifyUtc", "lastVerifyPass", "lastVerifySummary", "lastVerifyWasSample" }) backup!.AsObject().Remove(key);
        foreach (var destination in node["destinations"]!.AsArray()) destination!.AsObject().Remove("lastTokenValidatedUtc");
        return node;
    }

    internal static void RequireCurrent(string installation, AppConfig approved)
    {
        if (!Equivalent(approved, ReadDesktop(installation)))
            throw new ChangesNeedApprovalException("Your backup changes need approval. Open Settings → Administrator access and approve them before the next scheduled backup.");
    }
}
