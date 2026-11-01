using System;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backspace.Models;

namespace Backspace.Services.Platform.Windows;

/// <summary>Immutable, administrator-approved execution input. The desktop
/// supplies only a plan ID, operation and approval revision, never configuration
/// bytes, a command line, a working directory or a restore target.</summary>
internal sealed class ServiceRunAuthorization
{
    internal const int MaximumApprovalBytes = 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    internal sealed record Approval(int Version, string OwnerSid, string Revision, string ConfigurationSha256, AppConfig Configuration);
    internal sealed record Request(int Version, string Revision, string BackupId, string Operation);
    internal sealed record AuthorizedRun(string Revision, Backup Backup, AppConfig Configuration, RunKind Kind);
    private readonly byte[] _approval;
    private readonly string _owner;

    private ServiceRunAuthorization(byte[] approval, string owner)
    {
        _owner = owner;
        _approval = approval.ToArray();
        ParseApproval(_approval, owner);
    }

    internal static ServiceRunAuthorization Load(string path, string protectedInstallation, string ownerSid)
    {
        // Keep the approval handle open against replacement while inspecting and
        // reading it. The installer must publish a new approval atomically.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ServiceInstallationSecurity.ValidateProtectedRecord(path, protectedInstallation);
        if (file.Length is <= 0 or > MaximumApprovalBytes) throw new InvalidDataException("The service approval has an invalid size.");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        return new(bytes, ownerSid);
    }

    // Pure validation entry point for deterministic tests; production loads only
    // an administrator-protected file through Load above.
    internal static ServiceRunAuthorization ValidateForTest(byte[] approval, string ownerSid) => new(approval, ownerSid);
    internal Approval Snapshot() => ParseApproval(_approval, _owner);

    internal static byte[] CreateApproval(string ownerSid, string revision, AppConfig configuration)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Approval(1, ownerSid, revision, Digest(configuration), configuration), Json);
        ParseApproval(bytes, ownerSid);
        return bytes;
    }

    internal AuthorizedRun Authorize(ReadOnlySpan<byte> requestBytes, bool ownerCommand = false)
    {
        if (requestBytes.Length is <= 0 or > 4096) throw new InvalidDataException("The service run request has an invalid size.");
        RejectDuplicateProperties(requestBytes);
        var request = JsonSerializer.Deserialize<Request>(requestBytes, Json)
            ?? throw new InvalidDataException("The service run request is empty.");
        // Deserialize a private copy every time. Runtime outcome updates must not
        // mutate the approval retained for the next request.
        var approval = ParseApproval(_approval, _owner);
        if (request.Version != 1 || request.Revision != approval.Revision)
            throw new SecurityException("Backup settings changed. Refresh the approved service configuration.");
        var backup = approval.Configuration.Backups.SingleOrDefault(b => b.Id == request.BackupId)
            ?? throw new SecurityException("This backup has not been approved for the service.");
        if (!backup.Enabled) throw new SecurityException("This backup is disabled.");
        var kind = request.Operation switch
        {
            "backup" => RunKind.Backup,
            "check" when ownerCommand || backup.CheckSchedule is not null => RunKind.Check,
            "prune" when ownerCommand || backup.PruneSchedule is not null => RunKind.Prune,
            _ => throw new SecurityException("This operation has not been approved for the service."),
        };
        return new(approval.Revision, backup, approval.Configuration, kind);
    }

    internal static Approval ParseApproval(ReadOnlySpan<byte> bytes, string owner)
    {
        if (bytes.Length is <= 0 or > MaximumApprovalBytes) throw new InvalidDataException("The service approval has an invalid size.");
        RejectDuplicateProperties(bytes);
        var a = JsonSerializer.Deserialize<Approval>(bytes, Json) ?? throw new InvalidDataException("The service approval is empty.");
        if (a.Version != 1 || a.OwnerSid != owner || !new SecurityIdentifier(owner).IsAccountSid()
            || !Guid.TryParseExact(a.Revision, "N", out _) || a.Configuration is null
            || a.ConfigurationSha256 != SavedDigest(bytes))
            throw new SecurityException("The service approval is invalid or belongs to another account.");
        var cfg = a.Configuration;
        if (cfg.Backups is null || cfg.Destinations is null
            || cfg.Backups.Any(b => b is null || string.IsNullOrWhiteSpace(b.Id))
            || cfg.Destinations.Any(d => d is null || string.IsNullOrWhiteSpace(d.Id))
            || cfg.Backups.Select(b => b.Id).Distinct(StringComparer.Ordinal).Count() != cfg.Backups.Count
            || cfg.Destinations.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != cfg.Destinations.Count)
            throw new InvalidDataException("The service approval contains missing or duplicate identities.");
        foreach (var b in cfg.Backups)
        {
            if (b.SourcePaths is null || b.SourcePaths.Count == 0 || b.SourcePaths.Any(string.IsNullOrWhiteSpace)
                || b.Targets is null || b.Targets.Count == 0
                || b.Targets.Any(t => t is null || !cfg.Destinations.Any(d => d.Id == t.DestinationId))
                || !string.IsNullOrWhiteSpace(b.ExtraBackupArgs))
                throw new InvalidDataException("The approved backup has incomplete paths or unsupported custom engine arguments.");
            if (b.Schedule is null) throw new InvalidDataException("The approved backup has no schedule.");
            foreach (var schedule in new[] { b.Schedule, b.CheckSchedule, b.PruneSchedule }.Where(s => s is not null))
            {
                if (!Enum.IsDefined(schedule!.Frequency) || schedule.TimeOfDay < TimeSpan.Zero || schedule.TimeOfDay >= TimeSpan.FromDays(1)
                    || schedule.ExecutionTimeLimit < TimeSpan.Zero
                    || (schedule.Frequency == ScheduleFrequency.Hourly && schedule.HourlyInterval < 1)
                    || (schedule.Frequency == ScheduleFrequency.Weekly && (schedule.WeeklyDaysBitmask <= 0 || (schedule.WeeklyDaysBitmask & ~127) != 0)))
                    throw new InvalidDataException("The approved backup contains an invalid schedule.");
            }
        }
        return a;
    }

    /// <summary>The digest of the configuration exactly as it was approved.
    /// Serializing it again would include settings added by a later version
    /// of Backspace with their defaults, and no approval made before an update
    /// would verify any more (found on 31 October 2026 when a backup setting
    /// was added).</summary>
    internal static string SavedDigest(ReadOnlySpan<byte> approvalBytes)
    {
        using var document = JsonDocument.Parse(approvalBytes.ToArray());
        if (!document.RootElement.TryGetProperty("configuration", out var configuration) || configuration.ValueKind != JsonValueKind.Object)
            throw new SecurityException("The service approval is invalid or belongs to another account.");
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(configuration.GetRawText())));
    }

    internal static string Digest(AppConfig configuration) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(configuration, Json)));

    internal static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes);
        var objects = new System.Collections.Generic.Stack<System.Collections.Generic.HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!))
                throw new InvalidDataException("Duplicate JSON properties are not permitted in service requests or approvals.");
        }
    }
}
