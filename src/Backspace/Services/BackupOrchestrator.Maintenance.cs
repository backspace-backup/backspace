using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Serilog.Context;

namespace Backspace.Services;

// Stand-alone maintenance (prune / check) half of BackupOrchestrator —
// fired by the OS scheduler from a Backup.PruneSchedule /
// Backup.CheckSchedule entry. Independent of RunBackupAsync so a long
// prune doesn't queue behind an unrelated backup that happens to be in
// flight.
public sealed partial class BackupOrchestrator
{
    /// <summary>Run a stand-alone prune for every (source × target)
    /// cell of <paramref name="backup"/>. Persists one RunRecord
    /// covering the whole fan-out so Activity &amp; Logs shows a
    /// single "Prune ran Sunday 03:00" row, with the per-cell
    /// outcomes folded into the aggregate status.</summary>
    public Task<RunRecord> RunPruneAsync(Backup backup, CancellationToken ct)
        => RunStandaloneMaintenanceAsync(
            backup, RunKind.Prune, action: "Prune",
            engineCall: (engine, b, src, dest, storageName, runCt) =>
                engine.RunPruneOnlyAsync(b, src, dest, storageName, runCt),
            ct);

    /// <summary>Stand-alone integrity check, same shape as
    /// <see cref="RunPruneAsync"/>.</summary>
    public Task<RunRecord> RunCheckAsync(Backup backup, CancellationToken ct)
        => RunStandaloneMaintenanceAsync(
            backup, RunKind.Check, action: "Check",
            engineCall: (engine, b, src, dest, storageName, runCt) =>
                engine.RunCheckOnlyAsync(b, src, dest, storageName, runCt),
            ct);

    private async Task<RunRecord> RunStandaloneMaintenanceAsync(
        Backup backup, RunKind kind, string action,
        Func<IBackupEngine, Backup, string, Destination, string, CancellationToken, Task<RunRecord>> engineCall,
        CancellationToken ct)
    {
        using var scope = LogContext.PushProperty("Backup", backup.Name);
        var started = DateTime.UtcNow;
        var aggregate = new RunRecord
        {
            BackupId = backup.Id,
            BackupName = backup.Name,
            Kind = kind,
            StartedUtc = started,
            EndedUtc = started,
            Status = BackupRunStatus.Success,
            Summary = $"{action} starting…",
        };

        // Maintenance gate. Backup + prune/check should not interleave
        // on the same storage; let in-flight backups finish first.
        try { await ServiceLocator.Maintenance.WaitForReadAsync(ct); }
        catch (OperationCanceledException)
        {
            aggregate.EndedUtc = DateTime.UtcNow;
            aggregate.Status = BackupRunStatus.Skipped;
            aggregate.Summary = $"{action} cancelled before it could start.";
            try { _logs.AppendRun(aggregate); }
            catch (Exception ex) { _log.Warning(ex, "Failed to persist {Action} cancelled-pre-start run for {Backup}", action, backup.Name); }
            return aggregate;
        }

        // Metered-network watchdog. Mirrors the in-process guard the
        // backup path adds when AbortOnMeteredNetwork is set on the
        // Backup: prune / check transfer less than a backup but they
        // can still pull GB of pack files on a real prune of a big
        // repo, and "I tethered to my phone after the scheduled task
        // started" is a real user-reported scenario. The OS scheduler
        // already enforces RunOnlyIfNetworkAvailable AT LAUNCH; this
        // watchdog enforces the "no metered" intent MID-RUN. Inherits
        // AbortOnMeteredNetwork from the Backup so the user only has
        // one knob to flip — same setting governs both paths.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bool meteredAborted = false;
        var watchdogTask = StartMeteredWatchdogIfEnabled(backup, linked, _ => meteredAborted = true);

        try
        {
            int cellsOk = 0, cellsFailed = 0, cellsSkipped = 0;
            // Shared dedup helper so a future change can't make Maintenance
            // and CellLoop drift on the grouping shape (which would
            // re-introduce the truncated-log + ambiguous-event-stream bug
            // for one path but not the other).
            var distinctTargets = DistinctTargets(backup.Targets);
            foreach (var src in backup.SourcePaths)
            {
                if (linked.IsCancellationRequested) break;
                foreach (var target in distinctTargets)
                {
                    if (linked.IsCancellationRequested) break;
                    var dest = _config.Current.Destinations.FirstOrDefault(d => d.Id == target.DestinationId);
                    if (dest is null)
                    {
                        // Round-4 review caught: a Target whose
                        // Destination was DELETED from config is a
                        // permanent misconfig, not a transient skip
                        // — the user has to fix it. Treating it as
                        // cellsSkipped+ was masking the real problem
                        // by routing the aggregate to Skipped, so
                        // it's now cellsFailed+ instead.
                        aggregate.Errors.Add($"Destination id={target.DestinationId} no longer exists for source '{src}' — Backup needs reconfiguration.");
                        cellsFailed++;
                        continue;
                    }
                    try
                    {
                        var cell = await engineCall(_engine, backup, src, dest, target.StorageName, linked.Token);
                        if (cell.Status == BackupRunStatus.Failed)
                        {
                            cellsFailed++;
                            aggregate.Errors.AddRange(cell.Errors);
                        }
                        else
                        {
                            cellsOk++;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (meteredAborted)
                        {
                            aggregate.Errors.Add(
                                $"{action} cancelled at source '{src}', dest '{dest.Name}': " +
                                "network became metered (mobile / tethered) — per backup setting.");
                        }
                        else
                        {
                            aggregate.Errors.Add($"{action} cancelled mid-flow at source '{src}', dest '{dest.Name}'.");
                        }
                        cellsSkipped++;
                        break;
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "{Action} threw for backup {Backup}, source {Source}, dest {Dest}",
                            action, backup.Name, src, dest.Name);
                        aggregate.Errors.Add($"{action} failed for source '{src}', dest '{dest.Name}': {ex.Message}");
                        cellsFailed++;
                    }
                }
            }

            aggregate.EndedUtc = DateTime.UtcNow;
            // Failed > Success > Skipped, with one twist: a no-cell-run
            // outcome that was DUE TO metered abort surfaces as Skipped
            // (user intent: pause, not fail) while a no-cell-run
            // outcome due to "every destination missing in config" or
            // "every cell threw a Failed-status engine error" must
            // surface as Failed so the user notices. The dead-ternary
            // shape that previously had `meteredAborted ? Skipped :
            // Skipped` collapsed both into Skipped, masking real
            // misconfiguration as a metered-abort-style Skipped.
            // Carve out for "user just created this Backup, scheduler
            // fired before they finished configuring Targets /
            // SourcePaths" — Skipped is the honest verdict (nothing
            // to attempt isn't a failure of the maintenance op). The
            // bottom branch only stays Failed for the case where we
            // tried at least one source × target pair and every cell
            // bailed without reaching Skipped status either.
            var nothingToAttempt = backup.Targets.Count == 0 || backup.SourcePaths.Count == 0;
            aggregate.Status = cellsFailed > 0
                ? BackupRunStatus.Failed
                : cellsOk > 0 ? BackupRunStatus.Success
                  : meteredAborted ? BackupRunStatus.Skipped
                  : nothingToAttempt ? BackupRunStatus.Skipped
                  : cellsSkipped > 0 ? BackupRunStatus.Skipped
                  : BackupRunStatus.Failed;
            var durationSec = ((aggregate.EndedUtc ?? DateTime.UtcNow) - started).TotalSeconds;
            var meteredSuffix = meteredAborted ? " — aborted on metered network" : "";
            aggregate.Summary = $"{action}: {cellsOk} ok, {cellsFailed} failed, {cellsSkipped} skipped " +
                                $"across {backup.SourcePaths.Count}×{backup.Targets.Count} cells " +
                                $"in {durationSec:F1}s{meteredSuffix}.";

            try { _logs.AppendRun(aggregate); }
            catch (Exception ex) { _log.Warning(ex, "Failed to persist {Action} run for {Backup}", action, backup.Name); }
            return aggregate;
        }
        finally
        {
            try { linked.Cancel(); } catch { }
            if (watchdogTask is not null)
            {
                try { await watchdogTask; }
                catch (OperationCanceledException) { /* expected on linked cancel */ }
                catch (Exception ex) { _log.Debug(ex, "Metered watchdog teardown threw for {Action}", action); }
            }
        }
    }
}
