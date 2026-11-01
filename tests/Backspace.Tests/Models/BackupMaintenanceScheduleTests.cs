using System;
using System.Text.Json;
using Backspace.Models;
using Xunit;

namespace Backspace.Tests.Models;

/// <summary>
/// Pins the per-backup PruneSchedule / CheckSchedule fields:
///   • Default to null (legacy inline behaviour stays the default)
///   • Round-trip through JSON without loss
///   • A loaded config WITHOUT the fields populated still parses
///     (backwards-compat with v0.1 and earlier configs)
/// </summary>
public class BackupMaintenanceScheduleTests
{
    [Fact]
    public void NewBackup_DefaultsPruneAndCheckSchedulesToNull()
    {
        var b = new Backup();
        // Null schedule = run the maintenance pass inline after each
        // backup. This is the simple-case default the BackupEditor
        // surfaces as "After each backup" in the cadence combo.
        Assert.Null(b.PruneSchedule);
        Assert.Null(b.CheckSchedule);
    }

    [Fact]
    public void Backup_RoundTripsPruneAndCheckSchedulesThroughJson()
    {
        var src = new Backup
        {
            Name = "weekly",
            PruneSchedule = new BackupSchedule
            {
                Frequency = ScheduleFrequency.Weekly,
                TimeOfDay = TimeSpan.FromHours(3),
                WeeklyDaysBitmask = 0b0000_0001, // Sunday only
            },
            CheckSchedule = new BackupSchedule
            {
                Frequency = ScheduleFrequency.Hourly,
                HourlyInterval = 24,
            },
        };

        var json = JsonSerializer.Serialize(src);
        var rt = JsonSerializer.Deserialize<Backup>(json)!;

        Assert.NotNull(rt.PruneSchedule);
        Assert.Equal(ScheduleFrequency.Weekly, rt.PruneSchedule!.Frequency);
        Assert.Equal(TimeSpan.FromHours(3), rt.PruneSchedule.TimeOfDay);
        Assert.Equal(0b0000_0001, rt.PruneSchedule.WeeklyDaysBitmask);

        Assert.NotNull(rt.CheckSchedule);
        Assert.Equal(ScheduleFrequency.Hourly, rt.CheckSchedule!.Frequency);
        Assert.Equal(24, rt.CheckSchedule.HourlyInterval);
    }

    [Fact]
    public void ComputeNextFire_Monthly_FirstOfThisMonthAtTimeOfDay()
    {
        // Monthly cadence (added with the Pruning/Check policy combo)
        // fires on the 1st of every month at TimeOfDay. Two cases —
        // "now is before this month's 1st @ 03:00" → fire at 03:00 on
        // the 1st of this month; "now is after" → fire on the 1st of
        // next month. Pins both branches so the BackupSchedule helper
        // stays in sync with WindowsTaskScheduler's MonthlyTrigger.
        var schedule = new BackupSchedule
        {
            Frequency = ScheduleFrequency.Monthly,
            TimeOfDay = TimeSpan.FromHours(3),
        };

        // Case 1: we're after the 1st of March 03:00 → next fire is
        // April 1st 03:00.
        var afterFirst = new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Local);
        var next1 = schedule.ComputeNextFire(afterFirst);
        Assert.NotNull(next1);
        Assert.Equal(new DateTime(2026, 4, 1, 3, 0, 0, DateTimeKind.Local), next1);

        // Case 2: we're BEFORE the 1st of March 03:00 (i.e. it's still
        // Feb 28 11pm) → next fire is March 1st 03:00.
        var beforeFirst = new DateTime(2026, 3, 1, 1, 0, 0, DateTimeKind.Local);
        var next2 = schedule.ComputeNextFire(beforeFirst);
        Assert.NotNull(next2);
        Assert.Equal(new DateTime(2026, 3, 1, 3, 0, 0, DateTimeKind.Local), next2);
    }

    [Fact]
    public void Backup_LoadsConfigWithoutMaintenanceFields_AsNull()
    {
        // A v0.1 / pre-feature config doesn't have PruneSchedule or
        // CheckSchedule keys at all. The new optional fields must
        // tolerate that — System.Text.Json fills missing properties
        // with their declared defaults (null for the nullable
        // reference types). Without this guarantee an old user's
        // config would fail to load after an upgrade.
        var legacyJson = """{"Name":"docs","SourcePaths":["C:/Users/me/Docs"]}""";
        var rt = JsonSerializer.Deserialize<Backup>(legacyJson)!;
        Assert.Null(rt.PruneSchedule);
        Assert.Null(rt.CheckSchedule);
    }
}
