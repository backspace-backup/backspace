using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Serilog;

namespace Backspace.Services;

/// <summary>
/// "Test restore" drill — the user-facing answer to "is this backup
/// actually restorable?". Restores the saved sample or every file, including
/// empty files, from each source at every destination and (when possible)
/// byte-compares each one against the live source. The public legacy entry
/// point without a context performs a full check.
///
/// Why this exists: a backup that succeeds for six months but produces
/// unrestorable bytes is the silent-killer scenario for any backup
/// product. "Run succeeded" alone isn't a trustworthy signal — the
/// only real signal is "we've actually pulled bytes back and verified
/// them." This drill turns that into a button.
///
/// Source-state handling. Sources change all the time, so a strict
/// "byte equal current source" check would be a noisy false-fail
/// machine. The drill is conservative:
///   * Restored bytes vs snapshot's recorded size — always asserted.
///     A mismatch means recovery did not produce the expected file.
///   * Restored bytes vs current source bytes — only when the source
///     file's size and mtime match the recorded per-file metadata. A
///     different date in either direction disqualifies comparison; we report
///     <see cref="FileVerifyStatus.OkNoCompare"/> in that case.
///
/// Cadence. The Test restore button and the optional automatic restore
/// comparison use the same service. Automatic comparison is off by default
/// because it consumes bandwidth. The card's due status follows the user's
/// configured repository-check schedule.
/// </summary>
public sealed class BackupVerifier
{
    /// <summary>Legacy sample size retained for compatibility with older scenario helpers.</summary>
    public const int FilesToSample = 10;

    /// <summary>
    /// One-shot startup sweep of <c>ConfigRoot/verify/*</c>. Each
    /// verify drill creates a subdirectory and tries to delete it in
    /// the run's finally block, but if anti-virus or another process
    /// holds a transient handle on a restored file the recursive
    /// delete fails and the dir is orphaned. Without this sweep the
    /// portable config folder grows unbounded across launches — a
    /// real exposure for a backup app whose users may copy
    /// Backspace.config between machines. Best-effort: a sweep
    /// failure is logged at Debug and ignored.
    /// </summary>
    /// <summary>Heartbeat sentinel filename written inside an active
    /// verify scratch dir. The active drill <c>touches</c> this file
    /// every <see cref="HeartbeatInterval"/> so the startup sweep can
    /// safely distinguish "in-flight drill that's been running for
    /// 32 hours over a slow cloud" from "orphan dir from a crashed
    /// drill 5 days ago". Without the heartbeat the sweep had to
    /// trust mtime alone — fine for backups under 24h but unsafe for
    /// the multi-day cloud verifies the app needs to support.</summary>
    internal const string HeartbeatFileName = ".backspace-verify-heartbeat";

    /// <summary>Cadence at which the active verify drill touches its
    /// heartbeat sentinel. Set comfortably below the sweep's "dead"
    /// cutoff so a healthy drill never crosses the threshold even on
    /// a heavily-loaded machine.</summary>
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(5);

    /// <summary>How long without a heartbeat update before the sweep
    /// considers the drill dead and the scratch dir eligible for
    /// deletion. 30 minutes is ~6× HeartbeatInterval — generous enough
    /// to absorb a Defender scan stall, tight enough to clean up after
    /// a crashed unattended drill on the next launch.</summary>
    internal static readonly TimeSpan HeartbeatDeadCutoff = TimeSpan.FromMinutes(30);

    public static void SweepStaleScratchDirs()
    {
        var root = Path.Combine(AppPaths.ConfigRoot, "verify");
        if (!Directory.Exists(root)) return;
        // Heartbeat-aware sweep. The 24-hour mtime cutoff that was
        // here before code-review couldn't distinguish a slow cloud
        // drill that's been running for 32h from an orphaned crashed
        // dir, and the user wants backups (and verifies) to support
        // 24h+ runs. The replacement contract: every active drill
        // writes a HeartbeatFileName sentinel and touches it every
        // HeartbeatInterval. The sweep deletes a scratch dir IFF its
        // heartbeat is missing OR older than HeartbeatDeadCutoff;
        // fresh heartbeats keep the dir alive indefinitely. Falls
        // back to the dir's own mtime when the sentinel is missing
        // entirely (an older drill that pre-dates this code OR a
        // crashed drill that died before writing the sentinel) —
        // 1 hour for that case, the original "fresh enough to be
        // suspicious" cutoff.
        int deleted = 0, kept = 0, skippedYoung = 0;
        foreach (var dir in Directory.EnumerateDirectories(root).ToList())
        {
            try
            {
                if (IsScratchDirAlive(dir))
                {
                    skippedYoung++;
                    continue;
                }
                // An active owner keeps this handle even through sleep or a
                // debugger pause. Heartbeat age alone cannot prove death.
                using (var owner = new FileStream(Path.Combine(dir, ".owner.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None)) { }
                // An unacknowledged daemon may still be writing staging data.
                if (Directory.EnumerateDirectories(dir, ".backspace-restore-*", SearchOption.TopDirectoryOnly).Any())
                {
                    kept++;
                    continue;
                }
                Directory.Delete(dir, recursive: true);
                deleted++;
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "Couldn't sweep stale verify dir {Dir}", dir);
                kept++;
            }
        }
        if (deleted > 0 || kept > 0 || skippedYoung > 0)
            _log.Information("Verify scratch sweep: deleted {Deleted}, kept {Kept}, skipped-young {SkippedYoung}",
                deleted, kept, skippedYoung);
    }

    /// <summary>True iff <paramref name="dir"/> looks like an in-flight
    /// drill that the sweep shouldn't touch. Heartbeat file present and
    /// fresh → alive. Heartbeat file missing → fall back to mtime (legacy
    /// drills, drills that crashed before writing the sentinel) with
    /// a 1-hour grace window so we never delete a dir written to less
    /// than an hour ago.</summary>
    private static bool IsScratchDirAlive(string dir)
    {
        var heartbeat = Path.Combine(dir, HeartbeatFileName);
        if (File.Exists(heartbeat))
        {
            // Heartbeat-driven path: alive iff the file was touched
            // within HeartbeatDeadCutoff of now. Sweep would never
            // delete a healthy 32h cloud drill.
            return (DateTime.UtcNow - File.GetLastWriteTimeUtc(heartbeat)) < HeartbeatDeadCutoff;
        }
        // Legacy / sentinel-missing fallback: trust mtime alone with
        // the ORIGINAL 24h grace window so we don't delete:
        //   • An upgrade-mid-drill scratch dir (pre-heartbeat code).
        //   • A drill whose synchronous sentinel write failed
        //     silently (disk full, AV quarantine racing
        //     CreateDirectory). Round-4 review caught that this
        //     branch isn't ONLY for upgrades — any drill whose
        //     sentinel can't be written hits here.
        // 24h is generous enough to absorb both cases without
        // letting orphans pile up.
        var info = new DirectoryInfo(dir);
        var fallback = DateTime.UtcNow - TimeSpan.FromHours(24);
        return info.LastWriteTimeUtc > fallback || info.CreationTimeUtc > fallback;
    }

    private static ILogger _log => AppLogger.For<BackupVerifier>();

    private readonly ConfigStore _config;
    private readonly RestoreEngine _restore;
    private readonly RevisionBrowser _revisions;
    private readonly IBackupEngine _engine;

    public BackupVerifier(ConfigStore config, RestoreEngine restore, RevisionBrowser revisions, IBackupEngine engine)
    {
        _config = config;
        _restore = restore;
        _revisions = revisions;
        _engine = engine;
    }

    /// <summary>
    /// Run a verify drill against EVERY reachable destination this
    /// backup writes to. A multi-destination backup gets one verify
    /// per destination; the per-destination outcomes are combined into
    /// a single <see cref="VerifyOutcome"/> with file results
    /// concatenated and the OverallPass flag set to false if any one
    /// destination's verify failed. Each destination is independently
    /// try/caught so a Dropbox token expiry can't stop the verify of
    /// the local destination running in the same drill.
    /// </summary>
    public Task<VerifyOutcome> VerifyAsync(Backup backup, CancellationToken ct) => VerifyAsync(backup, ct, null);

    internal async Task<VerifyOutcome> VerifyAsync(Backup backup, CancellationToken ct, TestRestoreContext? context)
    {
        if (context?.Control is not null) await context.Control.WaitWhilePausedAsync(ct);
        // Maintenance gate (Erase / Wipe / Prune). Same as backup +
        // restore — test-restore drills also touch the storage chain
        // (read-only, but restic still acquires per-storage locks
        // we don't want to fight). Returns immediately when idle.
        // Cancellation is not a failed integrity check. The durable
        // test-restore service records an interrupted attempt separately
        // and leaves the last completed comparison result unchanged.
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ct = operationCts.Token;
        using var readLease = context?.MaintenanceLeaseHeld == true ? null
            : await ServiceLocator.Maintenance.AcquireReadAsync(ct, operationCts.Cancel);

        var started = context?.Journal.StartedUtc ?? DateTime.UtcNow;
        using var _syncing = AppStatus.BeginSyncing();

        // Register a synthetic "Running" RunRecord so the LogsView's
        // RUN dropdown shows the test-restore drill in flight (just
        // like backup runs surface via Orchestrator.TryGetRunningRecord).
        // The ID stays the same across the lifetime; AppendRun at end
        // re-uses it so the persisted entry IS the same row the user
        // clicked while it was in flight. The user reported: "Doing
        // a test restore shows in the Live view instead of as a
        // Running test restore run (in RUN)."
        var runRecord = new RunRecord
        {
            Id = context?.Journal.Id ?? Guid.NewGuid().ToString("N"),
            BackupId   = backup.Id,
            BackupName = backup.Name,
            Kind       = RunKind.TestRestore,
            StartedUtc = started,
            Status     = BackupRunStatus.Running,
            Summary    = "Running…",
        };
        ServiceLocator.Activity.Register(runRecord);

        // Capture every stdout line the engine emits during the
        // drill so the persisted log file contains the actual
        // restic output, not just the bullet-list summary
        // (writing only the summary made the terminal pane
        // replace the live log with that summary at end-of-run —
        // the user's exact complaint). The handler is hot-path
        // safe: append to a local StringBuilder, no allocation
        // per line beyond the one Append.
        var capturedLog = new System.Text.StringBuilder();
        Action<string> tee = ln =>
        {
            try { lock (capturedLog) capturedLog.AppendLine(ln); } catch { /* logging never fails the drill */ }
        };
        _engine.LineWritten += tee;

        // Tell the Activity &amp; logs view to flip into Live mode AND
        // pre-select the in-flight Running entry on this backup so
        // the user sees the test-restore stream as it happens.
        AppNavigator.SignalLiveActivity(backup.Name);

        try
        {
            return await VerifyCoreAsync(backup, runRecord, started, capturedLog, ct, context);
        }
        finally
        {
            // Symmetrical cleanup: always unsubscribe + unregister
            // even on early-return / cancellation / unexpected throw.
            _engine.LineWritten -= tee;
            ServiceLocator.Activity.Unregister(runRecord.Id);
        }
    }

    private async Task<VerifyOutcome> VerifyCoreAsync(
        Backup backup,
        RunRecord runRecord,
        DateTime started,
        System.Text.StringBuilder capturedLog,
        CancellationToken ct, TestRestoreContext? context)
    {
        var sourcePath = backup.SourcePaths.FirstOrDefault();
        if (string.IsNullOrEmpty(sourcePath))
            return Failed(backup, started, "Backup has no source paths configured.");

        var reachableDests = ResolveVerificationTargets(backup);
        if (reachableDests.Count == 0)
            return Failed(backup, started, "No destinations are configured for this backup.");

        if (context is not null) await context.PrepareAsync(backup, reachableDests, _revisions, ct);

        // Per-destination outcomes — collected as structured items so
        // the final Summary can group successes vs failures with bullet
        // lists (replaces the previous single ` · `-joined string the
        // user reported as unreadable). Each item carries enough
        // information to render the bullet line directly.
        var allFileResults = new List<FileVerifyResult>();
        var successItems = new List<string>(); // bullet body, e.g. "«Local» — rev 6 from 2026-04-29 09:15"
        var failedItems  = new List<string>(); // bullet body, e.g. "«Dropbox» — no snapshots yet"
        var perDestErrors = new List<string>();
        int destinationsOk = 0;
        int destinationsFailed = 0;
        int latestRevisionAcrossDests = 0;
        // Captured from the FIRST successful destination so the success
        // headline can read "10 files restored, 10 byte-identical (same
        // for all destinations normally)" once instead of repeating per
        // destination — multi-destination drills sample identical file
        // sets so the counts match.
        int? successFilesPicked = null;
        int? successFilesByteIdentical = null;
        int? successFilesSizeOnly = null;
        int successFilesRecovered = 0;

        foreach (var (dest, target, unavailable) in reachableDests)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (unavailable is not null) throw new IOException(unavailable);
                var (perDestResults, latest, latestCreatedUtc, err) = await VerifyOneDestinationAsync(
                    backup, sourcePath, dest, target, started, ct, context);
                allFileResults.AddRange(perDestResults);
                if (err is not null)
                {
                    destinationsFailed++;
                    perDestErrors.Add($"«{dest.Name}»: {err}");
                    failedItems.Add($"«{dest.Name}»: {err}");
                    continue;
                }
                latestRevisionAcrossDests = Math.Max(latestRevisionAcrossDests, latest);
                var failedHere = perDestResults.Count(r => r.Status is
                    FileVerifyStatus.SizeMismatch or
                    FileVerifyStatus.BytesMismatch or
                    FileVerifyStatus.RestoreFailed);
                // Surface the revision's local-time date in the summary
                // so the user knows WHEN the data they restored was
                // captured. "rev 7 from 2026-04-29 14:32" reads more
                // honestly than just "rev 7" — same backup at different
                // points in time is the whole point of revisions.
                var revStamp = $"rev {latest} from {latestCreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
                if (failedHere == 0)
                {
                    destinationsOk++;
                    var ok = perDestResults.Count(r => r.Status == FileVerifyStatus.OK);
                    var okNoCompare = perDestResults.Count(r => r.Status == FileVerifyStatus.OkNoCompare);
                    successItems.Add($"«{dest.Name}»: {revStamp}");
                    successFilesPicked = (successFilesPicked ?? 0) + perDestResults.Count;
                    successFilesByteIdentical = (successFilesByteIdentical ?? 0) + ok;
                    successFilesSizeOnly = (successFilesSizeOnly ?? 0) + okNoCompare;
                    successFilesRecovered += perDestResults.Count(r => r.Status == FileVerifyStatus.Recovered);
                }
                else
                {
                    destinationsFailed++;
                    var differences = perDestResults.Count(r => r.Status == FileVerifyStatus.BytesMismatch);
                    failedItems.Add(differences == failedHere
                        ? $"«{dest.Name}»: {revStamp} · Files recovered successfully, but {differences} differ from today's originals. This does not establish which copy changed."
                        : $"«{dest.Name}»: {revStamp} · {failedHere - differences} files could not be recovered correctly. {differences} additional files differ from today's originals.");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Defensive: any throw escaping VerifyOneDestinationAsync
                // (an entire async-thrown exception or somehow bypassed
                // catch upstream) is recorded against THIS destination
                // and the loop continues. Earlier this would propagate
                // and fail the whole drill, including any destinations
                // the user could have gotten OK results for — that's
                // the user's "Test restore crashed the app" report.
                _log.Error(ex, "Verify of destination {Name} threw", dest.Name);
                destinationsFailed++;
                perDestErrors.Add($"«{dest.Name}»: {ex.Message}");
                failedItems.Add($"«{dest.Name}»: {ex.Message}");
            }
        }

        // Whole-drill verdict: OK iff every destination's verify
        // passed. A backup with two destinations and one of them
        // broken is NOT a passing verify.
        var pass = destinationsFailed == 0 && destinationsOk > 0;
        var totalDests = reachableDests.Count;
        var overallSummary = BuildOverallSummary(
            totalDests, destinationsOk, destinationsFailed,
            successItems, failedItems,
            successFilesPicked ?? 0,
            successFilesByteIdentical ?? 0,
            successFilesSizeOnly ?? 0, successFilesRecovered);

        var outcome = new VerifyOutcome(
            BackupId: backup.Id,
            BackupName: backup.Name,
            StartedUtc: started,
            EndedUtc: DateTime.UtcNow,
            RevisionNumber: latestRevisionAcrossDests,
            FilesPicked: allFileResults.Count,
            FilesOk: allFileResults.Count(r => r.Status is FileVerifyStatus.OK or FileVerifyStatus.OkNoCompare or FileVerifyStatus.Recovered),
            FilesFailed: allFileResults.Count(r => r.Status is
                FileVerifyStatus.SizeMismatch or
                FileVerifyStatus.BytesMismatch or
                FileVerifyStatus.RestoreFailed),
            OverallPass: pass,
            Files: allFileResults,
            FatalError: perDestErrors.Count > 0 ? string.Join(" · ", perDestErrors) : null,
            Summary: context is null ? overallSummary : context.Journal.Options.Label + "\n" + overallSummary
                + (context.Journal.Options.Full ? "" : "\nThis checks a sample, not every file in the backup."))
            { IsSample = context?.Journal.Options.Full == false, IsScheduled = context?.Journal.Scheduled == true };

        // Persist a RunRecord so the test restore appears in the
        // Activity & logs RUN dropdown alongside backup and restore
        // entries — the user explicitly asked: "When doing a test
        // restore or a restore operation, it should show up the
        // related logs in Activity & logs". Best-effort: a failed
        // persist must never demote a successful drill.
        string logText;
        lock (capturedLog) logText = capturedLog.ToString();
        try { PersistTestRestoreRunRecord(backup, outcome, runRecord.Id, logText); }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to persist test-restore RunRecord for {Backup}", backup.Name);
        }

        return outcome;
    }

    /// <summary>
    /// Append a test-restore RunRecord to the backup's per-name
    /// history.json so the LogsView surfaces it. Writes the verifier's
    /// rendered <see cref="VerifyOutcome.Summary"/> as the log file
    /// (it's already the human-readable bullet-list form the
    /// notification uses, which is what the user wants to see in
    /// the terminal pane).
    /// </summary>
    private void PersistTestRestoreRunRecord(Backup backup, VerifyOutcome outcome, string runId, string capturedLog)
    {
        string? logPath = null;
        try
        {
            var dir = AppPaths.LogDirForBackup(backup.Name);
            System.IO.Directory.CreateDirectory(dir);
            logPath = LogStore.NewTranscriptPath(backup.Name, "test-restore", outcome.StartedUtc);
            // Persist the actual streamed restic output, then a
            // human-readable summary footer. Earlier the code wrote
            // ONLY the summary, so the LogsView's terminal pane
            // replaced the live log with that summary at end-of-run
            // — the user reported: "at the end the whole log
            // disappears and is replaced by the summary".
            var sb = new System.Text.StringBuilder();
            sb.Append(capturedLog ?? "");
            if (!string.IsNullOrWhiteSpace(outcome.Summary))
            {
                if (sb.Length > 0 && sb[^1] != '\n') sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("=== Summary ===");
                sb.Append(outcome.Summary);
            }
            sb.AppendLine();
            sb.AppendLine("=== Every file checked ===");
            foreach (var file in outcome.Files)
                sb.AppendLine($"[{file.Status}] {file.DestinationName} / {file.SourcePath} / {file.Path} — {file.ActualBytes:N0}/{file.ExpectedBytes:N0} bytes{(file.Detail is null ? "" : ": " + file.Detail)}");
            System.IO.File.WriteAllText(logPath, sb.ToString());
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Couldn't write test-restore log file for {Backup}", backup.Name);
        }

        var status = outcome.OverallPass
            ? (outcome.FilesFailed == 0 ? Models.BackupRunStatus.Success : Models.BackupRunStatus.Warning)
            : Models.BackupRunStatus.Failed;

        var summary = outcome.Summary;

        var rec = new Models.RunRecord
        {
            // Re-use the in-flight record's Id so the persisted
            // entry is the same row in the dropdown that was
            // showing as "Running" — clicking it during the run
            // and after the run lands on the same item.
            Id         = runId,
            BackupId   = backup.Id,
            BackupName = backup.Name,
            Kind       = Models.RunKind.TestRestore,
            StartedUtc = outcome.StartedUtc,
            EndedUtc   = outcome.EndedUtc,
            Status     = status,
            Summary    = summary,
            TriggeredAutomatically = outcome.IsScheduled,
            LogPath    = logPath ?? "",
            Checks = new() { new BackupCheckResult
            {
                Depth = outcome.IsSample ? CheckDepth.RestoreSample : CheckDepth.RestoreAllFiles,
                Status = status, EndedUtc = outcome.EndedUtc, Summary = outcome.Summary,
            } },
        };
        ServiceLocator.Logs.AppendRun(rec);
    }

    /// <summary>
    /// Verify one destination of a backup. Returns the per-file results,
    /// the revision number tested, and an error message if the verify
    /// couldn't run against this destination at all (no snapshots, no
    /// non-empty files, etc.). Throws only on cancellation; every
    /// other failure is reported via the returned tuple.
    /// </summary>
    private async Task<(IReadOnlyList<FileVerifyResult> Results, int Revision, DateTime RevisionCreatedUtc, string? Error)>
        VerifyOneDestinationAsync(Backup backup, string unusedSource, Destination dest, BackupTarget target,
            DateTime started, CancellationToken ct, TestRestoreContext? context)
    {
        var all = new List<FileVerifyResult>();
        var failures = new List<string>();
        int revision = 0;
        DateTime created = default;
        foreach (var source in backup.SourcePaths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await VerifyOneSourceAsync(backup, source, dest, target, started, ct, context);
                all.AddRange(result.Results.Select(f => f with { SourcePath = source, DestinationName = dest.Name }));
                revision = Math.Max(revision, result.Revision);
                if (result.RevisionCreatedUtc > created) created = result.RevisionCreatedUtc;
                if (result.Error is not null) failures.Add($"{source}: {result.Error}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Error(ex, "Test restore of source {Source} at destination {Destination} failed", source, dest.Name);
                failures.Add($"{source}: {ex.Message}");
            }
        }
        return (all, revision, created, failures.Count == 0 ? null : string.Join("; ", failures));
    }

    internal static List<FileVerifyResult> FindMissingSnapshotFiles(Backup backup, string sourceRoot,
        IReadOnlyList<RevisionFile> snapshot, DateTime capturedUtc, CancellationToken ct)
    {
        var result = new List<FileVerifyResult>();
        if (!Directory.Exists(sourceRoot)) return result;
        if ((File.GetAttributes(sourceRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Verification does not follow a linked source folder: " + sourceRoot);
        var known = snapshot.Select(f => f.Path.Replace('\\', '/')).ToHashSet(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var rules = SavingsEstimator.ParseRules(backup.FiltersText);
        var stack = new Stack<string>();
        stack.Push(sourceRoot);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = stack.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                var relative = Path.GetRelativePath(sourceRoot, entry).Replace('\\', '/');
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (SavingsEstimator.IsExcluded(rules, relative + (isDirectory ? "/" : ""))) continue;
                if (isDirectory) { stack.Push(entry); continue; }
                if (known.Contains(relative)) continue;
                var file = new FileInfo(entry);
                // Files created or edited after capture belong to the next
                // backup. Only flag an unchanged pre-existing omission.
                if (file.LastWriteTimeUtc > capturedUtc || file.CreationTimeUtc > capturedUtc) continue;
                result.Add(new FileVerifyResult(relative, file.Length, 0, FileVerifyStatus.RestoreFailed,
                    "This unchanged source file is included by the current filters but absent from the snapshot. Run a fresh backup to check completeness.")
                    { SourcePath = sourceRoot });
            }
        }
        return result;
    }
    private async Task<(IReadOnlyList<FileVerifyResult> Results, int Revision, DateTime RevisionCreatedUtc, string? Error)>
        VerifyOneSourceAsync(
            Backup backup, string sourcePath, Destination dest, BackupTarget target,
            DateTime started, CancellationToken ct, TestRestoreContext? context)
    {
        var cell = context?.Cell(sourcePath, dest, target.StorageName);
        if (cell?.Error is not null) return (cell.Results, cell.Snapshot.Number, cell.Snapshot.CreatedUtc, cell.Error);
        if (cell?.Complete == true) return (cell.Results.Concat(cell.Missing).ToList(), cell.Snapshot.Number, cell.Snapshot.CreatedUtc, null);
        // Latest revision at this destination.
        IReadOnlyList<RevisionSummary> revs;
        try
        {
            revs = cell is not null ? new[] { cell.Snapshot }
                : await _revisions.ListRevisionsAsync(backup, sourcePath, dest, target.StorageName, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (Array.Empty<FileVerifyResult>(), 0, default,
                $"couldn't list revisions: {ex.Message}");
        }

        if (revs.Count == 0)
            return (Array.Empty<FileVerifyResult>(), 0, default,
                "no snapshots found at this destination yet; run a backup first");

        var latest = revs.OrderByDescending(r => r.Number).First();

        IReadOnlyList<RevisionFile> files;
        try
        {
            files = cell is not null ? cell.Files : await _revisions.ListFilesAsync(
                backup, sourcePath, dest, target.StorageName, latest.Number, ct, latest.SnapshotId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (Array.Empty<FileVerifyResult>(), latest.Number, latest.CreatedUtc,
                $"couldn't list files in revision {latest.Number}: {ex.Message}");
        }

        // A saved context supplies the immutable sample/full plan. The legacy
        // entry point uses every file. Modified originals are reported as
        // size-only; they never cause a selected file to be silently omitted.
        var picked = files.ToList();
        var missing = cell?.Missing ?? (context?.Journal.Options.CompareOriginals == false
            ? new List<FileVerifyResult>()
            : await Task.Run(() => FindMissingSnapshotFiles(backup, sourcePath, files, latest.CreatedUtc, ct), ct));
        if (picked.Count == 0)
            return (missing, latest.Number, latest.CreatedUtc,
                missing.Count > 0 ? "Expected source files are absent from this snapshot." :
                "The latest snapshot contains no files to restore.");
        // Per-destination temp dir — including the destination id so
        // a multi-destination drill doesn't have all dests writing
        // into the same folder (which would clobber each other's
        // restored files and corrupt the per-file verify).
        // Millisecond suffix so a user clicking Test Restore twice
        // in the same second on the same backup doesn't collide
        // (each click gets its own dir; the second drill won't
        // clobber the first's in-flight files).
        // Use the portable ConfigRoot so the verify scratch dir lives
        // alongside the rest of the app's data — preserves the
        // "everything under ConfigRoot" invariant on Linux/macOS
        // (where LocalApplicationData resolves to ~/.local/share or
        // ~/.config and breaks portable installs).
        var verifyRoot = Path.Combine(
            AppPaths.ConfigRoot, "verify",
            $"{backup.Id}-{started:yyyyMMddHHmmssfff}-{ShortId(dest.Id)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(verifyRoot);
        using var scratchOwner = new FileStream(Path.Combine(verifyRoot, ".owner.lock"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        // Write the heartbeat sentinel SYNCHRONOUSLY before launching
        // the background refresh task. If the synchronous write
        // throws (disk full, AV quarantine, ACL race) we ALSO touch
        // the dir's mtime so the sweep's legacy fallback path still
        // sees a fresh signal, but the refresh task can't get into a
        // "no sentinel ever written" state. Round-4 review caught
        // that an async-first heartbeat lets the legacy 24h fallback
        // fire for healthy current drills whose sentinel write was
        // delayed past the kill point.
        try { File.WriteAllText(Path.Combine(verifyRoot, HeartbeatFileName), DateTime.UtcNow.ToString("o")); }
        catch (Exception ex)
        {
            _log.Debug(ex, "Initial heartbeat-sentinel write threw for {Path}", verifyRoot);
            // Fallback: touch the dir's mtime so the sweep's
            // legacy-fallback path (which trusts mtime when no
            // sentinel is found) sees a fresh signal even if the
            // sentinel itself couldn't be written.
            try { Directory.SetLastWriteTimeUtc(verifyRoot, DateTime.UtcNow); }
            catch { /* best-effort */ }
        }
        // Start the background task that refreshes the sentinel's
        // mtime every HeartbeatInterval. Observes the cell's CTS so
        // it stops on user-cancel or end-of-drill via the linked
        // cts disposed in finally below.
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = StartHeartbeatAsync(verifyRoot, heartbeatCts.Token);
        try
        {
            // Initial restore + verify of the pre-filtered pick.
            var fileResults = await RunRestoreAndVerifyAsync(
                picked, backup, sourcePath, dest, target.StorageName,
                latest.Number, verifyRoot, ct, latest.SnapshotId, context, cell);

            if (cell is not null) context!.Checkpoint(cell, fileResults, complete: true);
            fileResults.AddRange(missing);
            return ((IReadOnlyList<FileVerifyResult>)fileResults, latest.Number, latest.CreatedUtc, null);
        }
        finally
        {
            // Stop the heartbeat task BEFORE the dir is deleted so the
            // task's next touch can't race with Directory.Delete and
            // leave a dangling .lock file in a half-deleted dir.
            try { heartbeatCts.Cancel(); } catch { }
            try { await heartbeatTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex) { _log.Debug(ex, "Heartbeat task teardown threw"); }
            scratchOwner.Dispose();
            try
            {
                if (ServiceLocator.Maintenance.IsBlocked)
                    throw new IOException("The backup engine has not confirmed that it stopped. Restart Backspace before removing these files.");
                await Task.Run(() => ResticBackrestEngine.DeleteRestoreStaging(verifyRoot)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Cleanup of verify temp dir failed");
                throw new IOException($"The test restore stopped, but temporary copies could not be removed at {verifyRoot}. {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Spawn a background task that writes <see cref="HeartbeatFileName"/>
    /// inside <paramref name="dir"/> and touches its mtime every
    /// <see cref="HeartbeatInterval"/>. Exits cleanly when the token
    /// fires (either user-cancel or the drill's finally-block cancel).
    /// Write failures are logged at Debug; the drill itself continues
    /// even if the heartbeat can't be kept fresh (the worst case is a
    /// future sweep deletes the dir mid-drill, which is precisely the
    /// behaviour we had before this code was added).
    /// </summary>
    private static Task StartHeartbeatAsync(string dir, CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            var path = Path.Combine(dir, HeartbeatFileName);
            // Initial write happens synchronously at the caller
            // (RunOnePerDestAsync) so the sweep can always find the
            // sentinel for healthy current drills. This task only
            // REFRESHES the mtime.
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(HeartbeatInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
                catch (FileNotFoundException) { RecreateHeartbeat(dir, path); }
                catch (DirectoryNotFoundException)
                {
                    // Round-6 review caught: the sweep-deletes-the-
                    // scratch-dir race throws DirectoryNotFoundException
                    // (the dir itself is missing), not
                    // FileNotFoundException. The old code only handled
                    // file-missing and silently fell through to the
                    // generic Exception catch — heartbeat recreate
                    // never fired for the case the comment described.
                    try { Directory.CreateDirectory(dir); }
                    catch (Exception ex) { _log.Debug(ex, "Heartbeat dir re-create threw — will retry next interval"); }
                    RecreateHeartbeat(dir, path);
                }
                catch (Exception ex) { _log.Debug(ex, "Heartbeat touch threw for {Path}", path); }
            }
        }, ct);
    }

    /// <summary>
    /// One restore-and-verify round: ask the engine to restore the
    /// chosen <paramref name="files"/> into <paramref name="verifyRoot"/>,
    /// then per-file verify the bytes. Shared by the initial pick and
    /// every subsequent replacement round so the restore-request shape
    /// can't drift between call sites.
    /// </summary>
    private async Task<List<FileVerifyResult>> RunRestoreAndVerifyAsync(
        IReadOnlyList<RevisionFile> files,
        Backup backup, string sourcePath, Destination dest, string storageName,
        int revision, string verifyRoot, CancellationToken ct, string? snapshotId,
        TestRestoreContext? context, TestRestoreCell? cell)
    {
        var results = cell?.Results.ToList() ?? new List<FileVerifyResult>();
        var done = results.Select(r => r.Path).ToHashSet(StringComparer.Ordinal);
        // Bound scratch-space growth for large backups. Each batch is fully
        // restored and byte-compared before its output is removed.
        foreach (var batch in VerificationBatches(files.Where(f => !done.Contains(f.Path))))
        {
            ct.ThrowIfCancellationRequested();
            if (context?.Control is not null)
            {
                context.Control.ContinueNextBatch();
                await context.Control.WaitWhilePausedAsync(ct);
            }
            context?.Phase($"{dest.Name} · recovering {batch.Length:N0} files from {sourcePath}");
            if (context is not null)
            {
                // Restore staging and publication can briefly hold two copies.
                var required = checked(batch.Sum(f => Math.Max(0, f.SizeBytes)) * 2 + 64L * 1024 * 1024);
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(verifyRoot))!);
                if (drive.AvailableFreeSpace < required)
                    throw new IOException($"Not enough temporary space to test this batch. It needs about {required / 1048576d:N0} MiB free. Free some space, then start another test restore.");
            }
            var request = new RestoreRequest
            {
                Control = context?.Control,
                ProgressObserver = context is null ? null : p => context.PublishProgress(p.BytesRestored),
                MaintenanceLeaseHeld = true,
                ShowInRegistry = false,
                Backup = backup, SourcePath = sourcePath, Destination = dest,
                StorageName = storageName, Revision = revision, SnapshotId = snapshotId,
                Files = batch.Select(f => f.Path).ToList(), TargetPath = verifyRoot,
                Overwrite = true, PreserveStructure = true,
                Threads = Math.Max(1, backup.Threads == 0 ? 4 : backup.Threads),
            };
            var outcome = await _restore.RunAsync(request, ct);
            ct.ThrowIfCancellationRequested();
            context?.Control?.ContinueNextBatch();
            var compareOriginals = context?.Journal.Options.CompareOriginals ?? true;
            context?.Phase(compareOriginals
                ? $"{dest.Name} · comparing recovered files with eligible originals"
                : $"{dest.Name} · checking recovered files");
            foreach (var file in batch)
            {
                ct.ThrowIfCancellationRequested();
                if (context?.Control is not null) await context.Control.WaitWhilePausedAsync(ct);
                results.Add(await Task.Run(() => VerifyOneFile(file, sourcePath, verifyRoot, outcome, ct, compareOriginals), ct));
                var restored = SafeVerificationPath(verifyRoot, file.Path);
                try { File.Delete(restored); }
                catch (Exception ex) { _log.Debug(ex, "Could not remove verified scratch file"); }
            }
            if (outcome.TerminalStatus == RestoreTerminalStatus.Cancelled)
                throw new OperationCanceledException("Test restore cancelled.", ct);
            if (outcome.TerminalStatus is RestoreTerminalStatus.Error or RestoreTerminalStatus.Warning)
                results.Add(new FileVerifyResult("(restore operation)", 0, 0, FileVerifyStatus.RestoreFailed,
                    outcome.ErrorMessage ?? $"The restore engine returned {outcome.TerminalStatus}."));
            if (cell is not null) context!.Checkpoint(cell, results);
            // These copies have been checked and removed. Do not retain one
            // publication record per file for a multi-day full verification.
            if (context?.Control is { } control) await Task.Run(control.ReleaseVerifiedBatch).ConfigureAwait(false);
        }
        return results;
    }

    internal static IEnumerable<RevisionFile[]> VerificationBatches(IEnumerable<RevisionFile> files)
    {
        const long limit = 256L * 1024 * 1024;
        var batch = new List<RevisionFile>(); long bytes = 0;
        foreach (var file in files)
        {
            if (batch.Count > 0 && (batch.Count >= 128 || file.SizeBytes > limit - bytes))
            { yield return batch.ToArray(); batch.Clear(); bytes = 0; }
            batch.Add(file); bytes = checked(bytes + Math.Max(0, file.SizeBytes));
        }
        if (batch.Count > 0) yield return batch.ToArray();
    }
    /// <summary>
    /// Walk-and-pick helper: advance <paramref name="state"/>'s
    /// Idx through the pre-shuffled pool, dedup against Seen, and
    /// keep entries that pass the pre-restore freshness check, until
    /// <paramref name="needed"/> are collected OR the per-call attempt
    /// budget is exhausted OR the pool is fully walked. State mutates
    /// in place so successive calls (initial pick + replacement
    /// rounds) consume the same shuffled pool without re-shuffling.
    /// </summary>
    private static List<RevisionFile> PickFreshCandidates(
        PickerState state, int needed, string sourcePath, CancellationToken ct)
    {
        var local = new List<RevisionFile>();
        int attemptsThisCall = 0;
        while (local.Count < needed
               && attemptsThisCall < state.MaxAttempts
               && state.Idx < state.PoolShuffled.Count)
        {
            if (ct.IsCancellationRequested) break;
            attemptsThisCall++;
            var candidate = state.PoolShuffled[state.Idx++];
            if (!state.Seen.Add(candidate.Path)) continue;
            if (IsSourceUnchangedSinceSnapshot(candidate, sourcePath))
                local.Add(candidate);
        }
        return local;
    }

    /// <summary>
    /// Trim up to <paramref name="count"/> of the oldest IsMidFlightDirty
    /// entries from <paramref name="results"/> in place. Called by the
    /// replacement loop right before appending fresh results so the
    /// final list size stays bounded by FilesToSample (plus pool
    /// sparseness slack). Modifies the list in place; visits results
    /// in stored order, so the "oldest" dirty entries are removed first
    /// — matches the user's mental model that the earlier rounds were
    /// the wasted ones being retried.
    /// </summary>
    private static void RemoveOldestDirty(List<FileVerifyResult> results, int count)
    {
        if (count <= 0) return;
        int removed = 0;
        for (int i = 0; i < results.Count && removed < count;)
        {
            if (results[i].IsMidFlightDirty)
            {
                results.RemoveAt(i);
                removed++;
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary>Recreate the heartbeat sentinel file (used by both
    /// the file-missing and dir-missing recovery paths). Best-effort;
    /// failure is logged at Debug and the next refresh tick will
    /// try again.</summary>
    private static void RecreateHeartbeat(string dir, string path)
    {
        try { File.WriteAllText(path, DateTime.UtcNow.ToString("o")); }
        catch (Exception ex) { _log.Debug(ex, "Heartbeat re-create threw for {Path} — will retry next interval", path); }
    }

    /// <summary>Mutable picker state shared between the initial pick
    /// and the replacement loop. Held in a class so the helper can
    /// advance Idx across calls without ref parameters (which can't
    /// cross lambda capture).</summary>
    private sealed class PickerState
    {
        public required HashSet<string> Seen { get; init; }
        public required List<RevisionFile> PoolShuffled { get; init; }
        public int Idx { get; set; }
        public int MaxAttempts { get; init; }
    }

    /// <summary>
    /// True iff the current source file's mtime is unchanged from
    /// what the snapshot recorded. Used to skip files that have been
    /// edited since the backup ran — they'd produce
    /// <see cref="FileVerifyStatus.OkNoCompare"/> at verify time,
    /// which doesn't actually exercise the byte-compare path.
    ///
    /// Strict equality is correct: both reads come from the same
    /// source filesystem (NTFS / ReFS, 100 ns precision), so an
    /// unmodified file reports identical mtimes on both reads.
    /// Any later mtime means the file was touched between snapshot
    /// capture and now.
    ///
    /// Returns false when the source file is missing, unreadable, or
    /// edited after the snapshot.
    /// </summary>
    private static bool IsSourceUnchangedSinceSnapshot(RevisionFile snapshot, string sourceRoot)
    {
        try
        {
            var rel = snapshot.Path.Replace('/', Path.DirectorySeparatorChar);
            var src = Path.Combine(sourceRoot, rel);
            if (!File.Exists(src)) return false;
            var fi = new FileInfo(src);
            var snapMtimeUtc = AsUtc(snapshot.ModifiedUtc);
            return fi.LastWriteTimeUtc == snapMtimeUtc && fi.Length == snapshot.SizeBytes;
        }
        catch
        {
            // Path-too-long, permission denied, race-window deletion, etc.
            // Treat as "not safely verifiable" so we move on to another
            // candidate instead of hard-failing the drill.
            return false;
        }
    }

    private static string ShortId(string? id) =>
        string.IsNullOrEmpty(id) ? "x" : id.Substring(0, Math.Min(8, id.Length));

    /// <summary>Reachable destinations for verify — every target whose
    /// volume is mounted (local-like) or unconditionally reachable
    /// (cloud / S3). Returns the destinations in target-list order.</summary>
    private List<(Destination Destination, BackupTarget Target, string? Unavailable)> ResolveVerificationTargets(Backup backup)
    {
        var list = new List<(Destination, BackupTarget, string?)>();
        foreach (var t in backup.Targets)
        {
            var d = _config.Current.Destinations.FirstOrDefault(x => x.Id == t.DestinationId);
            if (d is null)
            {
                list.Add((new Destination { Id = t.DestinationId, Name = t.DestinationId }, t,
                    "This configured destination no longer exists. Verification could not run."));
                continue;
            }
            string? unavailable = null;
            if (d.IsLocalLike)
            {
                var v = VolumeDetector.Check(d.ExpectedDriveLetter, d.ExpectedVolumeLabel);
                if (v.Verdict != VolumeDetector.Verdict.Mounted)
                    unavailable = "The destination drive is not connected. Verification could not run.";
            }
            list.Add((d, t, unavailable));
        }
        return list;
    }

    /// <summary>
    /// Persist the outcome of a verify run onto the Backup record so the
    /// Dashboard can show "Verified 12 days ago — OK" without re-running
    /// the drill. Called by the VM after VerifyAsync returns.
    /// </summary>
    public void PersistOutcome(VerifyOutcome outcome)
    {
        _config.Update(cfg =>
        {
            var b = cfg.Backups.FirstOrDefault(x => x.Id == outcome.BackupId);
            if (b is null) return;
            b.LastVerifyUtc = outcome.EndedUtc;
            b.LastVerifyPass = outcome.OverallPass;
            b.LastVerifySummary = outcome.Summary;
            b.LastVerifyWasSample = outcome.IsSample;
        });
    }

    // ---- internals ----------------------------------------------------

    /// <summary>
    /// Normalise restic-parsed timestamps (which come back as Kind=Local
    /// or Kind=Unspecified after <c>DateTime.TryParse</c>) so we can
    /// compare them against <c>FileInfo.LastWriteTimeUtc</c> without a
    /// timezone-offset false positive flagging every file as "modified
    /// after snapshot".
    /// </summary>
    private static DateTime AsUtc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        _ => DateTime.SpecifyKind(d, DateTimeKind.Local).ToUniversalTime(),
    };

    internal static FileVerifyResult VerifyOneFile(
        RevisionFile snapshot, string sourceRoot, string restoreRoot,
        RestoreOutcome restoreOutcome, CancellationToken ct = default, bool compareOriginals = true)
    {
        ct.ThrowIfCancellationRequested();
        var rel = snapshot.Path;
        var restoredPath = SafeVerificationPath(restoreRoot, rel);
        if (File.Exists(restoredPath) && (File.GetAttributes(restoredPath) & FileAttributes.ReparsePoint) != 0)
            return new FileVerifyResult(snapshot.Path, snapshot.SizeBytes, 0, FileVerifyStatus.OkNoCompare,
                "This is a symbolic link. Its target was not followed or byte-compared.");

        // 2026-06-12 refactor: the per-file outcome map is gone.
        // File.Exists is the ground truth — same check restic uses
        // internally to know whether a file landed. Failure detail
        // surfaces from the overall ErrorMessage.
        if (!File.Exists(restoredPath))
        {
            return new FileVerifyResult(
                Path: snapshot.Path,
                ExpectedBytes: snapshot.SizeBytes,
                ActualBytes: 0,
                Status: FileVerifyStatus.RestoreFailed,
                Detail: restoreOutcome.ErrorMessage ?? "File missing after restore.");
        }

        var restoredFi = new FileInfo(restoredPath);
        if (restoredFi.Length != snapshot.SizeBytes)
        {
            return new FileVerifyResult(
                Path: snapshot.Path,
                ExpectedBytes: snapshot.SizeBytes,
                ActualBytes: restoredFi.Length,
                Status: FileVerifyStatus.SizeMismatch,
                Detail: $"Snapshot recorded {snapshot.SizeBytes:N0} bytes, restored file is {restoredFi.Length:N0} bytes.");
        }

        if (!compareOriginals)
            return new FileVerifyResult(snapshot.Path, snapshot.SizeBytes, restoredFi.Length, FileVerifyStatus.Recovered,
                "File recovered with the expected size. Comparison with today's original was not requested.");

        // Byte-compare against current source only when explicitly requested.
        var sourcePath = SafeVerificationPath(sourceRoot, rel);
        if (File.Exists(sourcePath) && (File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
            return new FileVerifyResult(snapshot.Path, snapshot.SizeBytes, restoredFi.Length, FileVerifyStatus.OkNoCompare,
                "The current source is a symbolic link; its target was not followed or byte-compared.");
        if (!File.Exists(sourcePath))
        {
            return new FileVerifyResult(
                snapshot.Path, snapshot.SizeBytes, restoredFi.Length,
                FileVerifyStatus.OkNoCompare,
                "Source file no longer exists; size matches snapshot. Byte comparison skipped.");
        }

        var srcFi = new FileInfo(sourcePath);
        // Compare the per-file date, not the snapshot creation time.
        // A different date in either direction makes today's original
        // unsuitable for comparison. Equal dates still are only a heuristic.
        var snapMtimeUtc = AsUtc(snapshot.ModifiedUtc);
        if (srcFi.LastWriteTimeUtc != snapMtimeUtc)
        {
            // The legacy unchanged-file sampler can replace this slot;
            // full and durable samples retain the skipped-comparison result.
            return new FileVerifyResult(
                snapshot.Path, snapshot.SizeBytes, restoredFi.Length,
                FileVerifyStatus.OkNoCompare,
                "Source modification date differs from the snapshot; byte comparison skipped.")
            { IsMidFlightDirty = true };
        }

        if (srcFi.Length != restoredFi.Length)
        {
            return new FileVerifyResult(
                snapshot.Path, snapshot.SizeBytes, restoredFi.Length,
                FileVerifyStatus.OkNoCompare,
                "Source size differs from snapshot. Byte comparison skipped.");
        }

        var previousWrite = srcFi.LastWriteTimeUtc;
        var previousLength = srcFi.Length;
        var matches = BytesEqual(sourcePath, restoredPath, ct);
        srcFi.Refresh();
        if (srcFi.LastWriteTimeUtc != previousWrite || srcFi.Length != previousLength)
            return new FileVerifyResult(snapshot.Path, snapshot.SizeBytes, restoredFi.Length, FileVerifyStatus.OkNoCompare,
                "Source changed during comparison; size matched the snapshot but byte comparison is inconclusive.") { IsMidFlightDirty = true };
        return matches
            ? new FileVerifyResult(
                snapshot.Path, snapshot.SizeBytes, restoredFi.Length,
                FileVerifyStatus.OK, null)
            : new FileVerifyResult(
                snapshot.Path, snapshot.SizeBytes, restoredFi.Length,
                FileVerifyStatus.BytesMismatch,
                "Recovered contents differ from today's original. The comparison failed; this alone does not show which copy changed.");
    }

    internal static string SafeVerificationPath(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidOperationException("Snapshot paths must be relative to the source.");
        relative = ResticBackrestEngine.NormalizeRestoreRelativePath(relative);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Snapshot path escapes the verification folder.");
        for (var parent = Path.GetDirectoryName(full); parent is not null && parent.Length >= fullRoot.Length; parent = Path.GetDirectoryName(parent))
            if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Verification does not follow linked folders: " + parent);
        return full;
    }

    internal static bool BytesEqual(string p1, string p2, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        const int BufSize = 64 * 1024;
        using var f1 = File.OpenRead(p1);
        using var f2 = File.OpenRead(p2);
        if (f1.Length != f2.Length) return false;
        var b1 = new byte[BufSize];
        var b2 = new byte[BufSize];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int n1 = f1.Read(b1, 0, BufSize);
            int n2 = f2.Read(b2, 0, BufSize);
            if (n1 != n2) return false;
            if (n1 == 0) return true;
            if (!b1.AsSpan(0, n1).SequenceEqual(b2.AsSpan(0, n2))) return false;
        }
    }

    /// <summary>
    /// Build the multi-line user-facing summary for a verify drill. The
    /// shape (per user feedback 2026-04-29) is:
    /// <list type="bullet">
    ///   <item><c>"X of Y destinations FAILED:"</c> + blank line + bullet list of failures</item>
    ///   <item>blank line</item>
    ///   <item><c>"Z of Y destinations SUCCEEDED: 10 files restored, 10 byte-identical"</c> + blank line + bullet list of successes</item>
    /// </list>
    /// Replaces the previous middle-dot-joined run-on string ("...FAILED ·
    /// «Local» ... · «Dropbox» ...") which was unreadable. Single-
    /// destination drills get the simpler single-line format —
    /// the bullet structure is overkill when there's only one item.
    /// </summary>
    private static string BuildOverallSummary(
        int totalDests, int destinationsOk, int destinationsFailed,
        IReadOnlyList<string> successItems, IReadOnlyList<string> failedItems,
        int filesPicked, int filesByteIdentical, int filesSizeOnly, int filesRecovered)
    {
        if (destinationsOk == 0 && destinationsFailed == 0)
            return "No destinations could be verified.";

        // Restic validates recovered data independently of today's originals.
        // Report the optional comparison separately, never imply it is needed
        // to establish a successful recovery or claim all skipped sources changed.
        string FilesLine() => filesRecovered == filesPicked
            ? $"{filesPicked} files recovered successfully. Comparison with today's originals was not requested."
            : $"{filesPicked} files restored; {filesByteIdentical} matched today's originals, {filesSizeOnly} could not be compared"
                + (filesRecovered > 0 ? $", {filesRecovered} did not request comparison" : "");

        // Single-destination short form. Doesn't need the grouped layout.
        if (totalDests == 1)
        {
            if (destinationsFailed == 0)
                return successItems.Count > 0
                    ? $"{successItems[0]} · {FilesLine()}"
                    : "Verified.";
            return failedItems.Count > 0
                ? failedItems[0]
                : "Verify failed. Open Activity & logs for details.";
        }

        var sb = new System.Text.StringBuilder();

        // Failures group first — they're the urgent thing to act on.
        // Single line break before each list item (was double — the
        // extra blank line between the header and the first bullet
        // looked like a missing item).
        if (destinationsFailed > 0)
        {
            sb.Append($"{destinationsFailed} of {totalDests} destinations FAILED:");
            foreach (var item in failedItems)
            {
                sb.AppendLine();
                sb.Append("  • ");
                sb.Append(item);
            }
        }

        if (destinationsOk > 0)
        {
            if (sb.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine();
            }
            sb.Append($"{destinationsOk} of {totalDests} destinations SUCCEEDED: ");
            sb.Append(FilesLine());
            sb.Append(" across these destinations");
            foreach (var item in successItems)
            {
                sb.AppendLine();
                sb.Append("  • ");
                sb.Append(item);
            }
        }

        return sb.ToString();
    }

    private static VerifyOutcome Failed(Backup backup, DateTime started, string error) =>
        new(
            BackupId: backup.Id,
            BackupName: backup.Name,
            StartedUtc: started,
            EndedUtc: DateTime.UtcNow,
            RevisionNumber: 0,
            FilesPicked: 0,
            FilesOk: 0,
            FilesFailed: 0,
            OverallPass: false,
            Files: Array.Empty<FileVerifyResult>(),
            FatalError: error,
            Summary: error);
}

// =================== outcome shapes ===================

/// <summary>
/// Per-file result from a verify drill. <see cref="FileVerifyStatus.OK"/>
/// means the bytes were compared against the live source and matched;
/// <see cref="FileVerifyStatus.OkNoCompare"/> means we couldn't compare
/// (source missing or modified after the snapshot, or a symbolic link).
/// <see cref="FileVerifyStatus.Recovered"/> means recovery and size checks
/// passed and comparison with current originals was not requested. Restic's
/// data validation is independent of that optional comparison.
/// </summary>
public enum FileVerifyStatus
{
    OK,
    OkNoCompare,
    SizeMismatch,
    BytesMismatch,
    RestoreFailed,
    Recovered,
}

public sealed record FileVerifyResult(
    string Path,
    long ExpectedBytes,
    long ActualBytes,
    FileVerifyStatus Status,
    string? Detail)
{
    public string SourcePath { get; init; } = "";
    public string DestinationName { get; init; } = "";

    /// <summary>
    /// True iff this <see cref="FileVerifyStatus.OkNoCompare"/> verdict
    /// was specifically caused by the source file being edited DURING
    /// the verify run — i.e. it passed
    /// <see cref="BackupVerifier.IsSourceUnchangedSinceSnapshot"/> at
    /// pre-restore but failed the same check post-restore. The
    /// replacement loop in <see cref="BackupVerifier"/> uses this to
    /// distinguish "user touched the file mid-test, try a different
    /// file" from other OkNoCompare causes (source no longer exists,
    /// snapshot size differs) — those aren't productively replaceable
    /// with a fresh pick. Default false; set to true at the single
    /// call site where mid-flight dirtying is detected.
    /// </summary>
    public bool IsMidFlightDirty { get; init; }
}

public sealed record VerifyOutcome(
    string BackupId,
    string BackupName,
    DateTime StartedUtc,
    DateTime EndedUtc,
    int RevisionNumber,
    int FilesPicked,
    int FilesOk,
    int FilesFailed,
    bool OverallPass,
    IReadOnlyList<FileVerifyResult> Files,
    string? FatalError,
    string Summary)
{
    public bool IsSample { get; init; }
    public bool IsScheduled { get; init; }
}
