using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Serilog;

namespace Backspace.Services;

/// <summary>
/// Removes only snapshots the engine identifies as belonging to a configured
/// backup/source. All providers use the same engine path. Repository folders,
/// unrelated files and snapshots from other tools are never deleted directly.
/// Callers hold the maintenance lease while this service works.
/// </summary>
public sealed class StorageCleaner
{
    private static ILogger _log => AppLogger.For<StorageCleaner>();
    private readonly ConfigStore _config;
    private readonly SecretsStore _secrets;
    private readonly IBackupEngine _engine;

    public StorageCleaner(ConfigStore config, SecretsStore secrets, IBackupEngine engine)
    {
        _config = config;
        _secrets = secrets;
        _engine = engine;
    }

    /// <summary>
    /// Derive the snapshot id that identifies a (Backup, sourcePath)
    /// pair on a storage. StorageCleaner is the only caller; the
    /// helper lives here rather than in a one-method utility class.
    /// </summary>
    internal static string SnapshotIdFor(Backup backup, string sourcePath)
    {
        var seed = !string.IsNullOrEmpty(backup.SnapshotIdSeed)
            ? backup.SnapshotIdSeed
            : backup.Name;
        var name = SanitizeSnapshotName(seed);
        if (backup.SourcePaths.Count <= 1) return name;
        return $"{name}-{HashSourcePath(sourcePath)}";
    }

    private static string HashSourcePath(string sourcePath)
    {
        var normalised = (sourcePath ?? "").TrimEnd('\\', '/');
        var bytes = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalised));
        var sb = new System.Text.StringBuilder(6);
        for (int i = 0; i < 3; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    private static string SanitizeSnapshotName(string s)
    {
        if (string.IsNullOrEmpty(s)) return "_";
        var buf = new char[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            bool ok = (c >= 'A' && c <= 'Z')
                   || (c >= 'a' && c <= 'z')
                   || (c >= '0' && c <= '9')
                   || c == '-' || c == '_';
            buf[i] = ok ? c : '_';
        }
        return new string(buf);
    }

    /// <summary>Probe used to enumerate snapshot ids on remote storages so
    /// the wipe path can iterate them. Set by ServiceLocator after both
    /// services are constructed to avoid a circular dependency at init.</summary>
    public DestinationProbe? Probe { get; set; }

    /// <summary>
    /// Activity-log sink. When set, each Erase / Wipe operation:
    ///   • opens a per-run log file under
    ///     <see cref="AppPaths.LogDirForBackup"/> for the relevant
    ///     bucket (real backup name, or the <c>[Destinations]</c>
    ///     sentinel for destination-wide wipes);
    ///   • captures every line of engine stdout/stderr the
    ///     <c>ResticBackrestEngine</c> emits while the operation
    ///     runs;
    ///   • appends a structured <see cref="RunRecord"/> to the
    ///     store on completion so the Activity &amp; Logs view's RUN
    ///     dropdown surfaces the entry alongside backup / restore
    ///     runs. Null = no log emission (used by tests).
    /// </summary>
    public LogStore? LogStore { get; set; }

    /// <summary>
    /// Bucket name used in the activity log when a wipe is for a
    /// whole destination (i.e. not tied to a specific backup). The
    /// user-facing string keeps the literal square brackets so it
    /// sorts and reads as obviously synthetic in the backup dropdown.
    /// </summary>
    public const string DestinationsBucketName = "[Destinations]";

    /// <summary>
    /// Fan-out IProgress: forwards every report to both inner
    /// channels. Used so erase/wipe progress text reaches both the
    /// user-facing modal AND the persisted activity log without
    /// having to call .Report twice at every emission site.
    /// </summary>
    private sealed class ChainedProgress<T> : IProgress<T>
    {
        private readonly IProgress<T> _a;
        private readonly IProgress<T> _b;
        public ChainedProgress(IProgress<T> a, IProgress<T> b) { _a = a; _b = b; }
        public void Report(T value)
        {
            try { _a.Report(value); } catch { }
            try { _b.Report(value); } catch { }
        }
    }

    /// <summary>
    /// One Erase / Wipe operation's transcript on disk. Hooks
    /// <c>ResticBackrestEngine.LineWritten</c> so every line of
    /// engine stdout/stderr lands in the log alongside our own
    /// status messages. Disposing detaches the hook and finalises the
    /// log header — the caller then constructs a
    /// <see cref="RunRecord"/> pointing at <see cref="Path"/> and
    /// hands it to <see cref="LogStore"/>.
    /// </summary>
    private sealed class RunLogSession : IDisposable
    {
        // _engine is nullable: unit tests construct StorageCleaner with
        // engine:null! for the local-path code (which never shells out)
        // — see StorageCleanerTests. The restic-output hook is just
        // skipped in that case; our own Status() writes still land.
        private readonly IBackupEngine? _engine;
        private readonly System.IO.StreamWriter _writer;
        private readonly Action<string>? _hook;
        private readonly object _gate = new();

        public string Path { get; }
        public DateTime StartedUtc { get; }

        public RunLogSession(IBackupEngine? engine, string bucketName, string headline)
        {
            _engine = engine;
            StartedUtc = DateTime.UtcNow;
            var dir = AppPaths.LogDirForBackup(bucketName);
            System.IO.Directory.CreateDirectory(dir);
            // Filename schema: UTC + milliseconds + 6-char random —
            // so concurrent operations and DST fall-back can't collide.
            var stamp = StartedUtc.ToString("yyyyMMddHHmmssfff");
            var rand = Guid.NewGuid().ToString("N").Substring(0, 6);
            Path = System.IO.Path.Combine(dir, $"{stamp}-erase-{rand}.log");
            // No-BOM UTF-8 — Encoding.UTF8 emits a BOM which renders as
            // garbage ("") at the head of the log when viewed with
            // grep / tail / non-BOM-aware tools.
            _writer = new System.IO.StreamWriter(Path, append: false,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            WriteLine($"=== {headline} ===");
            WriteLine($"Started:  {StartedUtc:O} UTC");
            if (_engine is not null)
            {
                _hook = line => WriteLine(line);
                _engine.LineWritten += _hook;
            }
        }

        public void Status(string message) => WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

        private void WriteLine(string line)
        {
            try
            {
                lock (_gate)
                {
                    _writer.WriteLine(line);
                    _writer.Flush();
                }
            }
            catch
            {
                // Best-effort: a failing log write must NEVER abort the
                // erase. We swallow and continue.
            }
        }

        public void Dispose()
        {
            try { if (_engine is not null && _hook is not null) _engine.LineWritten -= _hook; } catch { }
            try
            {
                lock (_gate)
                {
                    _writer.WriteLine($"Ended:    {DateTime.UtcNow:O} UTC");
                    _writer.Flush();
                    _writer.Dispose();
                }
            }
            catch (Exception ex)
            {
                // Disk-full or handle-leak during the final flush —
                // log so the operator can see the close failed, even
                // though the surrounding RunRecord may still report
                // success on the operation's intent.
                _log.Warning(ex, "Failed to finalise erase log at {Path}", Path);
            }
        }
    }

    /// <summary>
    /// Persist a <see cref="RunRecord"/> for an Erase / Wipe operation
    /// so the Activity &amp; Logs view's RUN dropdown surfaces it. No-op
    /// if <see cref="LogStore"/> isn't wired (test fixtures).
    /// </summary>
    private void PublishRunRecord(
        string bucketName, string bucketId, RunLogSession session,
        CleanupReport report, string summary)
    {
        if (LogStore is null) return;
        var status = report.Errors.Count > 0 ? BackupRunStatus.Warning : BackupRunStatus.Success;
        var record = new RunRecord
        {
            BackupId = bucketId,
            BackupName = bucketName,
            Kind = RunKind.Erase,
            StartedUtc = session.StartedUtc,
            EndedUtc = DateTime.UtcNow,
            Status = status,
            Summary = summary,
            LogPath = session.Path,
            Errors = new List<string>(report.Errors),
        };
        try { LogStore.AppendRun(record); }
        catch (Exception ex) { _log.Warning(ex, "Failed to append Erase RunRecord to log store"); }
    }

    /// <summary>
    /// Removes the data Backspace wrote for <paramref name="backup"/>
    /// from <paramref name="destination"/>. Other backups sharing the
    /// same storage root are untouched.
    /// </summary>
    public async Task<CleanupReport> EraseBackupFromDestinationAsync(
        Backup backup, Destination destination, string storageName, CancellationToken ct,
        IProgress<string>? progress = null,
        IProgress<int?>? chunkPercent = null)
    {
        var report = new CleanupReport(destination.Name);
        using var session = new RunLogSession(_engine, backup.Name,
            $"Erase backup «{backup.Name}» from destination «{destination.Name}»");
        // Bridge progress text to the activity log so the persisted
        // transcript has the same human narrative the modal showed.
        var teeProgress = progress is null
            ? (IProgress<string>)new Progress<string>(session.Status)
            : new ChainedProgress<string>(progress, new Progress<string>(session.Status));

        // Serialise cleanup calls for this destination. The caller's
        // maintenance lease coordinates cleanup with backups and restores.
        teeProgress.Report("Waiting for the destination to be free…");
        var gate = _wipeGates.GetOrAdd(destination.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await EraseSnapshotsViaEngineAsync(backup, destination, storageName, report, ct, teeProgress, chunkPercent);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Erase-backup failed at destination {Name}", destination.Name);
            report.Errors.Add(ex.Message);
        }
        finally
        {
            gate.Release();
        }
        teeProgress.Report("Done.");
        PublishRunRecord(
            bucketName: backup.Name, bucketId: backup.Id,
            session, report,
            summary: $"Erased «{backup.Name}» from «{destination.Name}»: "
                    + $"{report.DeletedSnapshotIds.Count} snapshot id(s) removed"
                    + (report.Errors.Count > 0 ? $", {report.Errors.Count} error(s)" : ""));
        return report;
    }

    /// <summary>
    /// Removes snapshots owned by configured backups at this destination.
    /// Leaves repository settings, unrelated files and other snapshots intact.
    /// </summary>
    public async Task<CleanupReport> WipeEntireDestinationAsync(
        Destination destination, CancellationToken ct,
        IProgress<string>? progress = null,
        IProgress<int?>? chunkPercent = null)
    {
        // Whole-destination wipes don't belong to a single backup, so
        // they log into the [Destinations] bucket — that name shows
        // up in the Activity & Logs backup dropdown as a synthetic
        // entry holding every destination-delete operation.
        using var session = new RunLogSession(_engine, DestinationsBucketName,
            $"Delete destination «{destination.Name}» ({destination.Kind})");
        var teeProgress = progress is null
            ? (IProgress<string>)new Progress<string>(session.Status)
            : new ChainedProgress<string>(progress, new Progress<string>(session.Status));

        // Per-destination semaphore. Two concurrent wipes against the same
        // destination would race in `Directory.Delete` (one sees the dir
        // disappear mid-iteration) for local kinds, and would race
        // restic prune commands against the same storage URL for cloud
        // kinds — risking metadata corruption either way. Serialize them.
        teeProgress.Report("Waiting for the destination to be free…");
        var gate = _wipeGates.GetOrAdd(destination.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var report = new CleanupReport(destination.Name);
            try
            {
                // Capture the configured ownership before the UI detaches this
                // destination. Never guess ownership from folder names or labels.
                var owners = _config.Current.Backups
                    .SelectMany(b => b.Targets.Where(t => t.DestinationId == destination.Id)
                        .Select(t => (Backup: b, Storage: t.StorageName)))
                    .DistinctBy(x => (x.Backup.Id, x.Storage)).ToArray();
                foreach (var owner in owners)
                {
                    ct.ThrowIfCancellationRequested();
                    await EraseSnapshotsViaEngineAsync(owner.Backup, destination, owner.Storage,
                        report, ct, teeProgress, chunkPercent);
                }
                report.Notes.Add(owners.Length == 0
                    ? "No configured backup owns snapshots here. Existing files and snapshots were left untouched."
                    : "Only snapshots belonging to the configured backups were removed. Other snapshots and unrelated files were preserved. The reusable repository folder and its settings remain.");
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Wipe-destination failed for {Name}", destination.Name);
                report.Errors.Add(ex.Message);
            }
            teeProgress.Report("Done.");
            PublishRunRecord(
                bucketName: DestinationsBucketName, bucketId: "",
                session, report,
                summary: $"Deleted destination «{destination.Name}»: "
                        + $"{report.DeletedSnapshotIds.Count} snapshot id(s) removed"
                        + (report.Errors.Count > 0 ? $", {report.Errors.Count} error(s)" : ""));
            return report;
        }
        finally
        {
            gate.Release();
        }
    }

    // Instance-scoped (was static) so test fixtures that construct their
    // own StorageCleaner don't share gate state across the suite, and so
    // a process-lifetime singleton's gate dictionary can be cleaned up
    // when the cleaner itself is replaced (e.g. service-locator reinit
    // in tests). The semantics that matter — serialising concurrent
    // operations against the SAME destination — still hold because
    // production wires StorageCleaner as a singleton via ServiceLocator.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _wipeGates = new();

    /// <summary>
    /// Drop the per-destination wipe gate. Called by
    /// <see cref="ViewModels.DestinationsViewModel"/> after a successful
    /// destination removal so the dictionary doesn't grow unboundedly
    /// across the lifetime of a long-running app instance.
    /// Safe to call for destinations that never had a gate created.
    /// </summary>
    public void ForgetWipeGate(string destinationId)
    {
        // Just remove from the dict and let the GC reclaim the
        // SemaphoreSlim once all holders' local references go out of
        // scope. We deliberately do NOT Dispose, even when
        // CurrentCount==1 looks safe — there's a window between
        // EraseBackupFromDestinationAsync's `_wipeGates.GetOrAdd(...)`
        // returning and its `await gate.WaitAsync(ct)` actually
        // taking the count down to 0. ForgetWipeGate firing in that
        // window observes CurrentCount==1, disposes, and the holder's
        // WaitAsync then throws ObjectDisposedException. The earlier
        // "atomic-remove-first then check count" mitigated one shape
        // of the bug but couldn't cover the GetOrAdd-vs-WaitAsync
        // gap. Skipping Dispose is the only correct call: the
        // SemaphoreSlim is small (~50 bytes), and ForgetWipeGate is
        // only fired on destination removal — a manual user action,
        // not a hot path. The dictionary entry IS removed, which is
        // the actual leak we care about (otherwise the dict grows
        // unboundedly across the lifetime of a long-running app).
        _wipeGates.TryRemove(destinationId, out _);
    }

    private async Task EraseSnapshotsViaEngineAsync(
        Backup backup, Destination destination, string storageName, CleanupReport report,
        CancellationToken ct, IProgress<string>? progress, IProgress<int?>? percent)
    {
        foreach (var source in backup.SourcePaths.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Checking stored snapshots for «{backup.Name}»…");
            await _engine.EnsureInitializedAsync(backup, source, destination, storageName, ct);
            // A listing failure must remain a failure, never 'already empty'.
            var snapshots = await _engine.ListSnapshotsAsync(backup, source, destination, storageName, ct);
            if (snapshots.Any(s => string.IsNullOrWhiteSpace(s.SnapshotId)))
                throw new InvalidOperationException("The engine did not identify the stored snapshots. Nothing was removed for this source.");
            if (snapshots.Count == 0)
            {
                report.Notes.Add($"No matching snapshots for {source}; nothing to remove.");
                continue;
            }
            var done = 0;
            foreach (var snapshot in snapshots)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Removing snapshot {done + 1} of {snapshots.Count} for «{backup.Name}»…");
                await _engine.PruneSnapshotIdAsync(backup, source, destination, storageName, snapshot.SnapshotId!, ct);
                report.DeletedSnapshotIds.Add(snapshot.SnapshotId!);
                percent?.Report(++done * 90 / snapshots.Count);
            }
            progress?.Report("Freeing space no remaining snapshot needs…");
            await _engine.PruneExhaustiveAsync(backup, source, destination, storageName, ct);
            percent?.Report(100);
        }
    }

    public static string? TryFormatPruneProgressLineForUi(string line) =>
        TryFormatPruneProgressLine(line);

    private static string? TryFormatPruneProgressLine(string line) =>
        TryFormatPruneProgressLineWithPercent(line).Status;

    /// <summary>
    /// Parse a single line of restic prune stdout, returning
    /// (a human status, an optional percent-of-current-snapshot).
    /// The percent is only set for "Deleted X of Y chunks" lines —
    /// restic emits these one per chunk, so they're the only
    /// data point that maps to a real percentage. Other recognised
    /// lines (fossil marking, removed-snapshot acks) update the
    /// status text but return null for percent, since they don't
    /// carry a total.
    /// </summary>
    private static (string? Status, int? Percent) TryFormatPruneProgressLineWithPercent(string line)
    {
        if (string.IsNullOrEmpty(line)) return (null, null);
        var m = PruneDeletedRx.Match(line);
        if (m.Success)
        {
            var done = int.Parse(m.Groups[1].Value);
            var total = int.Parse(m.Groups[2].Value);
            int? pct = total > 0
                ? (int)Math.Clamp(Math.Round(done * 100.0 / total), 0, 100)
                : null;
            return ($"Deleted {done} of {total} chunks…", pct);
        }
        m = PruneFossilRx.Match(line);
        if (m.Success)
            return ($"Marked {m.Groups[1].Value} chunks as fossils…", null);
        m = PruneRemovedSnapshotRx.Match(line);
        if (m.Success)
            return ($"Removed revision {m.Groups[1].Value}…", null);
        return (null, null);
    }

    private static readonly System.Text.RegularExpressions.Regex PruneDeletedRx = new(
        @"Deleted\s+(\d+)\s+of\s+(\d+)\s+chunks?",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex PruneFossilRx = new(
        @"Marked\s+(\d+)\s+(?:chunks?|fossils?)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex PruneRemovedSnapshotRx = new(
        @"(?:Removed|Deleted)\s+snapshot\s+\S+\s+(?:at\s+)?revision\s+(\d+)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);


}

/// <summary>
/// Summary of what a cleanup attempt did. Reported to the diagnostic
/// log; not surfaced to the user as a notification — by user direction
/// the app should always finish the job, not delegate to manual cleanup.
/// </summary>
public sealed class CleanupReport
{
    public string DestinationName { get; }
    public List<string> DeletedSnapshotIds { get; } = new();
    public string? WipedPath { get; set; }
    public List<string> Notes { get; } = new();
    public List<string> Errors { get; } = new();

    public CleanupReport(string destinationName) => DestinationName = destinationName;

    public string Summarize()
    {
        var sb = new StringBuilder();
        sb.Append("At «").Append(DestinationName).Append("»: ");
        if (WipedPath is not null)
            sb.Append("wiped ").Append(WipedPath).Append(". ");
        if (DeletedSnapshotIds.Count > 0)
            sb.Append("removed snapshot chains: ").Append(string.Join(", ", DeletedSnapshotIds)).Append(". ");
        foreach (var n in Notes) sb.Append(n).Append(' ');
        foreach (var e in Errors) sb.Append("Error: ").Append(e).Append(' ');
        return sb.ToString().TrimEnd();
    }
}
