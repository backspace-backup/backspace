using System;
using System.Collections.Generic;
using System.Linq;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>
/// A scheduled check that could not run because its destination was
/// unavailable: a drive not connected or not the expected one, or a network
/// share offline (U174, 9 November 2026). It stays overdue and runs once,
/// right after the next successful backup of that backup to that destination,
/// in the tray app's schedule and in the background service alike. Derived
/// from the run history, so nothing else is saved: the latest run of that
/// check at a destination decides. A check that ran there, whatever its
/// result, ends the wait; one that failed for another reason never starts a
/// wait. Each check entry waits on its own (U179); a check recorded before
/// checks were a list counts for every entry.
/// </summary>
public static class CheckOverdue
{
    /// <summary>A destination whose last run of <see cref="Check"/> waited for it.</summary>
    public sealed record Waiting(BackupCheck Check, BackupTarget Target, Destination Destination, DateTime MissedUtc, string Reason);

    /// <summary>Every scheduled check of <paramref name="backup"/> waiting for a destination.</summary>
    public static IReadOnlyList<Waiting> For(Backup backup, IEnumerable<RunRecord> history, IEnumerable<Destination> destinations)
    {
        var runs = history as IReadOnlyCollection<RunRecord> ?? history.ToList();
        var all = destinations as IReadOnlyCollection<Destination> ?? destinations.ToList();
        return backup.Checks.Where(c => c.Schedule is not null).SelectMany(c => For(backup, c, runs, all)).ToList();
    }

    /// <summary>The destinations whose latest run of <paramref name="check"/>
    /// could not reach them. Only for a check with a schedule: one that runs
    /// after each backup runs whenever the backup reaches the destination.</summary>
    public static IReadOnlyList<Waiting> For(Backup backup, BackupCheck check, IEnumerable<RunRecord> history, IEnumerable<Destination> destinations)
    {
        if (check.Schedule is null) return Array.Empty<Waiting>();
        var latest = new Dictionary<string, (CellOutcome Cell, DateTime When)>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in history.Where(r => r.BackupId == backup.Id && r.Kind == RunKind.Check && r.EndedUtc is not null
                         && (r.CheckId is null || r.CheckId == check.Id))
                     .OrderBy(r => r.StartedUtc))
            foreach (var cell in run.Cells)
                latest[Key(cell.DestinationName, cell.StorageName)] = (cell, run.EndedUtc!.Value);
        var waiting = new List<Waiting>();
        var all = destinations.ToList();
        foreach (var target in BackupOrchestrator.DistinctTargets(backup.Targets))
        {
            if (all.FirstOrDefault(d => d.Id == target.DestinationId) is not { } destination) continue;
            if (latest.TryGetValue(Key(destination.Name, target.StorageName), out var seen) && seen.Cell.DestinationUnavailable)
                waiting.Add(new(check, target, destination, seen.When, seen.Cell.Summary));
        }
        return waiting;
    }

    /// <summary>The destinations this finished backup run reached: every one
    /// of its parts there succeeded (or warned). A failed, stopped or skipped
    /// backup reaches none.</summary>
    public static IReadOnlyList<BackupTarget> Reached(Backup backup, RunRecord backupRun, IEnumerable<Destination> destinations)
    {
        if (backupRun.Kind != RunKind.Backup || backupRun.Status is not (BackupRunStatus.Success or BackupRunStatus.Warning))
            return Array.Empty<BackupTarget>();
        var all = destinations.ToList();
        return BackupOrchestrator.DistinctTargets(backup.Targets).Where(target =>
        {
            if (all.FirstOrDefault(d => d.Id == target.DestinationId) is not { } destination) return false;
            var cells = backupRun.Cells.Where(c => string.Equals(Key(c.DestinationName, c.StorageName), Key(destination.Name, target.StorageName), StringComparison.OrdinalIgnoreCase)).ToList();
            return cells.Count > 0 && cells.All(c => c.Status is BackupRunStatus.Success or BackupRunStatus.Warning);
        }).ToList();
    }

    /// <summary>The waiting checks this finished backup run reached.</summary>
    public static IReadOnlyList<Waiting> DueAfter(Backup backup, RunRecord backupRun, IEnumerable<RunRecord> history, IEnumerable<Destination> destinations)
    {
        var all = destinations.ToList();
        var reached = Reached(backup, backupRun, all);
        if (reached.Count == 0) return Array.Empty<Waiting>();
        return For(backup, history, all).Where(w => reached.Any(r => r.DestinationId == w.Target.DestinationId
            && string.Equals(r.StorageName, w.Target.StorageName, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>What the card shows: the waiting check, unless an unresolved
    /// failure needs attention first.</summary>
    public static BackupCheckState Shown(BackupCheckState state, Backup backup, IReadOnlyList<Waiting> waiting)
        => waiting.Count == 0 || state.Tone == "fail" ? state : State(backup, waiting) with { Kinds = state.Kinds };

    /// <summary>The card's check line while a check waits: calm, not an alarm.</summary>
    public static BackupCheckState State(Backup backup, IReadOnlyList<Waiting> waiting)
    {
        var kinds = waiting.Select(w => w.Check.Kind).Distinct().OrderBy(k => k).Select(BackupCheck.Name).ToList();
        var what = kinds.Count == 1 ? kinds[0] : "Checks";
        var names = string.Join(", ", waiting.Select(w => "«" + w.Destination.Name + "»").Distinct());
        var drive = waiting.All(w => w.Destination.IsLocalLike && w.Destination.Kind != DestinationKind.NetworkShare) ? "the drive" : "the destination";
        var since = waiting.Min(w => w.MissedUtc).ToLocalTime().ToString("d MMM", System.Globalization.CultureInfo.CurrentCulture);
        var list = kinds.Count == 1 ? what.ToLowerInvariant() : string.Join(", ", kinds.Select(k => k.ToLowerInvariant()));
        return new($"{what} overdue: waits for {drive}", "stale", "",
            $"The scheduled {list} of {since} could not reach {names}: {waiting[0].Reason}\n"
            + (kinds.Count == 1 ? "It runs by itself" : "They run by themselves") + " right after the next backup there. Nothing else is needed.");
    }

    private static string Key(string destinationName, string? storageName) => destinationName + "/" + (string.IsNullOrEmpty(storageName) ? "default" : storageName);
}
