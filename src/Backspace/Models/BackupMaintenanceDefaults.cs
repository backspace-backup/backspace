using System;
using System.Collections.Generic;

namespace Backspace.Models;

/// <summary>
/// Single source of truth for the maintenance new backups get, in Easy mode
/// (which shows it as one sentence) and in the Advanced editor alike, so the
/// two can't drift.
///
/// Defaults approved by the owner on 10 November 2026 (U179): cleanup every
/// week; a structure check and a 1 GiB data sample every week; a sample test
/// restore (1%, at most 1 GiB) every month; no full data read. All at 3 AM,
/// on Sunday for the weekly ones and on the 1st for the monthly one. Existing
/// backups keep what they have.
/// </summary>
public static class BackupMaintenanceDefaults
{
    /// <summary>Default time of day for maintenance: 3 AM, outside the
    /// typical 8 PM backup window and when the computer is usually idle.</summary>
    public static readonly TimeSpan OffHours = TimeSpan.FromHours(3);

    /// <summary>Apply the recommended cleanup and checks in place. They
    /// inherit the backup schedule's battery, network and missed-run
    /// conditions. Call before the person edits any of these.</summary>
    public static void ApplyTo(Backup b)
    {
        b.PruneSchedule = BuildSchedule(b.Schedule, ScheduleFrequency.Weekly);
        b.Checks = Checks(b.Schedule);
    }

    /// <summary>The recommended checks (U179).</summary>
    public static List<BackupCheck> Checks(BackupSchedule main) =>
    [
        new() { Kind = CheckKind.Structure, Schedule = BuildSchedule(main, ScheduleFrequency.Weekly) },
        new() { Kind = CheckKind.DataSample, Schedule = BuildSchedule(main, ScheduleFrequency.Weekly), MaximumBytes = BackupCheck.OneGiB },
        new() { Kind = CheckKind.TestRestore, Schedule = BuildSchedule(main, ScheduleFrequency.Monthly), SamplePercent = 1, MaximumBytes = BackupCheck.OneGiB },
    ];

    /// <summary>A new check added in the editor: weekly at 3 AM, or for a full
    /// data read, monthly.</summary>
    public static BackupCheck NewCheck(CheckKind kind, BackupSchedule main) => new()
    {
        Kind = kind,
        Schedule = BuildSchedule(main, kind is CheckKind.TestRestore or CheckKind.FullDataRead ? ScheduleFrequency.Monthly : ScheduleFrequency.Weekly),
    };

    public static BackupSchedule BuildSchedule(BackupSchedule main, ScheduleFrequency freq, TimeSpan? timeOfDay = null) =>
        new()
        {
            Frequency = freq,
            TimeOfDay = timeOfDay ?? OffHours,
            HourlyInterval = 24,
            // Weekly means once a week (Sunday); the model's default of
            // every day would turn it into a daily run.
            WeeklyDaysBitmask = freq == ScheduleFrequency.Weekly ? 0b0000_0001 : 0b0111_1111,
            // Inherit run-time guards from the backup's own schedule so
            // maintenance never bypasses the "skip on battery" / "network"
            // / "catch up missed runs" preferences.
            SkipOnBattery = main.SkipOnBattery,
            StopOnBattery = main.StopOnBattery,
            RequireNetwork = main.RequireNetwork,
            CatchUpMissedRuns = main.CatchUpMissedRuns,
        };
}
