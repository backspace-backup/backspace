using System;
using System.Collections.Generic;
using System.Linq;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>What the tray app's and the service's schedules share about a
/// backup's network destinations left waiting (U226).</summary>
internal static class ScheduledRuns
{
    /// <summary>The worker's variable naming the destinations to run, as
    /// <see cref="Backup.TargetKey"/>s separated by "|".</summary>
    internal const string OnlyTargetsVariable = "BACKSPACE_ONLY_TARGETS";

    /// <summary>A copy of the backup with only these destinations; the
    /// backup itself when <paramref name="only"/> is null. Nothing writes this
    /// copy back to the settings.</summary>
    internal static Backup ForTargets(Backup backup, IReadOnlyCollection<string>? only)
    {
        if (only is null) return backup;
        var copy = System.Text.Json.JsonSerializer.Deserialize<Backup>(System.Text.Json.JsonSerializer.Serialize(backup))!;
        copy.Targets = copy.Targets.Where(t => only.Contains(Backup.TargetKey(t.DestinationId, t.StorageName))).ToList();
        return copy;
    }

    /// <summary>Tells the schedule which network destinations this backup run
    /// left waiting. Those it couldn't reach although the network seemed fine
    /// ("reachable") are tried again only after a network change, or a day later.</summary>
    internal static void ReportWaiting(RunRecord? record)
    {
        if (record is null || record.Kind != RunKind.Backup) return;
        var waiting = record.Cells.Where(c => c.WaitingFor is not null && !string.IsNullOrEmpty(c.DestinationId)).ToList();
        ScheduleLoop.ReportWaiting(waiting.Select(c => Backup.TargetKey(c.DestinationId!, c.StorageName)),
            needsChange: waiting.Count > 0 && waiting.All(c => c.WaitingFor == "reachable"));
    }
}
