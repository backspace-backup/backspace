using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Serilog.Context;

namespace Backspace.Services;

// Stand-alone maintenance half of BackupOrchestrator: cleanup (forget +
// prune) and the backup's checks, one entry at a time (U179). Fired by the
// schedule, by the command line, and right after a backup for checks that
// run after each backup or waited for their destination (U174).
public sealed partial class BackupOrchestrator
{
    /// <summary>One check at a time per backup (U179): checks due together
    /// queue here, in this process. Across processes the background service
    /// runs one operation per backup at a time, and the destination's
    /// exclusive access (<see cref="RepositoryAccess"/>) keeps backups and
    /// other backups' checks waiting while a check runs.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _checkQueues = new(StringComparer.Ordinal);

    /// <summary>Run a stand-alone prune for every (source × target)
    /// cell of <paramref name="backup"/>. Persists one RunRecord
    /// covering the whole fan-out so Activity &amp; Logs shows a
    /// single "Prune ran Sunday 03:00" row, with the per-cell
    /// outcomes folded into the aggregate status.</summary>
    public Task<RunRecord> RunPruneAsync(Backup backup, CancellationToken ct)
        => RunStandaloneMaintenanceAsync(backup, RunKind.Prune, check: null, ct);

    /// <summary>Every check of the backup, one after another, in run order
    /// (the command line's --check). A backup without checks gets a structure
    /// check: the owner asked for one. Returns one record for them all, not
    /// saved: each check saved its own.</summary>
    public async Task<RunRecord> RunCheckAsync(Backup backup, CancellationToken ct, IReadOnlyCollection<BackupTarget>? onlyTargets = null)
    {
        var checks = backup.Checks.Count > 0
            ? BackupCheck.InRunOrder(backup.Checks).ToList()
            : [new BackupCheck { Kind = CheckKind.Structure }];
        var records = new List<RunRecord>();
        try
        {
            for (var i = 0; i < checks.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                // Says which check runs now: the card and Activity show it (U183).
                _progress.ReportActivity(backup.Id, checks.Count > 1 ? $"{checks[i].Title} ({i + 1} of {checks.Count})" : checks[i].Title);
                records.Add(await RunCheckAsync(backup, checks[i], ct, onlyTargets).ConfigureAwait(false));
            }
        }
        finally { _progress.ReportActivity(backup.Id, ""); }
        return CheckRunWording.Combine(backup, records);
    }

    /// <summary>One check entry (U179). Waits for any other check of this
    /// backup to end first, so checks due together run one after another.</summary>
    /// <param name="onlyTargets">Only these destinations (after a backup,
    /// U174); null for all.</param>
    public async Task<RunRecord> RunCheckAsync(Backup backup, BackupCheck check, CancellationToken ct, IReadOnlyCollection<BackupTarget>? onlyTargets = null)
    {
        var queue = _checkQueues.GetOrAdd(backup.Id, _ => new SemaphoreSlim(1, 1));
        if (queue.CurrentCount == 0)
            _log.Information("{Check} of {Backup} waits for the check before it", check.Title, backup.Name);
        try { await queue.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            var skipped = new RunRecord
            {
                BackupId = backup.Id, BackupName = backup.Name, Kind = RunKind.Check, CheckId = check.Id, CheckKind = check.Kind,
                StartedUtc = DateTime.UtcNow, EndedUtc = DateTime.UtcNow, Status = BackupRunStatus.Skipped,
                Summary = $"{check.Title} was cancelled before it could start.",
            };
            try { _logs.AppendRun(skipped); } catch (Exception ex) { _log.Warning(ex, "Failed to persist a cancelled check for {Backup}", backup.Name); }
            return skipped;
        }
        // Says which check runs, unless a list of checks already does: the
        // card shows "Checking · …" in the app and through the service.
        var named = string.IsNullOrEmpty(_progress.Snapshot(backup.Id)?.Activity);
        if (named) _progress.ReportActivity(backup.Id, check.Title);
        try
        {
            var record = await RunStandaloneMaintenanceAsync(backup, RunKind.Check, check, ct, onlyTargets).ConfigureAwait(false);
            await PingChecksAsync(backup, record).ConfigureAwait(false);
            return record;
        }
        finally
        {
            if (named) _progress.ReportActivity(backup.Id, "");
            queue.Release();
        }
    }

    /// <summary>The Healthchecks.io check for the backup's checks (U211): a
    /// check that passed pings it, one that failed says so; one that waited
    /// for its destination or was stopped says nothing, so the check goes
    /// down only when none has passed for its period and grace.</summary>
    private async Task PingChecksAsync(Backup backup, RunRecord record)
    {
        var address = (_config.Current.Backups.FirstOrDefault(b => b.Id == backup.Id) ?? backup).HealthcheckChecksPingUrl;
        if (string.IsNullOrWhiteSpace(address)) return;
        try
        {
            if (record.Status == BackupRunStatus.Success) await _hc.PingSuccessAsync(address, record.Summary ?? "", CancellationToken.None).ConfigureAwait(false);
            else if (record.Status == BackupRunStatus.Failed) await _hc.PingFailureAsync(address, record.Summary ?? "", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) { _log.Warning(ex, "Could not ping the Healthchecks.io check of {Backup}'s checks", backup.Name); }
    }

    /// <summary>Why a check can't reach this destination now, or null: a
    /// drive not connected or not the expected one, a network share offline
    /// (U174). The check then waits for the next backup there.</summary>
    private async Task<string?> UnavailableForCheckAsync(Destination dest, string sourcePath, string storageName, CancellationToken ct)
    {
        if (dest.IsLocalLike && VolumeDetector.Check(dest.ExpectedDriveLetter, dest.ExpectedVolumeLabel) is { Verdict: not VolumeDetector.Verdict.Mounted } volume)
            return volume.Detail;
        return await ProbeReachabilityAsync(dest, sourcePath, storageName, ct).ConfigureAwait(false) is { Status: BackupRunStatus.Skipped } offline
            ? offline.Summary : null;
    }

    /// <summary>Right after a backup, while its turn is over, in the tray app
    /// and the background service alike: the checks that run after each
    /// backup, then scheduled checks that waited for a destination this
    /// backup reached (U174), one after another (U179). Only destinations
    /// the backup reached, with every part there succeeded or warned.</summary>
    private async Task RunChecksAfterBackupAsync(Backup backup, RunRecord backupRun, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        try
        {
            if (_config.Current.Backups.FirstOrDefault(b => b.Id == backup.Id) is not { } current || current.Checks.Count == 0) return;
            var destinations = _config.Current.Destinations;
            var reached = CheckOverdue.Reached(current, backupRun, destinations);
            if (reached.Count == 0) return;
            IReadOnlyList<RunRecord>? history = null;
            var due = new List<(BackupCheck Check, List<BackupTarget> Targets)>();
            foreach (var check in BackupCheck.InRunOrder(current.Checks))
            {
                if (check.Schedule is null) { due.Add((check, reached.ToList())); continue; }
                history ??= _logs.LoadHistoryByBackupId(current.Id);
                var waiting = CheckOverdue.For(current, check, history, destinations)
                    .Select(w => w.Target).Where(t => reached.Any(r => SameTarget(r, t))).ToList();
                if (waiting.Count > 0) due.Add((check, waiting));
            }
            for (var i = 0; i < due.Count; i++)
            {
                var (check, targets) = due[i];
                if (ct.IsCancellationRequested) break;
                // The backup's own work is over: the card says which check runs (U183).
                _progress.ReportActivity(current.Id, due.Count > 1 ? $"{check.Title} ({i + 1} of {due.Count})" : check.Title);
                _log.Information("Running {Check} of {Backup} after its backup: {Destinations}", check.Title, current.Name,
                    string.Join(", ", targets.Select(t => destinations.FirstOrDefault(d => d.Id == t.DestinationId)?.Name ?? t.DestinationId)));
                await RunCheckAsync(current, check, ct, targets).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Warning(ex, "The checks after the backup of {Backup} could not run", backup.Name); }
        finally { _progress.ReportActivity(backup.Id, ""); }
    }

    private static bool SameTarget(BackupTarget a, BackupTarget b) => a.DestinationId == b.DestinationId
        && string.Equals(a.StorageName, b.StorageName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The copy of the backup the engine gets for one check (U179):
    /// a structure check, or a data read limited to the entry's size, or all
    /// of it.</summary>
    internal static Backup EngineViewFor(Backup backup, BackupCheck check)
    {
        var view = System.Text.Json.JsonSerializer.Deserialize<Backup>(System.Text.Json.JsonSerializer.Serialize(backup))!;
        view.CheckReadDataSample = check.Kind is CheckKind.DataSample or CheckKind.FullDataRead;
        view.DataCheckLimitBytes = check.Kind switch
        {
            CheckKind.DataSample => Math.Max(1, check.MaximumBytes),
            CheckKind.FullDataRead => long.MaxValue,
            _ => null,
        };
        return view;
    }

    private async Task<RunRecord> RunStandaloneMaintenanceAsync(
        Backup backup, RunKind kind, BackupCheck? check, CancellationToken ct, IReadOnlyCollection<BackupTarget>? onlyTargets = null)
    {
        using var scope = LogContext.PushProperty("Backup", backup.Name);
        var action = check is null ? "Cleanup" : check.Title;
        var wasConfigured = _config.Current.Backups.Any(b => b.Id == backup.Id);
        var started = DateTime.UtcNow;
        var aggregate = new RunRecord
        {
            BackupId = backup.Id,
            BackupName = backup.Name,
            Kind = kind,
            CheckId = check?.Id,
            CheckKind = check?.Kind,
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
            return Ended(BackupRunStatus.Skipped, $"{action} was cancelled before it could start.");
        }
        using var maintenanceReadLease = maintenanceRead;

        try
        {
            var admitted = await RefreshBackupAfterAdmissionAsync(backup, wasConfigured, ct);
            if (admitted is null)
                return Ended(BackupRunStatus.Skipped, $"{action} skipped: this backup was removed before it could start.");
            backup = admitted;
            // The entry as saved now; one removed meanwhile does not run. A
            // check the owner asked for without one saved (--check on a
            // backup without checks) runs as given.
            if (check is not null && wasConfigured && backup.Checks.Count > 0)
            {
                if (backup.Checks.FirstOrDefault(c => c.Id == check.Id) is { } saved) check = saved;
                else return Ended(BackupRunStatus.Skipped, $"{action} skipped: this check was removed before it could start.");
            }
        }
        catch (OperationCanceledException)
        {
            return Ended(BackupRunStatus.Skipped, $"{action} was cancelled before it could start.");
        }
        catch (Exception ex) { return await ReportAdmissionFailureAsync(backup, kind, ex); }

        // A full data read needs an engine that reads all of it (U179).
        if (check is { Kind: CheckKind.FullDataRead } && !_engine.HonoursDataCheckLimit)
            return Ended(BackupRunStatus.Skipped, CheckRunWording.FullReadUnavailable);

        // Metered-network watchdog. Mirrors the in-process guard the
        // backup path adds when AbortOnMeteredNetwork is set on the
        // Backup: prune / check transfer less than a backup but they
        // can still pull GB of pack files on a real prune of a big
        // repo, and "I tethered to my phone after the scheduled task
        // started" is a real user-reported scenario. Inherits
        // AbortOnMeteredNetwork from the Backup so the user only has
        // one knob to flip — same setting governs both paths.
        bool meteredAborted = false;
        var watchdogTask = StartMeteredWatchdogIfEnabled(backup, linked, _ => meteredAborted = true);

        try
        {
            var distinctTargets = DistinctTargets(backup.Targets);
            if (onlyTargets is not null)
                distinctTargets = distinctTargets.Where(t => onlyTargets.Any(o => SameTarget(o, t))).ToList();
            var waitingFor = new List<string>();
            if (check is { Kind: CheckKind.TestRestore })
                await RunTestRestoreCheckAsync(backup, check, distinctTargets, aggregate, waitingFor, linked, () => meteredAborted).ConfigureAwait(false);
            else
            {
                var engineBackup = check is null ? backup : EngineViewFor(backup, check);
                // A check covers the entire repository, not one source's
                // snapshots: it runs once per destination. Cleanup stays per
                // source because retention is plan-specific.
                var sources = kind == RunKind.Check ? backup.SourcePaths.Take(1) : backup.SourcePaths;
                var interrupted = false;
                foreach (var src in sources)
                {
                    foreach (var target in distinctTargets)
                    {
                        var dest = _config.Current.Destinations.FirstOrDefault(d => d.Id == target.DestinationId);
                        if (interrupted || linked.IsCancellationRequested)
                        {
                            AddOutcome(aggregate, src, dest?.Name ?? "(missing)", target.StorageName, BackupRunStatus.Skipped,
                                meteredAborted ? "Not started because the network became metered." : "Not started because the operation was cancelled.");
                            continue;
                        }
                        if (dest is null)
                        {
                            AddOutcome(aggregate, src, "(missing)", target.StorageName, BackupRunStatus.Failed,
                                $"Destination id={target.DestinationId} no longer exists. Backup needs reconfiguration.");
                            continue;
                        }
                        // A check of a drive that is not connected (or a share
                        // offline) waits for the next backup there (U174),
                        // rather than failing and waiting a whole schedule.
                        if (kind == RunKind.Check && await UnavailableForCheckAsync(dest, src, target.StorageName, linked.Token) is { } unavailable)
                        {
                            _log.Information("{Check} of {Dest} waits for the destination: {Detail}", action, dest.Name, unavailable);
                            AddOutcome(aggregate, src, dest.Name, target.StorageName, BackupRunStatus.Skipped, unavailable);
                            aggregate.Cells[^1].DestinationUnavailable = true;
                            waitingFor.Add(dest.Name);
                            continue;
                        }
                        try
                        {
                            // Cleanup and checks need the destination alone,
                            // whatever "Backups at once per destination" says:
                            // they wait for the backups running there (U156).
                            using var access = await AcquireRepositoryAsync(backup, aggregate, dest, target.StorageName,
                                RepositoryAccess.Kind.Exclusive, linked.Token);
                            var cell = kind == RunKind.Prune
                                ? await _engine.RunPruneOnlyAsync(engineBackup, src, dest, target.StorageName, linked.Token)
                                : await _engine.RunCheckOnlyAsync(engineBackup, src, dest, target.StorageName, linked.Token,
                                    progress: CheckStepReporter(backup.Id, dest.Name, distinctTargets.IndexOf(target), distinctTargets.Count));
                            var status = cell.Status;
                            var summary = cell.Summary;
                            // An absent terminal result cannot establish that the
                            // repository was checked or its retention policy applied.
                            if (status is BackupRunStatus.NeverRun or BackupRunStatus.Running)
                            { status = BackupRunStatus.Failed; summary = "The engine did not report a completed result. " + summary; }
                            if (status == BackupRunStatus.Success && cell.Errors.Count > 0) status = BackupRunStatus.Failed;
                            if (status == BackupRunStatus.Success && cell.Warnings.Count > 0) status = BackupRunStatus.Warning;
                            if (check is { Kind: CheckKind.FullDataRead })
                                cell.Checks = cell.Checks.Select(c => c with { Depth = CheckDepth.AllData }).ToList();
                            AddOutcome(aggregate, src, dest.Name, target.StorageName, status, summary, cell);
                        }
                        catch (OperationCanceledException)
                        {
                            interrupted = true;
                            AddOutcome(aggregate, src, dest.Name, target.StorageName, BackupRunStatus.Skipped,
                                meteredAborted ? "Cancelled because the network became metered (mobile / tethered)." : "Cancelled before completion.");
                        }
                        catch (Exception ex)
                        {
                            _log.Warning(ex, "{Action} threw for backup {Backup}, source {Source}, dest {Dest}",
                                action, backup.Name, src, dest.Name);
                            AddOutcome(aggregate, src, dest.Name, target.StorageName, BackupRunStatus.Failed, ex.Message);
                        }
                    }
                }
            }

            // The connection went away (or became metered) during a check or
            // cleanup: it didn't fail, it was stopped by its conditions. It is
            // skipped quietly and the schedule runs it again once they are met,
            // as for a backup (U185, owner, 11 November 2026).
            var networkGone = !NetworkAvailableNow();
            foreach (var cell in aggregate.Cells.Where(c => c.Status == BackupRunStatus.Failed))
            {
                var dest = _config.Current.Destinations.FirstOrDefault(d => string.Equals(d.Name, cell.DestinationName, StringComparison.OrdinalIgnoreCase));
                if (dest is null || !UsesNetwork(dest)) continue;
                if (networkGone)
                {
                    cell.Status = BackupRunStatus.Skipped;
                    cell.Summary = "The connection was lost; it runs again once the connection is back. (" + cell.Summary + ")";
                }
                else if (meteredAborted)
                {
                    cell.Status = BackupRunStatus.Skipped;
                    cell.Summary = "Stopped because the connection became metered; it runs again once it isn't. (" + cell.Summary + ")";
                }
            }
            aggregate.EndedUtc = DateTime.UtcNow;
            aggregate.Status = CheckRunWording.StatusOf(aggregate.Cells);
            aggregate.Summary = check is null
                ? CheckRunWording.CleanupSummary(aggregate, meteredAborted)
                : CheckRunWording.Summary(check, aggregate, waitingFor, meteredAborted,
                    limitIgnored: check.Kind == CheckKind.DataSample && check.MaximumBytes != BackupCheck.OneGiB && !_engine.HonoursDataCheckLimit);

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

        RunRecord Ended(BackupRunStatus status, string summary)
        {
            aggregate.EndedUtc = DateTime.UtcNow;
            aggregate.Status = status;
            aggregate.Summary = summary;
            try { _logs.AppendRun(aggregate); }
            catch (Exception ex) { _log.Warning(ex, "Failed to persist {Action} run for {Backup}", action, backup.Name); }
            return aggregate;
        }
    }

    /// <summary>Set by tests; the real check asks Windows.</summary>
    internal Func<bool>? NetworkAvailableOverride { get; set; }
    private bool NetworkAvailableNow() => NetworkAvailableOverride?.Invoke() ?? ScheduleConditions.NetworkAvailable();

    private static bool UsesNetwork(Destination d) =>
        d.Kind is not (DestinationKind.LocalFolder or DestinationKind.ExternalDrive)
        || (d.PathOrSubpath ?? "").StartsWith(@"\\", StringComparison.Ordinal);

    private static void AddOutcome(RunRecord aggregate, string source, string destination, string storage, BackupRunStatus status, string summary, RunRecord? result = null)
    {
        aggregate.Cells.Add(new CellOutcome
        {
            SourcePath = source, DestinationName = destination, StorageName = storage,
            Status = status, Summary = summary,
            LastError = status == BackupRunStatus.Failed ? result?.Errors.FirstOrDefault() ?? summary : null,
            Duration = result is null ? TimeSpan.Zero : (result.EndedUtc ?? DateTime.UtcNow) - result.StartedUtc,
            LogPath = result?.LogPath ?? "", AttemptsMade = result is null ? 0 : 1,
            SnapshotsRemoved = result?.SnapshotsRemoved ?? 0, BytesFreed = result?.BytesFreed ?? 0,
        });
        if (result is not null)
        {
            // What each cleanup freed, summed for the run (U142).
            aggregate.SnapshotsRemoved += result.SnapshotsRemoved;
            aggregate.BytesFreed += result.BytesFreed;
            aggregate.Checks.AddRange(result.Checks);
            aggregate.Errors.AddRange(result.Errors);
            aggregate.Warnings.AddRange(result.Warnings);
        }
        if (status == BackupRunStatus.Failed && (result is null || result.Errors.Count == 0))
            aggregate.Errors.Add($"[{source} → {destination}] {summary}");
        if (status is BackupRunStatus.Warning or BackupRunStatus.Skipped && (result is null || result.Warnings.Count == 0))
            aggregate.Warnings.Add($"[{source} → {destination}] {summary}");
    }

    /// <summary>Reports a restic check's steps for the card (U198): its share
    /// done across the destinations this check runs on, one after another.</summary>
    private Action<CheckStep, double> CheckStepReporter(string backupId, string destination, int index, int count)
    {
        var where = count > 1 ? $"{destination} ({index + 1} of {count})" : destination;
        return (step, percent) => _progress.ReportActivityStep(backupId, step.Text, where,
            (Math.Max(0, index) * 100 + percent) / Math.Max(1, count));
    }

    /// <summary>A sample test restore's progress, read once a second from its
    /// session, until <paramref name="ct"/> ends it (U198).</summary>
    private async Task WatchTestRestoreAsync(string backupId, string where, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (ServiceLocator.ActiveRestores.RunningTestFor(backupId) is { } session)
                {
                    var known = !session.IsProgressIndeterminate && session.TotalBytes > 0;
                    _progress.ReportActivityStep(backupId,
                        known ? $"Restoring and comparing files · {session.RestoredCount:N0} of {session.TotalFiles:N0} files" : "Preparing the files to restore",
                        where, known ? session.PercentComplete * 100 : -1);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Debug(ex, "Could not follow the test restore's progress"); }
    }

    /// <summary>A sample test restore entry (U179): rebuilds files from the
    /// latest backup at each destination it can reach, holding each one alone
    /// meanwhile, so backups and other checks there wait. A destination that
    /// is unavailable waits for the next backup there (U174).</summary>
    private async Task RunTestRestoreCheckAsync(Backup backup, BackupCheck check, IReadOnlyList<BackupTarget> targets,
        RunRecord aggregate, List<string> waitingFor, CancellationTokenSource linked, Func<bool> metered)
    {
        var source = backup.SourcePaths.FirstOrDefault() ?? "";
        var reachable = new List<(BackupTarget Target, Destination Destination)>();
        foreach (var target in targets)
        {
            var dest = _config.Current.Destinations.FirstOrDefault(d => d.Id == target.DestinationId);
            if (dest is null)
            {
                AddOutcome(aggregate, source, "(missing)", target.StorageName, BackupRunStatus.Failed,
                    $"Destination id={target.DestinationId} no longer exists. Backup needs reconfiguration.");
                continue;
            }
            if (await UnavailableForCheckAsync(dest, source, target.StorageName, linked.Token).ConfigureAwait(false) is { } unavailable)
            {
                AddOutcome(aggregate, source, dest.Name, target.StorageName, BackupRunStatus.Skipped, unavailable);
                aggregate.Cells[^1].DestinationUnavailable = true;
                waitingFor.Add(dest.Name);
                continue;
            }
            reachable.Add((target, dest));
        }
        if (reachable.Count == 0) return;
        var leases = new List<IDisposable>();
        try
        {
            // Always in the same order, so two checks never hold one each.
            foreach (var (target, dest) in reachable.OrderBy(r => r.Destination.Id, StringComparer.Ordinal).ThenBy(r => r.Target.StorageName, StringComparer.OrdinalIgnoreCase))
                leases.Add(await AcquireRepositoryAsync(backup, aggregate, dest, target.StorageName, RepositoryAccess.Kind.Exclusive, linked.Token).ConfigureAwait(false));
            var view = System.Text.Json.JsonSerializer.Deserialize<Backup>(System.Text.Json.JsonSerializer.Serialize(backup))!;
            view.Targets = reachable.Select(r => r.Target).ToList();
            // Its progress, once a second, for the card (U198).
            var where = reachable.Count == 1 ? reachable[0].Destination.Name : $"{reachable.Count} destinations";
            using var watching = new CancellationTokenSource();
            var watch = WatchTestRestoreAsync(backup.Id, where, watching.Token);
            VerifyOutcome outcome;
            try { outcome = await ServiceLocator.TestRestores.RunScheduledAsync(view, check.RestoreOptions(), linked.Token, maintenanceLeaseHeld: true).ConfigureAwait(false); }
            finally { watching.Cancel(); await watch.ConfigureAwait(false); }
            var status = outcome.OverallPass ? BackupRunStatus.Success : BackupRunStatus.Failed;
            foreach (var (target, dest) in reachable)
                AddOutcome(aggregate, source, dest.Name, target.StorageName, status, outcome.Summary);
            // The test restore's own record holds its result for the check
            // history (BackupCheckHistory); this record holds the plain
            // summary and which destinations it reached (U174).
            if (!outcome.OverallPass) aggregate.Errors.Add("The sample test restore needs attention. " + outcome.Summary);
        }
        catch (OperationCanceledException)
        {
            foreach (var (target, dest) in reachable.Where(r => !aggregate.Cells.Any(c => c.DestinationName == r.Destination.Name)))
                AddOutcome(aggregate, source, dest.Name, target.StorageName, BackupRunStatus.Skipped,
                    metered() ? "Cancelled because the network became metered (mobile / tethered)." : "Cancelled before completion.");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "The sample test restore of {Backup} could not finish", backup.Name);
            foreach (var (target, dest) in reachable.Where(r => !aggregate.Cells.Any(c => c.DestinationName == r.Destination.Name)))
                AddOutcome(aggregate, source, dest.Name, target.StorageName, BackupRunStatus.Failed, ex.Message);
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }
}
