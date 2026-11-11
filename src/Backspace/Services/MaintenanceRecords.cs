using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Backspace.Services;

/// <summary>
/// Backspace's own memory of repository maintenance (U175), read before the
/// cleanup after a backup: when each repository last had a full prune, and
/// whether each plan's previous backup finished. Backrest's operation history
/// answered both before; each Backrest instance (the app's, each background
/// worker's) kept its own, so the app and the service could disagree.
///
/// <para>Kept in <c>maintenance.json</c> in the configuration folder: the
/// app writes <c>Backspace.config\maintenance.json</c>, the background
/// service <c>Backspace.config\service\maintenance.json</c>, and both read
/// both, like the run history. Times are UTC:</para>
/// <code>
/// { "version": 1,
///   "repositories": { "&lt;repository id&gt;": { "lastFullPruneUtc": "2026-11-10T06:13:11Z" } },
///   "plans": { "&lt;plan id&gt;": { "lastBackupStartedUtc": "…", "lastBackupFinishedUtc": "…", "lastSnapshotId": "…" } } }
/// </code>
/// The repository id is <see cref="ResticBackrestEngine.MakeRepoId"/> (the
/// destination's id and storage name), the plan id
/// <see cref="ResticBackrestEngine.MakePlanId"/>. A record that cannot be
/// written leaves the backup alone: at worst one prune more or less.
/// </summary>
internal static class MaintenanceRecords
{
    internal const string FileName = "maintenance.json";

    /// <summary>This process's own file.</summary>
    internal static string OwnFile => Path.Combine(AppPaths.ConfigRoot, FileName);

    /// <summary>Every file read: this process's, and for this copy's portable
    /// folder the app's and the background service's.</summary>
    internal static IReadOnlyList<string> Files()
    {
        var files = new List<string> { OwnFile };
        if (EngineWorkPaths.IsPortableConfig())
        {
            var portable = Path.Combine(AppPaths.ExeDirectory, AppPaths.AppFolderName);
            files.Add(Path.Combine(portable, FileName));
            files.Add(Path.Combine(portable, "service", FileName));
        }
        return files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>When a full prune last succeeded in this repository, or null.</summary>
    internal static DateTime? LastFullPrune(string repoId, CancellationToken ct = default)
        => ReadAll(ct).Select(f => f.Repositories.TryGetValue(repoId, out var r) ? r.LastFullPruneUtc : null).Max();

    /// <summary>True when this plan's latest backup started and did not
    /// finish (stopped, failed, or the computer or Backspace went down): it
    /// may have left data behind that only a full prune removes.</summary>
    internal static bool PreviousBackupUnfinished(string planId, CancellationToken ct = default)
    {
        var plans = ReadAll(ct).Select(f => f.Plans.TryGetValue(planId, out var p) ? p : null).Where(p => p is not null).ToList();
        var started = plans.Select(p => p!.LastBackupStartedUtc).Max();
        var finished = plans.Select(p => p!.LastBackupFinishedUtc).Max();
        return started is { } s && (finished is not { } f || f < s);
    }

    /// <summary>The snapshot this plan's latest finished backup made, as
    /// remembered (owner's answer of 10 November 2026 to U175's question 4):
    /// the next backup's parent once restic confirms it is still this plan's
    /// snapshot. Null when none is remembered.</summary>
    internal static string? RememberedSnapshot(string planId, CancellationToken ct = default)
        => ReadAll(ct).Select(f => f.Plans.TryGetValue(planId, out var p) ? p : null)
            .Where(p => p?.LastBackupFinishedUtc is not null && ResticBackrestEngine.IsValidBackrestGuid(p.LastSnapshotId))
            .MaxBy(p => p!.LastBackupFinishedUtc)?.LastSnapshotId;

    internal static void BackupStarted(string planId, DateTime utc, CancellationToken ct = default)
        => Update(file => Plan(file, planId).LastBackupStartedUtc = utc, ct);

    /// <summary>The backup finished and made <paramref name="snapshotId"/>.</summary>
    internal static void BackupFinished(string planId, DateTime utc, string? snapshotId, CancellationToken ct = default)
        => Update(file =>
        {
            var plan = Plan(file, planId);
            plan.LastBackupFinishedUtc = utc;
            plan.LastSnapshotId = snapshotId;
        }, ct);

    internal static void FullPruneDone(string repoId, DateTime utc, CancellationToken ct = default)
        => Update(file =>
        {
            if (!file.Repositories.TryGetValue(repoId, out var repository)) file.Repositories[repoId] = repository = new();
            repository.LastFullPruneUtc = utc;
        }, ct);

    private static PlanRecord Plan(Record file, string planId)
    {
        if (!file.Plans.TryGetValue(planId, out var plan)) file.Plans[planId] = plan = new();
        return plan;
    }

    private static IEnumerable<Record> ReadAll(CancellationToken ct)
    {
        foreach (var path in Files())
        {
            Record? record = null;
            try { record = Read(path, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                AppLogger.For(nameof(MaintenanceRecords)).Warning(ex, "Could not read {Path}", path);
            }
            if (record is not null) yield return record;
        }
    }

    private static Record? Read(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        // Another program holding the file is waited out (U135); a file that
        // is not a record is reported, never taken as empty.
        var text = FileAccessRetry.Get(() => File.ReadAllText(path), path, ct: ct);
        return JsonSerializer.Deserialize<Record>(text, Json) ?? throw new JsonException("The record is empty.");
    }

    /// <summary>Changes this process's file in one transaction across
    /// processes (several background workers write the same one). Never
    /// throws but for cancellation: a record is not worth a backup.</summary>
    private static void Update(Action<Record> change, CancellationToken ct)
    {
        var path = OwnFile;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var transaction = StateFileLock.Acquire(path);
            Record record;
            try { record = Read(path, ct) ?? new Record(); }
            catch (JsonException ex)
            {
                // Kept aside, never overwritten in place.
                var aside = path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
                AppLogger.For(nameof(MaintenanceRecords)).Warning(ex, "{Path} could not be read; kept as {Aside}", path, aside);
                FileAccessRetry.Run(() => File.Move(path, aside), path, ct: ct);
                record = new Record();
            }
            change(record);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(record, Json));
                FileAccessRetry.Run(() => File.Move(temporary, path, overwrite: true), path, ct: ct);
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLogger.For(nameof(MaintenanceRecords)).Warning(ex, "Could not update {Path}", path);
        }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal sealed class Record
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("repositories")] public Dictionary<string, RepositoryRecord> Repositories { get; set; } = new(StringComparer.Ordinal);
        [JsonPropertyName("plans")] public Dictionary<string, PlanRecord> Plans { get; set; } = new(StringComparer.Ordinal);
        [JsonExtensionData] public Dictionary<string, JsonElement>? Other { get; set; }
    }

    internal sealed class RepositoryRecord
    {
        [JsonPropertyName("lastFullPruneUtc")] public DateTime? LastFullPruneUtc { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? Other { get; set; }
    }

    internal sealed class PlanRecord
    {
        [JsonPropertyName("lastBackupStartedUtc")] public DateTime? LastBackupStartedUtc { get; set; }
        [JsonPropertyName("lastBackupFinishedUtc")] public DateTime? LastBackupFinishedUtc { get; set; }
        [JsonPropertyName("lastSnapshotId")] public string? LastSnapshotId { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? Other { get; set; }
    }
}

/// <summary>
/// The repositories this computer has opened or created for backups (U175):
/// a backup to one of them goes straight to restic, without asking it first
/// whether the repository exists (on a cloud destination that costs a restic
/// start and several round trips). Backrest's configuration played this part:
/// it ran <c>restic init</c> (or adopted an existing repository) only for a
/// destination it did not know. A repository that disappears later is reported
/// by restic as missing and is never created again in its place.
///
/// <para>A reproducible cache in this computer's working folder
/// (<see cref="EngineWorkPaths"/>, <c>repositories.json</c>): when it is
/// missing, the first backup to each destination asks restic again
/// (<c>restic cat config</c>, and <c>restic init</c> only when restic says
/// there is no repository).</para>
/// </summary>
internal static class KnownRepositories
{
    internal static string FilePath => Path.Combine(EngineWorkPaths.Root, "repositories.json");

    internal static bool Knows(string repoId, CancellationToken ct = default)
    {
        try { return Read(FilePath, ct).Repositories.ContainsKey(repoId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLogger.For(nameof(KnownRepositories)).Warning(ex, "Could not read {Path}; the repository is checked again", FilePath);
            return false;
        }
    }

    /// <summary>restic's id for a repository this computer knows at
    /// <paramref name="uri"/> (what Backrest called its guid), or null.</summary>
    internal static string? ResticId(string repoId, string uri, CancellationToken ct = default)
    {
        try
        {
            return Read(FilePath, ct).Repositories.TryGetValue(repoId, out var entry)
                && string.Equals(entry.Uri, uri, StringComparison.Ordinal)
                && ResticBackrestEngine.IsValidBackrestGuid(entry.Id) ? entry.Id : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLogger.For(nameof(KnownRepositories)).Warning(ex, "Could not read {Path}", FilePath);
            return null;
        }
    }

    /// <summary>Remembers that the repository exists here (with restic's id
    /// for it, when known).</summary>
    internal static void Remember(string repoId, string? resticId, string uri, CancellationToken ct = default)
    {
        var path = FilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var transaction = StateFileLock.Acquire(path);
            File cache;
            try { cache = Read(path, ct); }
            catch (JsonException) { cache = new File(); } // a cache: rebuilt
            cache.Repositories[repoId] = new Entry { Id = resticId, Uri = uri, SeenUtc = DateTime.UtcNow };
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(cache, Json));
                FileAccessRetry.Run(() => System.IO.File.Move(temporary, path, overwrite: true), path, ct: ct);
            }
            finally { try { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); } catch (IOException) { } }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLogger.For(nameof(KnownRepositories)).Warning(ex, "Could not update {Path}", path);
        }
    }

    private static File Read(string path, CancellationToken ct)
    {
        if (!System.IO.File.Exists(path)) return new File();
        var text = FileAccessRetry.Get(() => System.IO.File.ReadAllText(path), path, ct: ct);
        return JsonSerializer.Deserialize<File>(text, Json) ?? new File();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed class File
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("repositories")] public Dictionary<string, Entry> Repositories { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class Entry
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("uri")] public string Uri { get; set; } = "";
        [JsonPropertyName("seenUtc")] public DateTime SeenUtc { get; set; }
    }
}
