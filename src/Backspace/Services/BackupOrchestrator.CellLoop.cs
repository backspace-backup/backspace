using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;

namespace Backspace.Services;

// Cell-fan-out half of BackupOrchestrator: the per-(source × destination)
// loop, the in-cell retry cascade, and the cell↔run bookkeeping helpers
// (cell log path, cancel-reason resolution, cell-into-run merge, run
// classification). Kept in its own file so the main BackupOrchestrator
// stays focused on the public lifecycle, gates, and the in-flight
// registry.
public sealed partial class BackupOrchestrator
{
    /// <summary>
    /// Mutates <paramref name="final"/> with the run's outcome (status,
    /// cells, errors, summary, ended-utc) and returns the same instance.
    /// Earlier this method allocated a fresh RunRecord internally and
    /// returned it, leaving the caller's RunningInfo.Record reference
    /// pointing at the original synthetic forever — TryGetRunningRecord
    /// kept reporting "Running…" with no cells, and ForceFinaliseRun
    /// cloned the stale synthetic to persist instead of the real
    /// outcome's cell breakdown. Mutating the caller's instance keeps
    /// the registry's view in sync as the run progresses.
    /// </summary>
    private async Task<RunRecord> RunBackupCoreAsync(Backup backup, RunRecord final, CancellationToken ct)
    {

        var sources = backup.SourcePaths;
        _log.Information(
            "Run starting: sources={SourceCount} targets={TargetCount} threads={Threads} vss={Vss}",
            sources.Count, backup.Targets.Count, backup.Threads, backup.UseVss);

        // Both early-return paths below leave persist + alerts to the
        // outer RunBackupAsync's natural finally (gated on TryRemove
        // ownership). Calling them here would double-persist if force-
        // finalise hasn't fired and would race with force-finalise if
        // it has — same TOCTOU class as the main path. The early-return
        // is an empty-config fast-fail; the user gets one history
        // entry + one alert from the finally.
        if (sources.Count == 0)
        {
            final.EndedUtc = DateTime.UtcNow;
            final.Status = BackupRunStatus.Failed;
            final.Errors.Add("Backup has no source paths configured.");
            final.Summary = "No sources to back up.";
            return final;
        }

        if (backup.Targets.Count == 0)
        {
            // Fast-fail when the backup has zero destinations (e.g. the
            // user removed the only destination this backup pointed at
            // via the Destinations tab). Without this the orchestrator
            // walked an empty target loop and reported Success on a
            // run that didn't actually back anything up — a silent
            // data-loss footgun. The status text mentions exactly what
            // the user needs to do so they can fix it from the toast.
            final.EndedUtc = DateTime.UtcNow;
            final.Status = BackupRunStatus.Failed;
            final.Errors.Add("Backup has no destinations. Add one (or delete this backup) before running again.");
            final.Summary = "No destinations configured.";
            return final;
        }

        if (_engine is ResticBackrestEngine && OperatingSystem.IsWindows()
            && !backup.UseVss && !backup.VssDisabledAcknowledged)
        {
            final.EndedUtc = DateTime.UtcNow;
            final.Status = BackupRunStatus.Failed;
            final.Summary = "Backup stopped: Windows snapshots are disabled without your confirmation.";
            final.Errors.Add("Open this backup’s Advanced settings. Enable Windows snapshots, or explicitly accept that open and locked files may be missing.");
            return final;
        }

        if (HealthcheckAddress(backup) is { } hcUuid)
            await _hc.PingStartAsync(hcUuid, ct);

        // Pre-populate the (source × destination) cell matrix in the
        // progress service so the Overall denominator reflects the FULL
        // workload from t=0 — not just the cell that's currently
        // running. Without this, a 2-source × 2-destination run with
        // the first cell at 21% and three not-yet-started cells
        // displays Overall=21% (only one cell exists in the dictionary
        // → 21/1 = 21). With the matrix pre-registered we correctly
        // get 21/4 ≈ 5%, and per-source rolls up as "fraction of
        // destinations done" matching the user's mental model
        // (50% when one of two destinations finishes).
        var plannedDestNames = backup.Targets
            .Select(t => _config.Current.Destinations.FirstOrDefault(d => d.Id == t.DestinationId)?.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (plannedDestNames.Count > 0)
        {
            // Best-effort byte-weights: read from SourceSizeProbe's
            // cache so we don't burn IO walking each source tree at
            // run start (the user explicitly asked us not to). The
            // BackupCard's source-size labels populate this cache as
            // a side-effect of opening the Backups view, so for the
            // common case the weights are available. Sources without
            // a cached size silently fall back to equal weight inside
            // RecomputeOverall.
            var weights = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var src in sources)
            {
                var cached = ServiceLocator.SizeProbe.TryGetCached(src);
                if (cached is { Bytes: > 0 } r) weights[src] = r.Bytes;
            }
            _progress.RegisterRunCells(backup.Id, sources, plannedDestNames, weights);
        }

        // Run-wide cancellation: user cancel OR metered-network watchdog.
        // Cell failures never trigger this — they're isolated below.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watchdogTask = StartMeteredWatchdogIfEnabled(backup, linked, _ =>
        {
            // Record the reason on the running-info BEFORE signalling
            // cancellation so the cell loop sees it when its
            // OperationCanceledException fires. Atomic publication —
            // if the user pressed Stop microseconds earlier their
            // reason wins (TrySetCancelReason no-ops here).
            if (_runningRuns.TryGetValue(backup.Id, out var info))
            {
                info.TrySetCancelReason("Network became metered (mobile / tethered); backup paused per backup setting.");
            }
        });

        try
        {
            // Outer loop: sources. Inner loop: targets. Order matters
            // only cosmetically — we run linearly because a multi-GB
            // backup already saturates bandwidth; parallel cells would
            // just trash the disk + network queue.
            // Distinct against the (DestinationId, StorageName) pair so
            // a hand-edited config.json with duplicate target rows
            // doesn't fan out into multiple identical cells. Identical
            // cells previously truncated each other's per-cell .log
            // file (BuildCellLogPath returns the same path) and
            // produced indistinguishable Backrest operation event
            // streams (same planId). The editor blocks dupes upstream;
            // this defends against direct config-file edits and the
            // migrator's bulk-import path.
            // Lowercase the StorageName component so a hand-edited
            // config with "default" vs "Default" doesn't bypass the
            // dedup. MakeRepoId / Slug downstream lowercase the entire
            // id, so two casing-only variants reach the same Backrest
            // repo — fanning out as two cells would re-introduce the
            // truncated-log + ambiguous-event-stream bug.
            var distinctTargets = DistinctTargets(backup.Targets);
            foreach (var sourcePath in sources)
            {
                if (linked.IsCancellationRequested) break;

                foreach (var target in distinctTargets)
                {
                    if (linked.IsCancellationRequested) break;

                    var dest = _config.Current.Destinations.FirstOrDefault(d => d.Id == target.DestinationId);
                    if (dest is null)
                    {
                        _log.Warning("Target references missing destination {DestId}", target.DestinationId);
                        final.Cells.Add(new CellOutcome
                        {
                            SourcePath = sourcePath,
                            DestinationName = "(missing)",
                            StorageName = target.StorageName,
                            Status = BackupRunStatus.Failed,
                            LastError = $"Destination {target.DestinationId} not found in config.",
                        });
                        final.Errors.Add($"Destination {target.DestinationId} not found");
                        continue;
                    }

                    // Volume-mount check for local-like destinations.
                    // A missing drive is Skipped (not Failed) — the
                    // unattended CLI treats that as a graceful outcome.
                    if (dest.IsLocalLike)
                    {
                        var vc = VolumeDetector.Check(dest.ExpectedDriveLetter, dest.ExpectedVolumeLabel);
                        if (vc.Verdict != VolumeDetector.Verdict.Mounted)
                        {
                            _log.Information("Skipping cell {Src}→{Dest}: {Detail}", sourcePath, dest.Name, vc.Detail);
                            final.Cells.Add(new CellOutcome
                            {
                                SourcePath = sourcePath,
                                DestinationName = dest.Name,
                                StorageName = target.StorageName,
                                Status = BackupRunStatus.Skipped,
                                Summary = vc.Detail,
                            });
                            final.Warnings.Add($"[{sourcePath} → {dest.Name}] skipped: {vc.Detail}");
                            continue;
                        }
                    }

                    // Reachability gate. Per-kind cheap pre-flight that
                    // shortcuts a Skipped cell when restic itself would
                    // otherwise burn minutes of retry against a
                    // destination we already know is offline. Today
                    // only NetworkShare has a sub-second probe distinct
                    // from VolumeDetector's drive check; future
                    // backends (SFTP, REST, rclone-routed cloud) can
                    // hook in here without modifying the cell loop's
                    // top-level shape.
                    var gateMiss = await ProbeReachabilityAsync(dest, sourcePath, target.StorageName, ct).ConfigureAwait(false);
                    if (gateMiss is not null)
                    {
                        _log.Information("Skipping cell {Src}→{Dest}: {Detail}", sourcePath, dest.Name, gateMiss.Summary);
                        final.Cells.Add(gateMiss);
                        if (gateMiss.Status == BackupRunStatus.Failed)
                        {
                            final.Errors.Add($"[{sourcePath} → {dest.Name}] {gateMiss.LastError}");
                            if (HealthcheckAddress(backup) is { } gatePing && _healthcheckFailedRuns.TryAdd(final.Id, 0))
                                if (!await _hc.PingFailureAsync(gatePing, BuildEmailBody(backup, final), CancellationToken.None))
                                    _healthcheckFailedRuns.TryRemove(final.Id, out _);
                        }
                        else final.Warnings.Add($"[{sourcePath} → {dest.Name}] skipped: {gateMiss.Summary}");
                        continue;
                    }

                    // Per-cell progress lifecycle: report start → forward
                    // every stdout line through the progress service so
                    // the BackupCard can render a live percentage → mark
                    // cell ended on completion. The line callback is
                    // passed AS A PARAMETER all the way to the runner
                    // (rather than via the runner's shared LineWritten
                    // event) so concurrent runs against the same runner
                    // singleton can't cross-attribute lines: cell A's
                    // handler sees only cell A's stdout. The runner
                    // still raises LineWritten for the global "live
                    // tail" subscriber (LogsViewModel) which wants the
                    // union of every active backup.
                    var capturedSourcePath = sourcePath;
                    var capturedDestName = dest.Name;
                    _progress.ReportCellStarted(backup.Id, sourcePath, capturedDestName);

                    // Per-cell log file. The engine doesn't write one
                    // itself — it streams operation events over the
                    // Connect-Go RPC instead — so without this tee a
                    // failed cell would leave the user with an empty
                    // "Selected run" pane and nowhere to look for the
                    // actual stdout that caused the failure.
                    // File-write errors are swallowed so a
                    // disk-full / locked-log doesn't cascade into the
                    // backup itself failing.
                    var cellLogPath = BuildCellLogPath(backup, sourcePath, dest, target);
                    StreamWriter? cellLogWriter = null;
                    try
                    {
                        cellLogWriter = new StreamWriter(cellLogPath, append: false)
                        {
                            AutoFlush = true,
                        };
                        cellLogWriter.WriteLine($"--- Backspace cell log ---");
                        cellLogWriter.WriteLine($"backup        : {backup.Name}");
                        cellLogWriter.WriteLine($"source        : {sourcePath}");
                        cellLogWriter.WriteLine($"destination   : {dest.Name} ({dest.Kind})");
                        cellLogWriter.WriteLine($"storage-name  : {target.StorageName}");
                        cellLogWriter.WriteLine($"started (UTC) : {DateTime.UtcNow:o}");
                        cellLogWriter.WriteLine();
                    }
                    catch (Exception ex) { _log.Warning(ex, "Couldn't open cell log {Path}", cellLogPath); }

                    object writerLock = new();
                    Action<string> lineHandler = line =>
                    {
                        _progress.ReportLine(backup.Id, capturedSourcePath, capturedDestName, line);
                        // Broadcast on the engine's LineWritten so the
                        // Logs view's live-tail picks it up. Without
                        // this, orchestrator-level breadcrumbs
                        // ("[orchestrator] attempt 1 starting at …",
                        // retry/failure markers) only landed in the
                        // cell log FILE — the in-app log pane stayed
                        // empty until cell.LogPath was set in the
                        // finally block, leaving the user staring at
                        // "0%" with no signs of life.
                        try { _engine.RaiseLineWritten(line); } catch { /* live-tail listener failure can't escape */ }
                        if (cellLogWriter is null) return;
                        // Lock so the engine emitting from multiple
                        // background threads doesn't interleave bytes.
                        try { lock (writerLock) cellLogWriter.WriteLine(line); }
                        catch { /* file dispose race; ignore */ }
                    };
                    // Initialise with a Failed sentinel rather than null! so an
                    // exception escaping RunCellWithRetryAsync (it shouldn't
                    // — it has its own catch — but defensively) leaves a
                    // valid CellOutcome the finally block and the cell list
                    // can both reference.
                    var cellOutcome = new CellOutcome
                    {
                        SourcePath = sourcePath,
                        DestinationName = dest.Name,
                        StorageName = target.StorageName,
                        Status = BackupRunStatus.Failed,
                        Summary = "did not start",
                        // Stamp the log path BEFORE the cell starts so the
                        // in-app log viewer can tail the file live —
                        // previously this was only set in the finally
                        // block, so a running cell showed an empty log
                        // pane until completion. Cells that fail before
                        // RunCellWithRetryAsync gets a chance to update
                        // it still carry the path to disk.
                        LogPath = cellLogPath,
                    };
                    try
                    {
                        cellOutcome = await RunCellWithRetryAsync(backup, sourcePath, dest, target, linked.Token, lineHandler);
                        if (string.IsNullOrEmpty(cellOutcome.LogPath))
                            cellOutcome.LogPath = cellLogPath;
                    }
                    catch (Exception ex)
                    {
                        // Defensive: any unexpected exception (NRE in our own
                        // code, OOM, etc.) is recorded as a cell-level
                        // failure with the message so the run summary and
                        // the per-run log surface what happened.
                        _log.Error(ex, "Cell {Src}→{Dest} threw an unexpected exception", sourcePath, dest.Name);
                        cellOutcome.LastError = ex.Message;
                        cellOutcome.Summary = "Unexpected error. Check Activity & logs for the full message.";
                    }
                    finally
                    {
                        _progress.ReportCellEnded(backup.Id, sourcePath, capturedDestName,
                            success: cellOutcome.Status == BackupRunStatus.Success);

                        // Finalise the cell log: write the cell-level
                        // outcome to the file so even a "the engine
                        // never said anything" failure (sidecar not
                        // running, connection refused) leaves a
                        // useful breadcrumb. Without this the
                        // user-reported "Logs are empty!" case stays
                        // empty.
                        if (cellLogWriter is not null)
                        {
                            try
                            {
                                lock (writerLock)
                                {
                                    cellLogWriter.WriteLine();
                                    cellLogWriter.WriteLine($"--- cell outcome ---");
                                    cellLogWriter.WriteLine($"status        : {cellOutcome.Status}");
                                    cellLogWriter.WriteLine($"attempts      : {cellOutcome.AttemptsMade}");
                                    cellLogWriter.WriteLine($"summary       : {cellOutcome.Summary}");
                                    if (!string.IsNullOrEmpty(cellOutcome.LastError))
                                        cellLogWriter.WriteLine($"last error    : {cellOutcome.LastError}");
                                    cellLogWriter.WriteLine($"ended (UTC)   : {DateTime.UtcNow:o}");
                                }
                            }
                            catch { /* best-effort */ }
                            try { cellLogWriter.Dispose(); } catch { }
                        }
                        if (string.IsNullOrEmpty(cellOutcome.LogPath))
                            cellOutcome.LogPath = cellLogPath;
                    }
                    final.Cells.Add(cellOutcome);
                    MergeCellInto(final, cellOutcome);
                    if (cellOutcome.Status == BackupRunStatus.Failed && HealthcheckAddress(backup) is { } ping
                        && _healthcheckFailedRuns.TryAdd(final.Id, 0))
                        if (!await _hc.PingFailureAsync(ping, BuildEmailBody(backup, final), CancellationToken.None))
                            _healthcheckFailedRuns.TryRemove(final.Id, out _);

                    _log.Information(
                        "Cell {Src}→{Dest} finished: status={Status} attempts={Attempts} {Summary}",
                        sourcePath, dest.Name, cellOutcome.Status, cellOutcome.AttemptsMade, cellOutcome.Summary);
                }
            }
        }
        finally
        {
            try { linked.Cancel(); } catch { }
            if (watchdogTask is not null) { try { await watchdogTask; } catch { } }
        }

        final.EndedUtc = DateTime.UtcNow;
        var plannedCells = sources.Count * DistinctTargets(backup.Targets).Count;
        if (ct.IsCancellationRequested && final.Cells.Count < plannedCells)
            final.Warnings.Add("Backup stopped before all sources and destinations were processed.");
        final.Status = ClassifyFromCells(final, ct, plannedCells);
        // Pass the orchestrator's recorded cancel reason as a fallback
        // so the run-level Summary still reads "Manually skipped after
        // 1m 12s" even when the user clicked Stop BEFORE any cell got
        // a chance to attach its own per-cell skip reason. Without this,
        // an early Stop produced a generic "Skipped after 1m 12s" line
        // that hid which actor cancelled (user / metered watchdog /
        // force-finalise).
        var orchestratorReason = _runningRuns.TryGetValue(backup.Id, out var info)
            ? info.CancelReason
            : null;
        final.Summary = BuildSummary(final, orchestratorReason, backup, _config.Current);

        // Persistence + alerts are NOT done here — they're now done in
        // the outer RunBackupAsync's finally block, gated on the
        // TryRemove that authoritatively decides who owns finalisation
        // (this thread or ForceFinaliseRun). The previous implementation
        // had a TOCTOU window where a `ContainsKey` check here could
        // return true, ForceFinaliseRun could TryRemove + persist, and
        // we would then ALSO persist — producing two history entries
        // with the same StartedUtc and a duplicate alert.
        return final;
    }

    /// <summary>
    /// Per-cell retry cascade. Transient failures (network blip, remote
    /// rate-limit, brief locked-file contention) deserve up to 3 tries
    /// with 5s → 20s → 60s backoff. Post-3 we mark the cell Failed and
    /// move on — we do <em>not</em> cancel the enclosing run.
    /// </summary>
    private async Task<CellOutcome> RunCellWithRetryAsync(
        Backup backup,
        string sourcePath,
        Destination dest,
        BackupTarget target,
        CancellationToken ct,
        Action<string>? lineSink = null)
    {
        var cell = new CellOutcome
        {
            SourcePath = sourcePath,
            DestinationName = dest.Name,
            StorageName = target.StorageName,
            Status = BackupRunStatus.Running,
        };

        // First try runs immediately; subsequent tries wait the
        // cascade duration. See CellRetryBackoffs at the top of the
        // class for the cascade rationale.
        var started = DateTime.UtcNow;
        RunRecord? lastRun = null;
        string? lastError = null;
        // Per-cell 60-second budget for TransientRetriable failures
        // (Defender / AV file lock, momentary file-system permission
        // race). Starts ticking at the FIRST transient failure so a
        // backup that succeeds on attempt 1 doesn't suffer from a
        // budget. Once the budget is breached, the next transient
        // failure escalates the cell to Failed with the original
        // error surfaced, instead of riding the cascade to
        // exhaustion. Intolerable failures use the existing cascade
        // unchanged; tolerable failures fast-skip as before.
        DateTime? transientDeadline = null;
        const int TransientBudgetSeconds = 60;

        for (int attempt = 0; attempt < CellRetryBackoffs.Length; attempt++)
        {
            if (ct.IsCancellationRequested) break;

            if (CellRetryBackoffs[attempt] > TimeSpan.Zero)
            {
                lineSink?.Invoke($"[orchestrator] retry {attempt + 1}/{CellRetryBackoffs.Length} after {CellRetryBackoffs[attempt].TotalSeconds:F0}s backoff…");
                try { await Task.Delay(CellRetryBackoffs[attempt], ct); }
                catch (TaskCanceledException) { break; }
            }

            cell.AttemptsMade = attempt + 1;
            // Stamp every attempt in the cell log so the user has a
            // breadcrumb of progress while the operation runs (the
            // engine's own LineWritten only fires on operation-event
            // transitions, which can be silent for tens of seconds).
            lineSink?.Invoke($"[orchestrator] attempt {attempt + 1} starting at {DateTime.UtcNow:o}");

            try
            {
                lastRun = _engine is ResticBackrestEngine && VssElevationPolicy.Evaluate(backup).RequiresElevation
                    ? await new VssElevationCoordinator().RunElevatedAsync(new VssElevationRequest(backup, sourcePath, dest, target.StorageName), ct)
                    : await _engine.BackupPruneCheckAsync(backup, sourcePath, dest, target.StorageName, ct, lineSink);
            }
            catch (OperationCanceledException)
            {
                cell.Status = BackupRunStatus.Skipped;
                cell.Summary = ResolveCancelReason(backup.Id);
                cell.Duration = DateTime.UtcNow - started;
                lineSink?.Invoke($"[orchestrator] cancelled: {cell.Summary}");
                return cell;
            }
            catch (Exception ex)
            {
                // Single classifier-driven catch. Order matters here:
                //   1. Tolerable wins first because
                //      EngineUnreachableDestinationException is both
                //      ITolerable and INonRetriable — we want it
                //      surfaced as Skipped (laptop offline, NAS
                //      unreachable), not Failed.
                //   2. INonRetriable then short-circuits the cascade
                //      for init failures, bad preferences, decrypt
                //      mismatches, and missing-engine cases.
                //   3. TransientRetriable enters the 60s retry budget.
                //   4. Anything else falls through to the cascade
                //      backoff (next attempt below).
                // Round 10 dedup: previously these branches lived in
                // THREE separate catch blocks (ITolerable filter,
                // INonRetriable filter, catch-all running Classify
                // again). The repetition forced the
                // EngineUnreachableDestinationException structured-
                // reason path to be duplicated in two of the three
                // blocks, and a future refactor was at risk of
                // dropping one copy. The classifier is now the
                // single source of truth for Tolerable; the marker
                // interface only feeds it (ex is ITolerable returns
                // Tolerable).
                var verdict = EngineErrorClassifier.Classify(ex);
                if (verdict == EngineErrorClassifier.Verdict.Tolerable)
                {
                    var reason = (ex as EngineUnreachableDestinationException)?.Reason
                                 ?? EngineErrorClassifier.ReasonForTolerable(ex.Message);
                    cell.Status = BackupRunStatus.Skipped;
                    cell.LastError = ex.Message;
                    cell.Summary = reason;
                    cell.Duration = DateTime.UtcNow - started;
                    _log.Information(ex,
                        "Cell {Src}→{Dest} tolerable skip: {Reason}",
                        sourcePath, dest.Name, reason);
                    lineSink?.Invoke($"[orchestrator] tolerable skip: {reason}");
                    return cell;
                }
                if (ex is INonRetriable)
                {
                    // Fail-fast for known non-retriable errors (init
                    // failures, bad preferences, decrypt mismatches,
                    // missing engine binary). Earlier we'd burn 91s of
                    // 5s/20s/60s backoff repeating the same config
                    // error. Surface the underlying exception's
                    // message verbatim so the failure toast / email /
                    // Activity log render the captured engine output,
                    // not a generic "failed".
                    lastError = ex.Message;
                    cell.Status = BackupRunStatus.Failed;
                    cell.LastError = ex.Message;
                    cell.Summary = "failed (non-retriable)";
                    cell.Duration = DateTime.UtcNow - started;
                    _log.Warning(ex,
                        "Cell {Src}→{Dest} hit non-retriable error — skipping retry cascade",
                        sourcePath, dest.Name);
                    lineSink?.Invoke($"[orchestrator] non-retriable failure: {ex.Message}");
                    return cell;
                }
                if (verdict == EngineErrorClassifier.Verdict.TransientRetriable)
                {
                    // Transient retry budget: 60s total wall-clock
                    // window for the transient cause to resolve. The
                    // budget interacts with the cascade differently
                    // in prod vs test:
                    //   • Prod cascade (0+5+20+60s = 85s total) is
                    //     already longer than the 60s budget. The
                    //     deadline check below fires at attempt 3
                    //     and escalates to Failed BEFORE the cascade
                    //     exhausts — so the user sees the transient
                    //     reason surfaced cleanly. The extension via
                    //     `attempt--` is dead code in prod because
                    //     the cascade-internal sleep is too long.
                    //   • Test/fast cascade (0+100+200+500ms = 800ms
                    //     total) is SHORTER than the budget. The
                    //     extension fires, keeping the cell at the
                    //     last cascade index until the budget elapses.
                    // Net effect: the user always gets ~60s of retry
                    // before a transient cell escalates to Failed,
                    // regardless of which cascade is active.
                    transientDeadline ??= DateTime.UtcNow.AddSeconds(TransientBudgetSeconds);
                    if (DateTime.UtcNow >= transientDeadline.Value)
                    {
                        lastError = ex.Message;
                        cell.Status = BackupRunStatus.Failed;
                        cell.LastError = ex.Message;
                        cell.Summary = $"transient failure persisted past {TransientBudgetSeconds}s budget";
                        cell.Duration = DateTime.UtcNow - started;
                        _log.Warning(ex,
                            "Cell {Src}→{Dest} transient retry budget ({Budget}s) exhausted",
                            sourcePath, dest.Name, TransientBudgetSeconds);
                        lineSink?.Invoke($"[orchestrator] transient retry budget exhausted: {ex.Message}");
                        return cell;
                    }
                    // Mid-budget: keep retrying. If the cascade is
                    // about to run out of attempts (last iteration of
                    // the for-loop) but the budget hasn't been
                    // breached, extend by manually waiting and looping
                    // back to a fresh attempt within the budget window.
                    // The cascade's `for` bound is `attempt <
                    // CellRetryBackoffs.Length`; the manual-loop
                    // pattern below preserves the cascade's last
                    // backoff cadence and re-enters the try-catch.
                    var remaining = (transientDeadline.Value - DateTime.UtcNow).TotalSeconds;
                    _log.Information(ex,
                        "Cell {Src}→{Dest} transient retry — {Remaining:F0}s of budget left (cascade attempt {Attempt}/{Max})",
                        sourcePath, dest.Name, remaining, attempt + 1, CellRetryBackoffs.Length);
                    lineSink?.Invoke($"[orchestrator] transient retry ({remaining:F0}s of budget left): {ex.Message}");
                    lastError = ex.Message;
                    // If the cascade is at its last iteration but the
                    // budget allows more time, decrement `attempt` so
                    // the for-loop's increment re-enters at the last
                    // cascade index — the top-of-loop's existing
                    // Task.Delay will provide the inter-retry sleep
                    // using the cascade's last backoff cadence. Don't
                    // sleep INLINE here too — round-4 review caught
                    // the double-sleep (inline + top-of-loop) that
                    // halved the effective retry rate.
                    if (attempt == CellRetryBackoffs.Length - 1
                        && DateTime.UtcNow < transientDeadline.Value)
                    {
                        attempt--;
                    }
                    continue;
                }
                lastError = ex.Message;
                _log.Warning(ex, "Cell {Src}→{Dest} attempt {Attempt} threw", sourcePath, dest.Name, attempt + 1);
                lineSink?.Invoke($"[orchestrator] attempt {attempt + 1} failed: {ex.Message}");
                continue;
            }

            // Success or benign warning → commit and return.
            if (lastRun.Status == BackupRunStatus.Success || lastRun.Status == BackupRunStatus.Warning)
            {
                cell.Status = lastRun.Status;
                cell.Summary = lastRun.Summary;
                cell.Duration = DateTime.UtcNow - started;
                // Carry the per-attempt log file path up so the
                // LogsView's "Selected run" pane has something to
                // render. Earlier this was only set on init failures,
                // so a successful run AND a retry-exhausted failure
                // both surfaced empty logs in the UI even though the
                // engine stdout was on disk.
                cell.LogPath = lastRun.LogPath ?? "";
                // Promote per-cell metrics into the caller's RunRecord
                // via MergeCellInto on the way out; here we record the
                // cell-level numbers.
                return cell;
            }

            // Skipped → don't retry; treat as a cell-level decision.
            if (lastRun.Status == BackupRunStatus.Skipped)
            {
                cell.Status = BackupRunStatus.Skipped;
                cell.Summary = lastRun.Summary;
                cell.Duration = DateTime.UtcNow - started;
                cell.LogPath = lastRun.LogPath ?? "";
                return cell;
            }

            lastError = lastRun.Errors.Count > 0 ? lastRun.Errors[^1] : null;
            _log.Warning(
                "Cell {Src}→{Dest} attempt {Attempt} failed — will retry ({Remaining} left)",
                sourcePath, dest.Name, attempt + 1, CellRetryBackoffs.Length - attempt - 1);
        }

        cell.Status = ct.IsCancellationRequested ? BackupRunStatus.Skipped : BackupRunStatus.Failed;
        cell.Summary = cell.Status == BackupRunStatus.Skipped
            ? ResolveCancelReason(backup.Id)
            : "failed after retries";
        // Only carry forward LastError on the FAILED branch. Round-8
        // review caught: a cancellation that fired between two retry
        // attempts (where lastError came from a prior engine failure
        // we hadn't given up on yet) used to bleed that prior-attempt
        // error onto the cancelled-Skipped cell, which then triggered
        // the LocateMovedHint and surfaced misleading "your folder
        // moved" advice for a cell the USER cancelled. On the Skipped
        // branch, the cancellation reason already lives in Summary —
        // no engine-level LastError to attach.
        //
        // Side effect noted by round-9 review: the per-cell email
        // body's "error: <text>" line at Reporting.cs no longer
        // surfaces the last engine error for a cancel-mid-retry
        // case. Accepted: the email entry now reads as a clean
        // "Skipped — Manually skipped after 12s" line instead of
        // "Skipped — Manually skipped …  error: Fatal: network path
        // was not found", which previously implied the network blip
        // was the actual reason for the skip (it wasn't — the user
        // cancelled). If users want the diagnostic context, the
        // per-cell log file (cell.LogPath) preserves the full
        // engine output.
        if (cell.Status == BackupRunStatus.Failed) cell.LastError = lastError;
        cell.Duration = DateTime.UtcNow - started;
        // Final attempt's log is the most useful for diagnosing why
        // the cascade gave up — surface it on the cell so the
        // history.json record carries it through to the LogsView.
        if (lastRun is not null && !string.IsNullOrEmpty(lastRun.LogPath))
            cell.LogPath = lastRun.LogPath;
        return cell;
    }

    /// <summary>
    /// Build the per-cell log path under the per-backup engine log
    /// dir. One file per (timestamp × source × destination × storage
    /// name) so the LogsView can show a cell's stdout in isolation
    /// without concurrent cells' lines interleaved. Misnamed for
    /// historic reasons — this method does NOT open a file handle;
    /// the caller opens the StreamWriter at the returned path. See
    /// the call site in <see cref="RunBackupCoreAsync"/>.
    /// </summary>
    private static string BuildCellLogPath(Backup backup, string sourcePath, Destination dest, BackupTarget target)
    {
        var dir = AppPaths.LogDirForBackup(backup.Name);
        try { Directory.CreateDirectory(dir); } catch { /* if this fails the StreamWriter will too */ }
        var srcSlug = AppPaths.Sanitize(Path.GetFileName(sourcePath.TrimEnd('\\', '/')));
        if (string.IsNullOrEmpty(srcSlug)) srcSlug = "root";
        var destSlug = AppPaths.Sanitize(dest.Name);
        return LogStore.NewTranscriptPath(backup.Name, $"{srcSlug}-{destSlug}-{target.StorageName}", DateTime.UtcNow);
    }

    /// <summary>
    /// Read the cancel-reason recorded on the running-info for this
    /// backup, or fall back to a generic "Cancelled" if no caller
    /// stamped a reason. Used by every cell-level Skipped summary so
    /// the LogsView and the card status text both surface the WHY
    /// of the skip rather than a generic "cancelled" line.
    /// </summary>
    private string ResolveCancelReason(string backupId)
    {
        if (_runningRuns.TryGetValue(backupId, out var info)
            && !string.IsNullOrEmpty(info.CancelReason))
            return info.CancelReason!;
        return "Cancelled";
    }

    /// <summary>
    /// Roll cell-level metrics up into the run's top-line counters so
    /// the dashboard and summary line still work. Cells retain the
    /// per-cell breakdown for the detail view.
    /// </summary>
    private static void MergeCellInto(RunRecord run, CellOutcome cell)
    {
        // We don't have per-cell bytes/files in CellOutcome today —
        // they live on the RunRecord returned from the runner, which
        // RunCellWithRetryAsync currently discards. If we need per-cell
        // upload stats in the UI later, thread them through CellOutcome;
        // for now the tile shows aggregate + per-cell Status only.
        if (cell.Status == BackupRunStatus.Failed && cell.LastError is { } err)
            run.Errors.Add($"[{cell.SourcePath} → {cell.DestinationName}] {err}");

        // Propagate the first cell's log path up to the run record so
        // the LogsView can locate something to render in the Selected
        // run pane. Multi-cell runs share a backup-name log dir; one
        // path is enough to navigate the user there.
        if (string.IsNullOrEmpty(run.LogPath) && !string.IsNullOrEmpty(cell.LogPath))
            run.LogPath = cell.LogPath;
    }

    private static BackupRunStatus ClassifyFromCells(RunRecord r, CancellationToken ct, int plannedCells)
    {
        var hasFailure = r.Cells.Any(c => c.Status == BackupRunStatus.Failed);
        var hasWarning = r.Cells.Any(c => c.Status == BackupRunStatus.Warning);
        var hasSkipped = r.Cells.Any(c => c.Status == BackupRunStatus.Skipped) || r.Cells.Count < plannedCells;
        var hasSuccess = r.Cells.Any(c => c.Status == BackupRunStatus.Success);

        // Stopping later work does not erase a failure already reported, and
        // saving only part of the requested backup is not a complete success.
        if (hasFailure) return BackupRunStatus.Failed;
        if (hasWarning) return BackupRunStatus.Warning;
        if (hasSuccess) return hasSkipped ? BackupRunStatus.Warning : BackupRunStatus.Success;
        // All cells skipped, no failure → overall Skipped (e.g. every
        // external drive unplugged).
        if (hasSkipped || ct.IsCancellationRequested) return BackupRunStatus.Skipped;
        return BackupRunStatus.Failed;
    }

    /// <summary>
    /// Shared metered-network watchdog wiring used by both the live
    /// backup loop (<see cref="BackupOrchestrator.CellLoop"/>) and
    /// the standalone maintenance loop
    /// (<see cref="BackupOrchestrator.Maintenance"/>). Returns the
    /// long-running watchdog task so the caller can await it before
    /// disposing the linked cts, or <c>null</c> if the backup didn't
    /// opt in to mid-run metered abort. The watchdog cancels
    /// <paramref name="linked"/> on its own — the
    /// <paramref name="onMetered"/> callback fires FIRST so the
    /// caller can record state (cancel reason, abort flag) before
    /// the downstream code sees an <see cref="OperationCanceledException"/>.
    /// Two callers previously inlined this with subtly different
    /// emit-then-cancel ordering, which an adversarial review
    /// caught as a future-drift risk for the cancel-reason
    /// publication race.
    /// </summary>
    /// <summary>
    /// Per-destination-kind reachability gate. Returns a Skipped
    /// <see cref="CellOutcome"/> if the destination is known
    /// offline via a cheap pre-flight probe; <c>null</c> if the
    /// gate doesn't apply or the probe succeeded. Today only
    /// NetworkShare has a per-call probe — restic against an
    /// unreachable UNC retries with exponential backoff for ~13
    /// minutes before giving up (measured against a missing
    /// localhost share: 812 s wall time vs. 19 ms for
    /// <see cref="Directory.Exists(string)"/>). ExternalDrive is
    /// already handled by VolumeDetector upstream; cloud
    /// destinations rely on restic's own connectivity check
    /// because we don't have cheap per-cloud probes that wouldn't
    /// re-implement the rclone driver. The 5 s timeout matches
    /// the per-call deadline used elsewhere in the cell loop.
    /// </summary>
    internal static async Task<CellOutcome?> ProbeReachabilityAsync(
        Destination dest, string sourcePath, string storageName, CancellationToken ct)
    {
        if (dest.Kind != DestinationKind.NetworkShare
            || string.IsNullOrWhiteSpace(dest.PathOrSubpath))
        {
            return null;
        }
        var unc = dest.PathOrSubpath!;
        var probe = Task.Run(() =>
        {
            try
            {
                var attributes = File.GetAttributes(unc);
                return (attributes & FileAttributes.Directory) != 0 ? null
                    : new IOException("The network destination is not a folder.");
            }
            catch (Exception ex) { return ex; }
        }, ct);
        var completed = await Task.WhenAny(probe, Task.Delay(TimeSpan.FromSeconds(5), ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var failure = completed == probe ? await probe.ConfigureAwait(false) : new TimeoutException("Network share timed out.");
        if (failure is null) return null;
        var tolerable = IsTolerableShareFailure(failure);
        return new CellOutcome
        {
            SourcePath = sourcePath,
            DestinationName = dest.Name,
            StorageName = storageName,
            Status = tolerable ? BackupRunStatus.Skipped : BackupRunStatus.Failed,
            Summary = tolerable ? $"Network share unavailable: {unc}" : $"Cannot access network destination: {unc}",
            LastError = failure.Message,
        };
    }

    internal static bool IsTolerableShareFailure(Exception error)
    {
        if (error is UnauthorizedAccessException or DirectoryNotFoundException or FileNotFoundException or ArgumentException) return false;
        if (error is TimeoutException) return true;
        // Preserve the OS reason instead of Directory.Exists swallowing access
        // denial and missing paths. These Win32 codes describe known outages.
        var code = error.HResult & 0xffff;
        return code is 53 or 64 or 121 or 1222 or 1231 or 1232
            || EngineErrorClassifier.Classify(error.Message) == EngineErrorClassifier.Verdict.Tolerable;
    }
    internal Task? StartMeteredWatchdogIfEnabled(
        Backup backup,
        CancellationTokenSource linked,
        Action<string> onMetered)
    {
        if (!backup.AbortOnMeteredNetwork) return null;
        var watchdog = MeteredWatchdogFactory();
        watchdog.MeteredDetected += reason =>
        {
            onMetered(reason);
            try { linked.Cancel(); } catch { /* already disposed */ }
        };
        return Task.Run(() => watchdog.RunAsync(linked.Token), linked.Token);
    }
}
