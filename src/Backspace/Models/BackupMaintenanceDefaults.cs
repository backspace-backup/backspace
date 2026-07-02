using System;

namespace Backspace.Models;

/// <summary>
/// Single source of truth for the "recommended out-of-the-box"
/// maintenance cadences applied to new backups. Both the Easy-mode
/// wizard (which hides these knobs entirely, same as retention policy)
/// and the Advanced editor (which surfaces them as the Pruning Policy /
/// Check Policy cards) call <see cref="ApplyTo"/> on a fresh
/// <see cref="Backup"/> so the two paths can't drift.
///
/// Defaults: Weekly prune off-hours + Monthly check off-hours. This
/// matches restic best-practice for non-trivial repos — prune is
/// repack-heavy and shouldn't run after every backup once the repo
/// grows past trivial; integrity check is cheap-ish but not free, and
/// monthly catches data-at-rest corruption with negligible overhead.
/// Existing backups (loaded from on-disk config) keep whatever values
/// they already have — these defaults only fire when callers
/// explicitly opt into them.
/// </summary>
public static class BackupMaintenanceDefaults
{
    /// <summary>Default time-of-day for both maintenance passes. 3 AM
    /// local time — outside the typical 8 PM backup window so prune /
    /// check don't queue behind a still-running backup, and during
    /// hours where the user almost certainly isn't using the machine.</summary>
    public static readonly TimeSpan OffHours = TimeSpan.FromHours(3);

    /// <summary>
    /// Apply the recommended cadences in place. Inherits the main
    /// schedule's battery / metered / catch-up preconditions so a
    /// maintenance run never violates "don't run on battery" intent.
    /// Safe to call on a Backup whose Schedule is still at defaults —
    /// the preconditions are also defaulted in <see cref="BackupSchedule"/>.
    /// Caller should call this BEFORE the user has edited any of these
    /// fields; calling on an already-configured Backup overwrites the
    /// schedule entries.
    /// </summary>
    public static void ApplyTo(Backup b)
    {
        b.PruneSchedule = BuildSchedule(b.Schedule, ScheduleFrequency.Weekly);
        b.CheckSchedule = BuildSchedule(b.Schedule, ScheduleFrequency.Monthly);
    }

    private static BackupSchedule BuildSchedule(BackupSchedule main, ScheduleFrequency freq) =>
        new()
        {
            Frequency = freq,
            TimeOfDay = OffHours,
            HourlyInterval = 24,
            // Inherit run-time guards from the backup's own schedule
            // so the maintenance pass never bypasses the user's
            // "skip on battery" / "skip on metered" / "catch up
            // missed runs" preferences.
            SkipOnBattery = main.SkipOnBattery,
            StopOnBattery = main.StopOnBattery,
            RequireNetwork = main.RequireNetwork,
            CatchUpMissedRuns = main.CatchUpMissedRuns,
        };
}
