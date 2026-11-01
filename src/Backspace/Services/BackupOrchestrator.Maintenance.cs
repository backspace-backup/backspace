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
        var wasConfigured = _config.Current.Backups.Any(b => b.Id == backup.Id);
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
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IDisposable maintenanceRead;
        try { maintenanceRead = await ServiceLocator.Maintenance.AcquireReadAsync(ct, linked.Cancel); }
        catch (OperationCanceledException)
        {
            aggregate.EndedUtc = DateTime.UtcNow;
            aggregate.Status = BackupRunStatus.Skipped;
            aggregate.Summary = $"{action} cancelled before it could start.";
            try { _logs.AppendRun(aggregate); }
            catch (Exception ex) { _log.Warning(ex, "Failed to persist {Action} cancelled-pre-start run for {Backup}", action, backup.Name); }
            return aggregate;
        }
        using var maintenanceReadLease = maintenanceRead;

        try
        {
            var admitted = await RefreshBackupAfterAdmissionAsync(backup, wasConfigured, ct);
            if (admitted is null)
            {
                aggregate.EndedUtc = DateTime.UtcNow;
                aggregate.Status = BackupRunStatus.Skipped;
                aggregate.Summary = $"{action} skipped: this backup was removed before it could start.";
                _logs.AppendRun(aggregate);
                return aggregate;
            }
            backup = admitted;
        }
        catch (OperationCanceledException)
        {
            aggregate.EndedUtc = DateTime.UtcNow;
            aggregate.Status = BackupRunStatus.Skipped;
            aggregate.Summary = $"{action} cancelled before it could start.";
            _logs.AppendRun(aggregate);
            return aggregate;
        }
        catch (Exception ex) { return await ReportAdmissionFailureAsync(backup, kind, ex); }

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
        bool meteredAborted = false;
        var watchdogTask = StartMeteredWatchdogIfEnabled(backup, linked, _ => meteredAborted = true);

        try
        {
            bool interrupted = false;
            // Shared dedup helper so a future change can't make Maintenance
            // and CellLoop drift on the grouping shape (which would
            // re-introduce the truncated-log + ambiguous-event-stream bug
            // for one path but not the other).
            var distinctTargets = DistinctTargets(backup.Targets);
            // Check covers the entire repository, not one source's snapshots.
            // Do not download the same data sample again for every source.
            // Prune remains per source because retention is plan-specific.
            var sources = kind == RunKind.Check ? backup.SourcePaths.Take(1) : backup.SourcePaths;
            foreach (var src in sources)
            {
                foreach (var target in distinctTargets)
                {
                    var dest = _config.Current.Destinations.FirstOrDefault(d => d.Id == target.DestinationId);
                    if (interrupted || linked.IsCancellationRequested)
                    {
                        AddOutcome(src, dest?.Name ?? "(missing)", target.StorageName, BackupRunStatus.Skipped,
                            meteredAborted ? "Not started because the network became metered." : "Not started because the operation was cancelled.");
                        continue;
                    }
                    if (dest is null)
                    {
                        AddOutcome(src, "(missing)", target.StorageName, BackupRunStatus.Failed,
                            $"Destination id={target.DestinationId} no longer exists. Backup needs reconfiguration.");
                        continue;
                    }
                    try
                    {
                        var cell = await engineCall(_engine, backup, src, dest, target.StorageName, linked.Token);
                        var status = cell.Status;
                        var summary = cell.Summary;
                        // An absent terminal result cannot establish that the
                        // repository was checked or its retention policy applied.
                        if (status is BackupRunStatus.NeverRun or BackupRunStatus.Running)
                        { status = BackupRunStatus.Failed; summary = "The engine did not report a completed result. " + summary; }
                        if (status == BackupRunStatus.Success && cell.Errors.Count > 0) status = BackupRunStatus.Failed;
                        if (status == BackupRunStatus.Success && cell.Warnings.Count > 0) status = BackupRunStatus.Warning;
                        AddOutcome(src, dest.Name, target.StorageName, status, summary, cell);
                    }
                    catch (OperationCanceledException)
                    {
                        interrupted = true;
                        AddOutcome(src, dest.Name, target.StorageName, BackupRunStatus.Skipped,
                            meteredAborted ? "Cancelled because the network became metered (mobile / tethered)." : "Cancelled before completion.");
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "{Action} threw for backup {Backup}, source {Source}, dest {Dest}",
                            action, backup.Name, src, dest.Name);
                        AddOutcome(src, dest.Name, target.StorageName, BackupRunStatus.Failed, ex.Message);
                    }
                }
            }

            aggregate.EndedUtc = DateTime.UtcNow;
            var cellsOk = aggregate.Cells.Count(c => c.Status == BackupRunStatus.Success);
            var cellsWarned = aggregate.Cells.Count(c => c.Status == BackupRunStatus.Warning);
            var cellsFailed = aggregate.Cells.Count(c => c.Status == BackupRunStatus.Failed);
            var cellsSkipped = aggregate.Cells.Count(c => c.Status == BackupRunStatus.Skipped);
            // Partial completion must not look like an entirely successful
            // check. Count unstarted parts too, including cancellation between
            // two completed engine calls.
            aggregate.Status = cellsFailed > 0
                ? BackupRunStatus.Failed
                : cellsWarned > 0 || cellsOk > 0 && cellsSkipped > 0 ? BackupRunStatus.Warning
                : cellsOk > 0 ? BackupRunStatus.Success
                : BackupRunStatus.Skipped;
            var comparisonSummary = kind == RunKind.Check && backup.CheckRestoreFiles
                ? await AddAutomaticRestoreCheckAsync(backup, aggregate, linked.Token, maintenanceLeaseHeld: true)
                : "";
            var durationSec = ((aggregate.EndedUtc ?? DateTime.UtcNow) - started).TotalSeconds;
            var meteredSuffix = meteredAborted ? " (aborted on metered network)" : "";
            aggregate.Summary = $"{action}: {cellsOk} completed, {cellsWarned} with warnings, {cellsFailed} failed, {cellsSkipped} skipped " +
                                $"across {aggregate.Cells.Count} parts " +
                                $"in {durationSec:F1}s{meteredSuffix}." + comparisonSummary;

            try { _logs.AppendRun(aggregate); }
            catch (Exception ex) { _log.Warning(ex, "Failed to persist {Action} run for {Backup}", action, backup.Name); }
            return aggregate;

            void AddOutcome(string source, string destination, string storage, BackupRunStatus status, string summary, RunRecord? result = null)
            {
                aggregate.Cells.Add(new CellOutcome
                {
                    SourcePath = source, DestinationName = destination, StorageName = storage,
                    Status = status, Summary = summary,
                    LastError = status == BackupRunStatus.Failed ? result?.Errors.FirstOrDefault() ?? summary : null,
                    Duration = result is null ? TimeSpan.Zero : (result.EndedUtc ?? DateTime.UtcNow) - result.StartedUtc,
                    LogPath = result?.LogPath ?? "", AttemptsMade = result is null ? 0 : 1,
                });
                if (result is not null)
                {
                    aggregate.Checks.AddRange(result.Checks);
                    aggregate.Errors.AddRange(result.Errors);
                    aggregate.Warnings.AddRange(result.Warnings);
                }
                if (status == BackupRunStatus.Failed && (result is null || result.Errors.Count == 0))
                    aggregate.Errors.Add($"[{source} → {destination}] {summary}");
                if (status is BackupRunStatus.Warning or BackupRunStatus.Skipped && (result is null || result.Warnings.Count == 0))
                    aggregate.Warnings.Add($"[{source} → {destination}] {summary}");
            }
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

    private async Task<string> AddAutomaticRestoreCheckAsync(Backup backup, RunRecord record, CancellationToken ct, bool maintenanceLeaseHeld)
    {
        if (record.Status != BackupRunStatus.Success || ct.IsCancellationRequested)
        {
            return " The automatic test restore was not started because the preceding work did not finish successfully.";
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watchdog = StartMeteredWatchdogIfEnabled(backup, linked, _ => { });
        string summary;
        try
        {
            var outcome = await ServiceLocator.TestRestores.RunScheduledAsync(backup, linked.Token, maintenanceLeaseHeld);
            summary = "\nAutomatic test restore: " + outcome.Summary;
            if (!outcome.OverallPass)
            {
                record.Status = record.Kind == RunKind.Backup ? BackupRunStatus.Warning : BackupRunStatus.Failed;
                record.Errors.Add("Automatic test restore needs attention. " + outcome.Summary);
            }
        }
        catch (OperationCanceledException)
        {
            record.Status = BackupRunStatus.Warning;
            summary = "\nThe automatic test restore was cancelled before completion.";
            record.Warnings.Add("Automatic test restore did not finish.");
        }
        catch (Exception ex)
        {
            record.Status = record.Kind == RunKind.Backup ? BackupRunStatus.Warning : BackupRunStatus.Failed;
            summary = "\nThe automatic test restore could not finish: " + ex.Message;
            record.Errors.Add(ex.Message);
        }
        finally
        {
            linked.Cancel();
            if (watchdog is not null) { try { await watchdog; } catch (OperationCanceledException) { } }
            record.EndedUtc = DateTime.UtcNow;
        }
        return summary;
    }
}
