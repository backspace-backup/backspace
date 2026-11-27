using System;
using Backspace.Models;

namespace Backspace.Tests;

/// <summary>Schedules for fixtures whose saved runs ended at some time today.</summary>
internal static class TestSchedules
{
    /// <summary>Daily, an hour from now: a backup completed in the last 23 hours
    /// has missed no scheduled time, whatever the time of day the test runs
    /// (U265: a scheduled time passed more than an hour ago without a run
    /// turns the card orange).</summary>
    public static BackupSchedule DueInAnHour()
    {
        var at = DateTime.Now.AddHours(1).TimeOfDay;
        return new BackupSchedule { Frequency = ScheduleFrequency.Daily, TimeOfDay = new TimeSpan(at.Hours, at.Minutes, 0) };
    }
}
