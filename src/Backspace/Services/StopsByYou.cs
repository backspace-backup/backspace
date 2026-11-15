using System;
using System.Collections.Concurrent;

namespace Backspace.Services;

/// <summary>When you last pressed Stop for each backup, so that a check,
/// cleanup or restore check you stopped is recorded as
/// <see cref="Models.RunRecord.StoppedByYou"/> (U242, owner 15 November
/// 2026: "complete everything so it works consistently"). Such a run is not
/// a result: the backup's check health stays that of its last completed
/// check. A stop by Backspace itself (quitting, a metered connection) is not
/// marked.</summary>
internal static class StopsByYou
{
    private static readonly ConcurrentDictionary<string, DateTime> Pressed = new(StringComparer.Ordinal);

    /// <summary>You pressed Stop for this backup. Call it before cancelling.</summary>
    public static void Mark(string backupId) => Pressed[backupId] = DateTime.UtcNow;

    /// <summary>You pressed Stop for this backup while a run that started
    /// at <paramref name="startedUtc"/> was going on.</summary>
    public static bool Marks(string backupId, DateTime startedUtc)
        => Pressed.TryGetValue(backupId, out var at) && at >= startedUtc;
}
