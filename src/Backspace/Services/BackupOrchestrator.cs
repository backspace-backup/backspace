using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.ViewModels;
using Serilog;

namespace Backspace.Services;

/// <summary>
/// The high-level "run this backup end-to-end" orchestrator.
/// Called by the GUI ("Run now") and by the unattended CLI entrypoint ("--run").
///
/// Responsibilities:
///   - Volume-mount check (skip if drive not mounted — not a failure)
///   - Start metered-network watchdog (if enabled)
///   - For each target destination: backup → prune → check
///   - Ping Healthchecks.io
///   - Send email notification
///   - Fire desktop notification
///   - Persist RunRecord
/// </summary>
public sealed partial class BackupOrchestrator
{
    /// <summary>De-duplicate a backup's targets by
    /// <c>(DestinationId, StorageName.ToLowerInvariant())</c>. Both
    /// the regular cell loop and the prune/check loop must agree on
    /// this grouping — otherwise a hand-edited config with duplicate
    /// targets fans out into multiple identical cells, truncates
    /// per-cell logs (BuildCellLogPath returns the same path), and
    /// produces indistinguishable Backrest operation event streams.
    /// Lowercasing matches MakeRepoId / Slug downstream which
    /// lowercase the entire repo id.
    /// Exposed as `internal static` so the regression test suite can
    /// call into the same code path the orchestrator does.</summary>
    internal static List<BackupTarget> DistinctTargets(IEnumerable<BackupTarget> targets)
    {
        return targets
            .GroupBy(t => (t.DestinationId, StorageName: (t.StorageName ?? "").ToLowerInvariant()))
            .Select(g => g.First())
            .ToList();
    }

    private readonly ConfigStore _config;
    private readonly SecretsStore _secrets;
    private readonly IBackupEngine _engine;
    private readonly LogStore _logs;
    private readonly HealthcheckService _hc;
    private readonly MailService _mail;
    private readonly NotificationService _notify;
    private readonly BackupProgressService _progress;
    private static ILogger _log => AppLogger.For<BackupOrchestrator>();

    public BackupOrchestrator(
        ConfigStore config,
        SecretsStore secrets,
        IBackupEngine engine,
        LogStore logs,
        HealthcheckService hc,
        MailService mail,
        NotificationService notify,
        BackupProgressService progress)
    {
        _config = config;
        _secrets = secrets;
        _engine = engine;
        _logs = logs;
        _hc = hc;
        _mail = mail;
        _notify = notify;
        _progress = progress;
    }

    /// <summary>
    /// Fans the backup out across every (source × destination) cell.
    /// A backup with 2 sources and 2 destinations produces 4 cells, each
    /// with its own <c>ResticBackrestEngine</c> invocation, its own
    /// repo, and its own retry budget. Cell failures are isolated — one
    /// cell timing out or hitting an auth error never cancels the rest.
    /// A pre-cancelled token or a metered-network trip <em>does</em>
    /// cancel the whole run; that's the one global signal.
    /// </summary>
    /// <summary>
    /// Registry of in-flight runs so callers (the delete flow, Dashboard,
    /// CLI shutdown) can cancel a backup that's currently underway. We
    /// link the caller's external token with our own internal cancel
    /// source on entry, store the source keyed by Backup.Id, and
    /// dispose it on exit. Process-local — see <see cref="MutexNameFor"/>
    /// for the cross-process gate that pairs with this dictionary.
    /// </summary>
    private readonly ConcurrentDictionary<string, RunningInfo> _runningRuns = new();

    /// <summary>
    /// Per-cell retry cascade. First entry is immediate (initial try);
    /// subsequent entries are the post-failure waits. Tuned for
    /// "transient network blip" recovery without burning the user's
    /// time on a persistent failure — 5 + 20 + 60 s totals about
    /// 85 s of waiting before the cell gives up. Marked
    /// <see cref="INonRetriable"/> exceptions short-circuit this
    /// cascade entirely (see <see cref="RunCellWithRetryAsync"/>).
    /// </summary>
    private static readonly TimeSpan[] CellRetryBackoffs = BuildCellRetryBackoffs();

    /// <summary>
    /// Build the per-cell retry-backoff schedule. Production default
    /// is 0/5/20/60s (85s total) — generous for the rare transient
    /// network blip a real user might hit. The test suite sets
    /// <c>BACKSPACE_FAST_RETRIES=1</c> so failing scenarios drain in
    /// seconds, not minutes — the wider suite was burning ~85s per
    /// failed cell × 13 cells / retry-able scenario before this knob.
    /// </summary>
    private static TimeSpan[] BuildCellRetryBackoffs()
    {
        var fast = string.Equals(
            Environment.GetEnvironmentVariable("BACKSPACE_FAST_RETRIES"),
            "1", StringComparison.Ordinal);
        return fast
            ? new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500) }
            : new[] { TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60) };
    }

    /// <summary>Per-run state we keep alive while a run is in flight.
    /// Lets callers (LogsView's "current run" entry, BackupsView's
    /// hard-cancel watchdog) read more than just "is it running" — the
    /// start time so the LogsView's run dropdown can render an entry
    /// for the live run with a real "started …" label, and a reference
    /// to the synthesized record so the hard-cancel path can tag it as
    /// "stopped by user" before firing RunEnded.
    /// <see cref="CancelReason"/> is set by whoever signals the cancel
    /// (the user via Stop = "Manually skipped"; the metered-network
    /// watchdog = "Network became metered"; force-finalise =
    /// "force-finalised after timeout"). The cell-loop and BuildSummary
    /// read it back so the Skipped surface text tells the user WHY,
    /// instead of the previous generic "drive unplugged or network
    /// gone" line that was wrong for at least the manual-stop case.</summary>
    private sealed class RunningInfo
    {
        public CancellationTokenSource Cts { get; }
        public DateTime StartedUtc { get; }
        public RunRecord Record { get; }
        /// <summary>First-writer-wins cancel reason. Use
        /// <see cref="TrySetCancelReason"/> for atomic publication —
        /// the previous "if (string.IsNullOrEmpty(CancelReason))
        /// CancelReason = …" pattern was a non-atomic check-then-set
        /// that lost reasons when two cancel signals raced (user Stop
        /// + metered watchdog hit within microseconds of each
        /// other).</summary>
        private string? _cancelReason;
        public string? CancelReason
        {
            get => Volatile.Read(ref _cancelReason);
            set => Volatile.Write(ref _cancelReason, value);
        }
        public bool TrySetCancelReason(string? reason)
        {
            if (string.IsNullOrEmpty(reason)) return false;
            return Interlocked.CompareExchange(ref _cancelReason, reason, null) is null;
        }
        public RunningInfo(CancellationTokenSource cts, DateTime startedUtc, RunRecord record)
        {
            Cts = cts; StartedUtc = startedUtc; Record = record;
        }
    }

    /// <summary>
    /// Cross-process run gate name for a given backup id. Two different
    /// Backspace processes (typically: the GUI and a scheduled-task
    /// <c>--run</c> CLI invocation) must not run the same backup at the
    /// same time — both would spawn <c>the engine binary backup</c> against
    /// the same storage URL and could interleave snapshot revisions,
    /// corrupting the chain.
    ///
    /// The name is <c>Local\Backspace-Run-{sha1(backup.Id)[..16]}</c>
    /// so it scopes to the current Windows session+user (matching the
    /// <see cref="SingleInstanceCoordinator"/> convention) and is stable
    /// for a given backup across processes and across builds.
    ///
    /// Internal so the regression test can assert determinism without
    /// reaching into reflection.
    /// </summary>
    internal static string MutexNameFor(string backupId)
    {
        var h = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(backupId ?? ""));
        var sb = new System.Text.StringBuilder(16);
        for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
        return $@"Local\Backspace-Run-{sb}";
    }

    /// <summary>
    /// Cross-process gate name for one (destination, storage) pair.
    /// Two backups that share a target QUEUE on this name instead of
    /// racing — restic's prune phase is documented as preferring
    /// exclusive access to a storage, so serialising at this boundary
    /// is the cheapest way to guarantee we can't pile two prunes onto
    /// the same destination at once. Mirrors <see cref="MutexNameFor"/>'s
    /// hash-truncation pattern so the name is short, stable, and
    /// session-scoped (Local\) for multi-user safety.
    ///
    /// Internal so the regression test can pin determinism without
    /// reflection.
    /// </summary>
    internal static string MutexNameForDestination(string destinationId, string storageName)
    {
        var key = $"{destinationId ?? ""}|{storageName ?? ""}";
        var h = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(key));
        var sb = new System.Text.StringBuilder(16);
        for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2"));
        return $@"Local\Backspace-Dest-{sb}";
    }

    /// <summary>
    /// Per-destination polling cadence while we wait for the gate.
    /// 250ms keeps the wait responsive to cancellation and to a peer
    /// run finishing without thrashing the kernel handle.
    /// </summary>
    private static readonly TimeSpan DestinationGatePollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Acquire (in deterministic order) one named cross-process
    /// semaphore per (DestinationId, StorageName) the backup writes
    /// to. Blocks asynchronously until each is free; surfaces a
    /// "Queued — waiting for …" status on the run record while
    /// waiting so the BackupCard label flips to it. Honours
    /// <paramref name="ct"/> cancellation mid-wait so a user clicking
    /// Stop on a queued run drops out cleanly.
    ///
    /// Acquisition order matters: two backups whose targets share a
    /// SUBSET of destinations would deadlock if one acquired
    /// {Dest1,Dest2} and the other acquired {Dest2,Dest1}. Sorting
    /// by key makes the order globally consistent and removes that
    /// hazard.
    /// </summary>
    private async Task AcquireDestinationGatesAsync(
        Backup backup,
        RunRecord runRecord,
        List<Semaphore> acquired,
        CancellationToken ct)
    {
        // Distinct + sorted target keys. Distinct collapses a backup
        // that lists the same destination twice (config rounding) so
        // we don't try to take the same lock twice and self-deadlock.
        var keys = backup.Targets
            .Select(t => (DestId: t.DestinationId ?? "", Storage: t.StorageName ?? "default"))
            .Distinct()
            .OrderBy(k => k.DestId, StringComparer.Ordinal)
            .ThenBy(k => k.Storage, StringComparer.Ordinal)
            .ToList();

        if (keys.Count == 0) return;

        foreach (var (destId, storage) in keys)
        {
            var name = MutexNameForDestination(destId, storage);
            Semaphore sem;
            try
            {
                sem = new Semaphore(initialCount: 1, maximumCount: 1, name: name);
            }
            catch (Exception ex)
            {
                // Restricted environment — degrade gracefully and
                // continue without the cross-process gate for this
                // destination. Runs in OTHER processes might still
                // race, but the GUI won't crash. Logged so it shows
                // up in support reports.
                _log.Warning(ex,
                    "Cross-process destination gate unavailable for {Name}/{Dest}/{Storage}; running unguarded",
                    backup.Name, destId, storage);
                continue;
            }

            // Fast path: try once non-blocking. If we get it, great.
            if (sem.WaitOne(0))
            {
                // Defensive: if `acquired.Add(sem)` throws (OOM
                // adding to the List), we've already taken the
                // semaphore (count → 0) but never recorded
                // ownership, so ReleaseDestinationGates would skip
                // it and the named semaphore would stay locked from
                // every other process's view forever. Release+
                // Dispose on the failure path to undo the WaitOne.
                try { acquired.Add(sem); }
                catch { try { sem.Release(); } catch { } try { sem.Dispose(); } catch { } throw; }
                continue;
            }

            // Slow path: someone else holds it — surface "queued"
            // and poll until it's free or the user cancels.
            var destName = ResolveDestinationDisplayName(destId);
            var reason = $"waiting for «{destName}» (another backup is using it)";
            runRecord.Summary = $"Queued: {reason}";
            _log.Information(
                "Run {Name} queued on destination {Dest}/{Storage}", backup.Name, destId, storage);
            try { RunQueuedReasonChanged?.Invoke(backup.Id, reason); }
            catch (Exception ex) { _log.Warning(ex, "RunQueuedReasonChanged handler threw for {Name}", backup.Name); }

            try
            {
                // Poll asynchronously: WaitOne(0) is non-blocking
                // (just probes the kernel handle). Between probes,
                // Task.Delay yields the thread so we don't pin a
                // thread-pool worker for the entire wait. ct
                // cancellation cuts the wait short within one poll
                // interval (250ms) without bouncing off WaitOne's
                // timer.
                var taken = false;
                while (!ct.IsCancellationRequested)
                {
                    if (sem.WaitOne(0))
                    {
                        // Same defensive pattern as the fast path —
                        // a List.Add throw after we've already
                        // taken the count must Release before we
                        // surface the throw, otherwise the cross-
                        // process semaphore stays locked forever.
                        try { acquired.Add(sem); }
                        catch { try { sem.Release(); } catch { } throw; }
                        taken = true;
                        break;
                    }
                    try { await Task.Delay(DestinationGatePollInterval, ct); }
                    catch (OperationCanceledException) { break; }
                }
                if (!taken)
                {
                    sem.Dispose();
                    ct.ThrowIfCancellationRequested();
                }
            }
            catch
            {
                // Don't keep a zombie handle if we throw after a
                // failed acquisition.
                try { sem.Dispose(); } catch { }
                throw;
            }
        }

        // Acquired everything — clear the queued banner and flip the
        // run record back to "Running…". Safe to do unconditionally:
        // if we never queued, the summary was already "Running…".
        runRecord.Summary = "Running…";
        try { RunQueuedReasonChanged?.Invoke(backup.Id, ""); }
        catch (Exception ex) { _log.Warning(ex, "RunQueuedReasonChanged (clear) handler threw for {Name}", backup.Name); }
    }

    /// <summary>Release + dispose every destination semaphore we
    /// acquired in <see cref="AcquireDestinationGatesAsync"/>. Called
    /// from the run's finally so a thrown body still frees the gates
    /// for the next queued backup.</summary>
    private static void ReleaseDestinationGates(List<Semaphore> acquired)
    {
        for (int i = acquired.Count - 1; i >= 0; i--)
        {
            try { acquired[i].Release(); } catch { /* not held — fine */ }
            try { acquired[i].Dispose(); } catch { }
        }
        acquired.Clear();
    }

    /// <summary>Look up a destination's friendly name from the live
    /// config for the queued-status message. Falls back to the id
    /// when the destination has been deleted between scheduling and
    /// the gate acquire (rare but possible).</summary>
    private string ResolveDestinationDisplayName(string destinationId)
    {
        if (string.IsNullOrEmpty(destinationId)) return "destination";
        var d = _config.Current.Destinations.FirstOrDefault(x => x.Id == destinationId);
        return d?.Name ?? destinationId;
    }

    /// <summary>Fired on the thread that started the run, after the run
    /// passed the concurrency gate. ViewModels subscribe to flip rows
    /// into a "running" state with a cancel button. Argument is Backup.Id.</summary>
    public event Action<string>? RunStarted;

    /// <summary>Fired when a run leaves the registry — success, failure,
    /// or skipped/cancelled. Carries the final RunRecord so subscribers
    /// can refresh history listings without reloading from disk.</summary>
    public event Action<string, RunRecord>? RunEnded;

    /// <summary>
    /// Fired when a run's queued-status changes — empty string means
    /// "no longer queued / now running", non-empty is a human-readable
    /// reason like "waiting for «Daily Photos» on Dropbox". The
    /// BackupCard subscribes so its inline label flips between
    /// "Running…" and "Queued — waiting for X" without polling. Also
    /// fired (with empty) when the gate is acquired so the card can
    /// switch back. First arg is the queued backup's id; second is
    /// the reason (or empty).
    /// </summary>
    public event Action<string, string>? RunQueuedReasonChanged;

    /// <summary>True if a run for this backup is currently in flight.</summary>
    public bool IsRunning(string backupId) => _runningRuns.ContainsKey(backupId);

    /// <summary>
    /// Snapshot of an in-flight run for the LogsView "current run" entry.
    /// Returns true and writes <paramref name="startedUtc"/> when the
    /// backup is currently running; false otherwise. The start time is
    /// the moment the orchestrator added the entry to its registry —
    /// not the wall-clock time of the user's click — so multiple
    /// observers see a consistent "this run started at T" anchor.
    /// </summary>
    public bool TryGetRunningInfo(string backupId, out DateTime startedUtc)
    {
        if (_runningRuns.TryGetValue(backupId, out var info))
        {
            startedUtc = info.StartedUtc;
            return true;
        }
        startedUtc = default;
        return false;
    }

    /// <summary>
    /// Stable reference to the in-flight RunRecord for a backup. Used by
    /// LogsView's "current run" entry — earlier the view synthesised a
    /// fresh RunRecord on every reload, which broke ComboBox selection
    /// (the dropdown bound to SelectedRun ended up pointing at a record
    /// that was no longer in the ItemsSource after the next reload, so
    /// the selection silently went blank). Returning the orchestrator's
    /// own backing instance makes the synthetic dropdown entry a stable
    /// object across reloads, so SelectedRun continues to match.
    /// </summary>
    public bool TryGetRunningRecord(string backupId, out RunRecord record)
    {
        if (_runningRuns.TryGetValue(backupId, out var info))
        {
            record = info.Record;
            return true;
        }
        record = null!;
        return false;
    }

    /// <summary>
    /// Signal an in-flight run for <paramref name="backupId"/> to cancel.
    /// Returns immediately; the run itself will tear down at the next
    /// cancellation checkpoint (between cells, within a cell waiting on
    /// retry, or inside the engine binary via the spawned process being
    /// killed). Use <see cref="WaitForRunCompletionAsync"/> if you need
    /// to know it's finished. Optional <paramref name="reason"/> is
    /// recorded on the running-info so the run's final Summary surfaces
    /// it ("Manually skipped", "Network became metered", etc.) instead
    /// of the previous generic skip text.
    /// </summary>
    public void CancelRun(string backupId, string? reason = null)
    {
        if (_runningRuns.TryGetValue(backupId, out var info))
        {
            // First-writer-wins for the reason so a user-clicked Stop
            // doesn't overwrite a metered-network reason that fired
            // moments earlier (or vice versa). Atomic via
            // TrySetCancelReason — the prior check-then-set was a
            // TOCTOU race that lost a reason under tight contention.
            info.TrySetCancelReason(reason);
            try { info.Cts.Cancel(); } catch { /* already disposed */ }
        }
    }

    /// <summary>
    /// Hard-cancel safety net for runs that are stuck in the registry
    /// despite an earlier <see cref="CancelRun(string)"/>. The natural
    /// path is: CancelRun → run hits its next cancellation checkpoint →
    /// finally block fires RunEnded → registry clears. Real-world bug
    /// (user-reported 2026-04-29): a run failed silently in init, the
    /// run-thread got stuck in a non-cancellable wait, and the card
    /// stayed on "Running…" with the Stop button doing nothing.
    /// This method is the escape hatch — synthesise a Failed RunRecord,
    /// fire <see cref="RunEnded"/> so subscribed VMs flip back to idle,
    /// then drop the registry entry. The original run thread is left
    /// alive (we can't safely abort an arbitrary Task); if it ever
    /// resumes its own finally will be a no-op (TryRemove already
    /// removed the entry, RunEnded subscribers see a no-op record).
    /// </summary>
    public void ForceFinaliseRun(string backupId, string reason)
    {
        if (!_runningRuns.TryRemove(backupId, out var info)) return;
        try { info.Cts.Cancel(); } catch { }

        // Snapshot the live record into a fresh instance instead of
        // mutating it in place. The orchestrator's RunningInfo.Record is
        // also handed to LogsViewModel via TryGetRunningRecord and lives
        // in an ObservableCollection — a concurrent ComboBox enumeration
        // of Errors/Cells could see torn state if we flipped Status from
        // Running → Skipped mid-bind. The snapshot lets the live record
        // stay immutable to its current observers; the new instance is
        // what gets persisted and surfaced via RunEnded.
        var live = info.Record;
        var record = new RunRecord
        {
            Id = live.Id,
            BackupId = live.BackupId,
            BackupName = live.BackupName,
            StartedUtc = live.StartedUtc,
            EndedUtc = DateTime.UtcNow,
            // The user's request was a Stop, not a generic Failure —
            // surface as Skipped so the card / banner / Logs view all
            // read "Manually skipped" rather than "Failed", which would
            // suggest the backup itself broke.
            Status = BackupRunStatus.Skipped,
            Summary = string.IsNullOrEmpty(live.Summary) || live.Summary == "Running…"
                ? reason
                : $"{live.Summary} · {reason}",
            LogPath = live.LogPath,
            RevisionNumber = live.RevisionNumber,
            BytesUploaded = live.BytesUploaded,
            FilesNew = live.FilesNew,
            FilesModified = live.FilesModified,
            FilesRemoved = live.FilesRemoved,
            Errors = new List<string>(live.Errors) { reason },
            Warnings = new List<string>(live.Warnings),
            // CellOutcome is a class, so a list-copy still aliases each
            // element. The natural-completion path can mutate live.Cells
            // entries after force-finalise persists; without per-cell
            // deep-copy, any subsequent mutation back-propagates into
            // the persisted record AND tears any LogsView binding still
            // pointing at the live record. Clone each cell explicitly.
            Cells = live.Cells.Select(c => new CellOutcome
            {
                SourcePath = c.SourcePath,
                DestinationName = c.DestinationName,
                StorageName = c.StorageName,
                Status = c.Status,
                Summary = c.Summary,
                AttemptsMade = c.AttemptsMade,
                LastError = c.LastError,
                Duration = c.Duration,
                LogPath = c.LogPath,
            }).ToList(),
        };
        _log.Warning("Force-finalising stuck run for backupId={Id}: {Reason}", backupId, reason);

        // Persist the synthetic outcome to the log store + Backup.LastRun
        // so a stuck-and-force-finalised run leaves the same audit trail
        // a natural completion does. Without this, the run is invisible
        // to the LogsView history and the Backup model still claims the
        // PRIOR run's status — so the card would briefly flip back to
        // "Manually skipped" then revert to whatever was there before
        // on the next config refresh.
        var backup = _config.Current.Backups.FirstOrDefault(b => b.Id == backupId);
        if (backup is not null)
        {
            try { _logs.AppendRun(record); }
            catch (Exception ex) { _log.Warning(ex, "AppendRun threw for force-finalise of {Id}", backupId); }
            try { UpdateBackupLastRun(backup, record); }
            catch (Exception ex) { _log.Warning(ex, "UpdateBackupLastRun threw for force-finalise of {Id}", backupId); }
        }

        try { _progress.ReportRunFinished(backupId); }
        catch (Exception ex) { _log.Warning(ex, "ReportRunFinished threw for force-finalise of {Id}", backupId); }
        try { RunEnded?.Invoke(backupId, record); }
        catch (Exception ex) { _log.Warning(ex, "RunEnded handler threw for force-finalise of {Id}", backupId); }
    }

    /// <summary>
    /// Blocks until the in-flight run for <paramref name="backupId"/> has
    /// removed itself from the registry, or the timeout elapses. Used by
    /// the delete flow to make sure the orchestrator isn't still writing
    /// repo metadata while we're deleting it.
    /// </summary>
    public async Task WaitForRunCompletionAsync(string backupId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (_runningRuns.ContainsKey(backupId) && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try { await Task.Delay(100, ct); } catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// Snapshot of every backup id currently registered as in flight.
    /// Used by <see cref="MaintenanceCoordinator"/> to fan-out
    /// cancellation before taking the maintenance gate. The dictionary
    /// is mutated by RunEnded fanout; iterating it directly would
    /// throw, so we materialise a list first.
    /// </summary>
    public IReadOnlyList<string> SnapshotRunningBackupIds() =>
        _runningRuns.Keys.ToArray();

    public async Task<RunRecord> RunBackupAsync(Backup backup, CancellationToken ct)
    {
        using var scope = Serilog.Context.LogContext.PushProperty("Backup", backup.Name);

        // Maintenance gate (Erase / Wipe / Prune). While one of these
        // sensitive ops holds the lock, NO backup may run — they need
        // exclusive access to the storage chain to avoid metadata
        // corruption. The coordinator returns immediately when no
        // maintenance is in flight (the common case), so this is
        // free 99% of the time. Cancellation here surfaces as a
        // Skipped RunRecord so callers downstream don't have to
        // catch OCE separately.
        try { await ServiceLocator.Maintenance.WaitForReadAsync(ct); }
        catch (OperationCanceledException)
        {
            return MakeSkippedRunRecord(backup, "Cancelled before run started.");
        }

        // Cross-process gate. See AcquireCrossProcessRunGate for the
        // full rationale. Returns (null, false) when the acquire was
        // refused — that means another instance (GUI or scheduled
        // task) is already running this backup id.
        var (crossProc, crossProcAcquired) = AcquireCrossProcessRunGate(backup);
        if (!crossProcAcquired)
        {
            crossProc?.Dispose();
            _log.Warning(
                "Run for {Name} already in progress in another process — refusing duplicate request", backup.Name);
            return MakeSkippedRunRecord(backup,
                "Skipped: another run for this backup is already in progress (scheduled task or another window).");
        }

        // From here on, the semaphore IS held. Wrap everything in a single
        // try/finally so a throw between this point and the inner try block
        // (e.g. a CTS allocation OOM, AppStatus throwing, _runningRuns
        // somehow throwing) cannot leak the semaphore across the user's
        // entire session. Earlier versions had a release-only-in-the-inner-
        // finally pattern that left a window where an exception would
        // permanently brick that backup id until process exit.
        try
        {
            // In-process gate. The cross-process semaphore alone would suffice
            // for serialisation, but the dictionary is what carries the CTS
            // for cancellation routing (CancelRun / WaitForRunCompletionAsync)
            // — the semaphore can't carry per-run state.
            // The Syncing lease is taken AFTER both gates so a refused duplicate
            // doesn't briefly flip the app's syncing icon for ~1µs.
            using var localCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Synthesize an in-flight Running record up-front; replace
            // it with the real outcome on success. This guarantees the
            // RunEnded event always carries a populated record, even if
            // RunBackupCoreAsync throws before returning one. The same
            // instance is also stashed in the running-info registry so
            // ForceFinaliseRun can mutate it when the user hard-cancels
            // a stuck run.
            //
            // EndedUtc MUST stay null while the run is in flight —
            // LogsViewModel.IsShowingLive checks for the (Status =
            // Running, EndedUtc = null) shape to pivot DisplayText to
            // LiveTail during a backup. Setting EndedUtc up-front made
            // the predicate fail, so the terminal pane stayed empty for
            // the entire run and only the persisted log appeared after
            // RunEnded swapped SelectedRun to the finished entry. The
            // user reported: "the terminal still doesn't show any logs
            // when running a backup; if I stop in the middle, then
            // it'll show log entries all at once."
            // The catch / finally branches below set EndedUtc to
            // DateTime.UtcNow when the run actually ends, so the
            // RunEnded payload is always a properly closed record.
            RunRecord result = new()
            {
                BackupId = backup.Id,
                BackupName = backup.Name,
                StartedUtc = DateTime.UtcNow,
                EndedUtc = null,
                Status = BackupRunStatus.Running,
                Summary = "Running…",
            };
            var runningInfo = new RunningInfo(localCts, result.StartedUtc, result);
            if (!_runningRuns.TryAdd(backup.Id, runningInfo))
            {
                _log.Warning("Run for {Name} already in progress — refusing duplicate request", backup.Name);
                return MakeSkippedRunRecord(backup,
                    "Skipped: another run for this backup is already in progress.");
            }

            // Re-check the maintenance gate now that we're registered.
            // The earlier WaitForReadAsync at the top of this method
            // was a snapshot read — between its return and the TryAdd
            // above, a concurrent Erase / Wipe / Prune could have
            // started its Acquire. The maintenance Acquire's snap-and-
            // cancel loop will see us in the registry and cancel us,
            // but that takes some time during which we'd otherwise be
            // doing setup work concurrent with the maintenance op.
            // Fast-path skip: if maintenance is now active, unwind
            // ourselves cleanly and return Skipped without waiting
            // for the cancel-loop to chase us. The maintenance
            // Acquire's NEXT loop iteration sees the empty registry
            // and proceeds.
            if (ServiceLocator.Maintenance.IsActive)
            {
                _runningRuns.TryRemove(backup.Id, out _);
                _log.Information(
                    "Run for {Name} aborting — a storage-maintenance task started during run startup", backup.Name);
                return MakeSkippedRunRecord(backup, "Skipped: storage maintenance is in progress.");
            }
            using var _syncing = AppStatus.BeginSyncing();
            ct = localCts.Token;
            try { RunStarted?.Invoke(backup.Id); }
            catch (Exception ex) { _log.Warning(ex, "RunStarted handler threw for {Name}", backup.Name); }

            // Per-destination QUEUEING. Two backups whose targets
            // overlap on a (DestId, StorageName) pair would otherwise
            // run their `prune` phases concurrently against the same
            // restic storage — restic's fossil-collection prune
            // is documented as preferring exclusive access, so two
            // simultaneous prunes can race on fossil deletion criteria.
            // Cross-process named semaphores let us serialise BOTH
            // the GUI's runs AND any --run scheduled-task invocations
            // that happen to fire at the same moment. The user
            // reported the underlying concern: "if both are scheduled
            // at the same time, can you confirm they'll be queued?"
            //
            // Acquired in deterministic key order so two backups that
            // share a SUBSET of destinations don't deadlock on
            // ABBA acquisition.
            var destinationSemaphores = new List<Semaphore>(backup.Targets.Count);
            try
            {
                await AcquireDestinationGatesAsync(backup, result, destinationSemaphores, ct);

                // Pass in `result` so RunBackupCoreAsync mutates the
                // SAME instance the registry is holding — TryGetRunningRecord
                // and ForceFinaliseRun then see live progress instead of
                // a stale "Running…" snapshot.
                await RunBackupCoreAsync(backup, result, ct);
                return result;
            }
            catch (OperationCanceledException)
            {
                // Cancellation while waiting on the destination gate
                // (or any other awaited cancellation point pre-cells).
                // Convert to a clean Skipped RunRecord so callers that
                // await RunBackupAsync see the same record-shaped result
                // as a cancelled mid-cells run, instead of having to
                // catch OCE separately. RunBackupCoreAsync's own
                // cancellation path (cells already running) classifies
                // the same way via ClassifyFromCells.
                result.Status = BackupRunStatus.Skipped;
                result.Summary = "Cancelled before run started.";
                result.EndedUtc = DateTime.UtcNow;
                return result;
            }
            catch (Exception ex)
            {
                result.Errors.Add(ex.Message);
                result.Summary = "Run threw before completing.";
                result.EndedUtc = DateTime.UtcNow;
                throw;
            }
            finally
            {
                // Release destination gates FIRST so a queued backup
                // can start as soon as the run actually finishes —
                // before we do the persist + alerts work below
                // (which can take a couple of seconds for slow disks
                // or remote toast services). The persisted history
                // file lives in AppData and doesn't touch the
                // destination, so this re-ordering is safe.
                ReleaseDestinationGates(destinationSemaphores);

                await FinaliseRunOwnershipAsync(backup, result);
            }
        }
        finally
        {
            // Always release + dispose the semaphore, even on the
            // early-return-Skipped path. Any throw between WaitOne(0)
            // succeeding and the inner try winds up here.
            try { crossProc?.Release(); } catch { /* not held — fine */ }
            try { crossProc?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Build the "we couldn't run, here's why" RunRecord that's
    /// returned from the early-out paths (cancelled before start,
    /// duplicate already running, maintenance in progress). The
    /// duplicated factory used to live inline in four places, each
    /// risking a small drift in field defaults. Single helper now
    /// guarantees identical shape regardless of which gate refused
    /// the run.
    /// </summary>
    private static RunRecord MakeSkippedRunRecord(Backup backup, string summary) => new()
    {
        BackupId = backup.Id,
        BackupName = backup.Name,
        StartedUtc = DateTime.UtcNow,
        EndedUtc = DateTime.UtcNow,
        Status = BackupRunStatus.Skipped,
        Summary = summary,
    };

    /// <summary>
    /// Take the cross-process named semaphore for this backup id.
    /// Returns (semaphore, acquired): caller checks <c>acquired</c>
    /// before proceeding. Falls back to "treat as acquired" if the
    /// kernel object can't be created (locked-down sandboxes,
    /// missing permissions) — better than refusing every backup
    /// in those environments. WaitOne failures, in contrast, are
    /// real refusals and propagate as <c>acquired=false</c>.
    /// </summary>
    private (Semaphore? semaphore, bool acquired) AcquireCrossProcessRunGate(Backup backup)
    {
        Semaphore? crossProc = null;
        var crossProcAcquired = false;
        try
        {
            // Named Semaphore (NOT Mutex) because the awaits below may
            // resume on different thread-pool threads and Mutex has
            // thread affinity (ReleaseMutex from a non-owning thread
            // throws ApplicationException).
            crossProc = new Semaphore(initialCount: 1, maximumCount: 1, name: MutexNameFor(backup.Id));
        }
        catch (Exception ex)
        {
            // Named-handle CREATION can fail in restricted environments
            // (locked-down sandboxes, exotic permission setups). Fall
            // through to the in-process gate alone — better than refusing
            // every backup. We deliberately DON'T catch a WaitOne failure
            // below the same way: a WaitOne throw means the kernel object
            // exists but we couldn't acquire (e.g. AbandonedMutex-class
            // states, handle-table exhaustion). Treating that as
            // "acquired" would let a duplicate cross-process run through
            // the gate.
            _log.Warning(ex, "Cross-process run gate unavailable for {Name}; using in-process gate only", backup.Name);
            return (null, true);
        }
        crossProcAcquired = crossProc.WaitOne(0);
        return (crossProc, crossProcAcquired);
    }

    /// <summary>
    /// Atomic ownership handoff for the end-of-run housekeeping. The
    /// thread that successfully <c>TryRemove</c>s the registry entry
    /// owns the five-step "persist + alerts" sequence; the loser
    /// (typically <see cref="ForceFinaliseRun"/>) does nothing. The
    /// TryRemove call IS the decision — no TOCTOU window.
    /// Order of work matters: AppendRun first (so the history file is
    /// the source of truth even if a later step throws), then
    /// UpdateBackupLastRun (config), then progress clear, then UI
    /// fanout, then alerts last (toast / email can be slow + remote
    /// and shouldn't gate the others).
    /// </summary>
    private async Task FinaliseRunOwnershipAsync(Backup backup, RunRecord result)
    {
        var weOwnFinalisation = _runningRuns.TryRemove(backup.Id, out _);
        if (!weOwnFinalisation)
        {
            _log.Information(
                "Run for {Name} was force-finalised before natural completion — skipping duplicate persist + RunEnded + alerts",
                backup.Name);
            return;
        }

        var duration = (result.EndedUtc ?? DateTime.UtcNow) - result.StartedUtc;
        _log.Information("Run ended: {Name} status={Status} duration={DurationSec:F1}s summary={Summary}",
            backup.Name, result.Status, duration.TotalSeconds, result.Summary ?? "");

        try { _logs.AppendRun(result); }
        catch (Exception ex) { _log.Warning(ex, "AppendRun threw for {Name}", backup.Name); }
        try { UpdateBackupLastRun(backup, result); }
        catch (Exception ex) { _log.Warning(ex, "UpdateBackupLastRun threw for {Name}", backup.Name); }
        try { _progress.ReportRunFinished(backup.Id); }
        catch (Exception ex) { _log.Warning(ex, "ReportRunFinished threw for {Name}", backup.Name); }
        try { RunEnded?.Invoke(backup.Id, result); }
        catch (Exception ex) { _log.Warning(ex, "RunEnded handler threw for {Name}", backup.Name); }
        try { await SendAlertsAsync(backup, result); }
        catch (Exception ex) { _log.Warning(ex, "SendAlertsAsync threw for {Name}", backup.Name); }
    }
}
