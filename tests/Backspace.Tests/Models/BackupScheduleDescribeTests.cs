using System;
using System.Globalization;
using Backspace.Models;
using Xunit;

namespace Backspace.Tests.Models;

/// <summary>
/// Regression tests for the next-run + period describer. The user
/// asked for the parens to carry the full periodicity (not just
/// "Daily") and for the time format to respect the user's locale.
/// These tests pin both behaviors so a future refactor doesn't
/// silently regress them back to "Daily" / hardcoded HH:mm.
/// </summary>
public class BackupScheduleDescribeTests
{
    [Theory]
    [InlineData("2026-10-18T08:00:00", "20:00:00", 4, "2026-10-18T20:00:00")]
    [InlineData("2026-10-18T18:00:00", "20:00:00", 4, "2026-10-18T20:00:00")]
    [InlineData("2026-10-18T20:00:00", "20:00:00", 4, "2026-10-19T00:00:00")]
    [InlineData("2026-10-18T12:00:00", "01:00:00", 4, "2026-10-18T13:00:00")]
    [InlineData("2026-10-18T23:59:00", "20:30:30", 4, "2026-10-19T00:30:30")]
    [InlineData("2026-10-18T12:00:00", "00:00:00.125", 4, "2026-10-18T12:00:00.125")]
    [InlineData("2026-10-18T12:00:00", "00:00:00", 0, "2026-10-18T13:00:00")]
    public void Hourly_next_run_preserves_the_start_and_returns_the_first_later_interval(
        string now, string start, int hours, string expected)
    {
        var schedule = new BackupSchedule { Frequency = ScheduleFrequency.Hourly,
            TimeOfDay = TimeSpan.Parse(start, CultureInfo.InvariantCulture), HourlyInterval = hours };
        Assert.Equal(DateTime.Parse(expected, CultureInfo.InvariantCulture),
            schedule.ComputeNextFire(DateTime.Parse(now, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Hourly_interval_beyond_the_representable_date_has_no_next_run()
    {
        var schedule = new BackupSchedule { Frequency = ScheduleFrequency.Hourly,
            TimeOfDay = TimeSpan.Zero, HourlyInterval = int.MaxValue };
        Assert.Null(schedule.ComputeNextFire(new DateTime(2026, 10, 18, 12, 0, 0)));
    }

    [Theory]
    [InlineData("en-US", "Daily at 8:00 PM")]
    [InlineData("en-GB", "Daily at 20:00")]
    public void DescribePeriod_Daily_includesTimeOfDay(string culture, string expected)
    {
        var s = new BackupSchedule
        {
            Frequency = ScheduleFrequency.Daily,
            TimeOfDay = TimeSpan.FromHours(20),
        };
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(expected, s.DescribePeriod());
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void DescribePeriod_Hourly_doesNotShowTimeOfDay()
    {
        var s = new BackupSchedule
        {
            Frequency = ScheduleFrequency.Hourly,
            HourlyInterval = 4,
        };
        Assert.Equal("Every 4h", s.DescribePeriod());
    }

    [Fact]
    public void DescribePeriod_Hourly_intervalOne_isHourly()
    {
        var s = new BackupSchedule
        {
            Frequency = ScheduleFrequency.Hourly,
            HourlyInterval = 1,
        };
        Assert.Equal("Hourly", s.DescribePeriod());
    }

    [Fact]
    public void DescribePeriod_WeeklyEveryDay_collapsesToDailyAt()
    {
        var s = new BackupSchedule
        {
            Frequency = ScheduleFrequency.Weekly,
            TimeOfDay = TimeSpan.FromHours(14),
            WeeklyDaysBitmask = 0b0111_1111,
        };
        Assert.StartsWith("Daily at ", s.DescribePeriod());
    }

    [Fact]
    public void DescribePeriod_WeeklyMonFri_listsDays()
    {
        // Mon=2, Fri=32 → 0b0010_0010 = 34
        var s = new BackupSchedule
        {
            Frequency = ScheduleFrequency.Weekly,
            TimeOfDay = TimeSpan.FromHours(14),
            WeeklyDaysBitmask = (1 << 1) | (1 << 5),
        };
        var period = s.DescribePeriod();
        Assert.Contains("Weekly at ", period);
        Assert.Contains("Mon", period);
        Assert.Contains("Fri", period);
    }

    [Fact]
    public void DescribeNextRun_includesAbsoluteAndPeriodInParens()
    {
        var s = new BackupSchedule
        {
            Frequency = ScheduleFrequency.Daily,
            TimeOfDay = TimeSpan.FromHours(20),
        };
        // 14:00 today → next fire is "tonight 20:00" (still on the same day)
        var now = DateTime.Today.Add(TimeSpan.FromHours(14));
        var label = s.DescribeNextRun(now);
        // The parens must say "(Daily)" — without the "at HH:MM" suffix
        // since the absolute prefix already carries the time. The user
        // flagged the previous "(Daily at 20:00)" trailing the absolute
        // "Tonight 20:00" as redundant: showing the same time twice on
        // the same line.
        Assert.Contains("(Daily)", label);
        Assert.DoesNotContain("Daily at", label);
    }

    /// <summary>U265 (owner, 27 November 2026: "Why tolerate...?"): the first
    /// scheduled time after the last backup counts as missed once it is more
    /// than an hour past without a run; a time inside a pause does not count.</summary>
    [Fact]
    public void A_scheduled_time_an_hour_past_without_a_run_is_missed()
    {
        static DateTime At(int day, int hour, int minute) => new DateTime(2026, 11, day, hour, minute, 0, DateTimeKind.Local).ToUniversalTime();
        var daily = new BackupSchedule { Frequency = ScheduleFrequency.Daily, TimeOfDay = TimeSpan.FromHours(20) };
        var last = At(25, 20, 10);
        Assert.Null(daily.MissedSlotUtc(last, At(26, 19, 0)));
        Assert.Null(daily.MissedSlotUtc(last, At(26, 20, 59)));
        Assert.Equal(At(26, 20, 0), daily.MissedSlotUtc(last, At(26, 21, 1)));
        Assert.True(daily.Missed(At(17, 20, 10), At(26, 12, 0)));
        // A run that finished after its time (a long run, a catch-up) moves on to the next time.
        Assert.False(daily.Missed(At(26, 23, 40), At(27, 12, 0)));
        // Paused: never missed while it lasts, and times inside it don't count once it ended.
        var paused = new BackupSchedule { Frequency = ScheduleFrequency.Daily, TimeOfDay = TimeSpan.FromHours(20), PausedUntilUtc = At(27, 12, 0) };
        Assert.False(paused.Missed(last, At(26, 23, 0)));
        Assert.False(paused.Missed(last, At(27, 13, 0)));
        Assert.True(paused.Missed(last, At(27, 21, 30)));
        // Every 4 hours from midnight.
        var hourly = new BackupSchedule { Frequency = ScheduleFrequency.Hourly, HourlyInterval = 4, TimeOfDay = TimeSpan.Zero };
        Assert.False(hourly.Missed(At(26, 8, 5), At(26, 12, 30)));
        Assert.True(hourly.Missed(At(26, 8, 5), At(26, 13, 30)));
        // Mondays only (23 November 2026 is a Monday).
        var weekly = new BackupSchedule { Frequency = ScheduleFrequency.Weekly, WeeklyDaysBitmask = 0b0000010, TimeOfDay = TimeSpan.FromHours(20) };
        Assert.False(weekly.Missed(At(23, 20, 10), At(28, 12, 0)));
        Assert.True(weekly.Missed(At(16, 20, 10), At(23, 21, 30)));
        Assert.False(weekly.Missed(At(16, 20, 10), At(23, 20, 30)));
    }
}
