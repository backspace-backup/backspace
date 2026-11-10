using System;
using System.Collections.Generic;
using System.Linq;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>
/// A scheduled check (and its test restore) that could not run because its
/// destination was unavailable: a drive not connected or not the expected
/// one, or a network share offline (U174, 9 November 2026). It stays overdue
/// and runs once, right after the next successful backup of that backup to
/// that destination, in the tray app's schedule and in the background
/// service alike. Derived from the run history, so nothing else is saved: the
/// latest check of a destination decides. A check that ran there, whatever
/// its result, ends the wait; one that failed for another reason never
/// starts a wait.
/// </summary>
public static class CheckOverdue
{
    /// <summary>A destination whose last scheduled check waited for it.</summary>
    public sealed record Waiting(BackupTarget Target, Destination Destination, DateTime MissedUtc, string Reason);

    /// <summary>The destinations of <paramref name="backup"/> whose latest
    /// check could not reach them. Only for a backup with a check schedule.</summary>
    public static IReadOnlyList<Waiting> For(Backup backup, IEnumerable<RunRecord> history, IEnumerable<Destination> destinations)
    {
        if (backup.CheckSchedule is null) return Array.Empty<Waiting>();
        var latest = new Dictionary<string, (CellOutcome Cell, DateTime When)>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in history.Where(r => r.BackupId == backup.Id && r.Kind == RunKind.Check && r.EndedUtc is not null)
                     .OrderBy(r => r.StartedUtc))
            foreach (var cell in run.Cells)
                latest[Key(cell.DestinationName, cell.StorageName)] = (cell, run.EndedUtc!.Value);
        var waiting = new List<Waiting>();
        var all = destinations.ToList();
        foreach (var target in BackupOrchestrator.DistinctTargets(backup.Targets))
        {
            if (all.FirstOrDefault(d => d.Id == target.DestinationId) is not { } destination) continue;
            if (latest.TryGetValue(Key(destination.Name, target.StorageName), out var seen) && seen.Cell.DestinationUnavailable)
                waiting.Add(new(target, destination, seen.When, seen.Cell.Summary));
        }
        return waiting;
    }

    /// <summary>The waiting destinations this finished backup run reached:
    /// every one of its parts there succeeded (or warned). A failed, stopped
    /// or skipped backup leaves the check waiting.</summary>
    public static IReadOnlyList<Waiting> DueAfter(Backup backup, RunRecord backupRun, IEnumerable<RunRecord> history, IEnumerable<Destination> destinations)
    {
        if (backupRun.Kind != RunKind.Backup || backupRun.Status is not (BackupRunStatus.Success or BackupRunStatus.Warning))
            return Array.Empty<Waiting>();
        return For(backup, history, destinations).Where(w =>
        {
            var cells = backupRun.Cells.Where(c => string.Equals(Key(c.DestinationName, c.StorageName), Key(w.Destination.Name, w.Target.StorageName), StringComparison.OrdinalIgnoreCase)).ToList();
            return cells.Count > 0 && cells.All(c => c.Status is BackupRunStatus.Success or BackupRunStatus.Warning);
        }).ToList();
    }

    /// <summary>What the card shows: the waiting check, unless an unresolved
    /// failure needs attention first.</summary>
    public static BackupCheckState Shown(BackupCheckState state, Backup backup, IReadOnlyList<Waiting> waiting)
        => waiting.Count == 0 || state.Tone == "fail" ? state : State(backup, waiting);

    /// <summary>The card's check line while a check waits: calm, not an alarm.</summary>
    public static BackupCheckState State(Backup backup, IReadOnlyList<Waiting> waiting)
    {
        var what = backup.CheckRestoreFiles ? "Test restore" : "Check";
        var names = string.Join(", ", waiting.Select(w => "«" + w.Destination.Name + "»"));
        var drive = waiting.All(w => w.Destination.IsLocalLike && w.Destination.Kind != DestinationKind.NetworkShare) ? "the drive" : "the destination";
        var since = waiting.Min(w => w.MissedUtc).ToLocalTime().ToString("d MMM", System.Globalization.CultureInfo.CurrentCulture);
        return new($"{what} overdue: waits for {drive}", "stale", "",
            $"The scheduled {what.ToLowerInvariant()} of {since} could not reach {names}: {waiting[0].Reason}\n"
            + "It runs by itself right after the next backup to it. Nothing else is needed.");
    }

    private static string Key(string destinationName, string? storageName) => destinationName + "/" + (string.IsNullOrEmpty(storageName) ? "default" : storageName);
}
