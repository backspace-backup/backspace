using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Backspace.Tests.Services;

/// <summary>U226 (owner, 12 November 2026): two conditions ("Run only while
/// on AC power", "Run only while on an unmetered connection"), no internet
/// setting, missed runs always run. A destination on this computer never
/// waits for the network; a backup's network destinations wait on their own
/// and run once the network allows, the local ones keeping their schedule
/// ("imagine we're offline for 7 days, the local destination should still be
/// getting fresh data every day"). One that can't be reached is tried again
/// after a network change or a day, never in a loop. Checks follow their
/// backup's conditions unless they have their own.</summary>
public sealed class ScheduleNetworkConditionsTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "backspace-schedule-net-" + Guid.NewGuid().ToString("N") + ".json");
    private DateTime _now = new DateTime(2026, 11, 4, 19, 0, 0, DateTimeKind.Local).ToUniversalTime(); // Wednesday 19:00
    private bool _battery, _offline, _metered;
    private readonly List<SchedulePlan> _runs = [];
    private Action<SchedulePlan> _work = _ => { };

    private readonly Destination _drive = new() { Name = "Drive", Kind = DestinationKind.ExternalDrive, PathOrSubpath = @"E:\Backups" };
    private readonly Destination _cloud = new() { Name = "Cloud", Kind = DestinationKind.GoogleDrive, PathOrSubpath = "backups" };

    public void Dispose() { try { File.Delete(_state); } catch { } }

    private static DateTime Local(int day, int hour, int minute = 0) => new DateTime(2026, 11, day, hour, minute, 0, DateTimeKind.Local).ToUniversalTime();

    private Backup Daily(params Destination[] to)
    {
        var backup = new Backup { Name = "Documents", Schedule = new BackupSchedule { Frequency = ScheduleFrequency.Daily, TimeOfDay = TimeSpan.FromHours(20) } };
        foreach (var d in to) backup.Targets.Add(new BackupTarget { DestinationId = d.Id, StorageName = "default" });
        return backup;
    }

    private string Key(Destination d) => Backup.TargetKey(d.Id, "default");

    private ScheduleLoop Loop(params Backup[] backups) => new(new(_state,
        () => SchedulePlan.From(new AppConfig { Backups = [.. backups], Destinations = [_drive, _cloud] }),
        (plan, ct) => { _runs.Add(plan); _work(plan); return System.Threading.Tasks.Task.FromResult(true); },
        plan => ScheduleConditions.Blocked(plan, [_drive, _cloud], () => _battery, () => !_offline, () => _metered),
        () => _battery, () => _now, TimeSpan.FromDays(1), TimeSpan.FromMilliseconds(20), Metered: () => _metered));

    [Fact]
    public void A_destination_on_this_computer_never_waits_for_the_network()
    {
        var local = Daily(_drive);
        var plan = SchedulePlan.From(new AppConfig { Backups = [local], Destinations = [_drive, _cloud] }).Single(p => p.Kind == ScheduledTaskKind.Backup);
        Assert.Null(ScheduleConditions.Blocked(plan, [_drive, _cloud], () => false, () => false, () => true));
        // The battery still holds it back.
        Assert.Equal("the computer is on battery", ScheduleConditions.Blocked(plan, [_drive, _cloud], () => true, () => true, () => false));
        // Only network: it waits.
        var cloud = SchedulePlan.From(new AppConfig { Backups = [Daily(_cloud)], Destinations = [_drive, _cloud] }).Single(p => p.Kind == ScheduledTaskKind.Backup);
        Assert.Equal("no network connection", ScheduleConditions.Blocked(cloud, [_drive, _cloud], () => false, () => false, () => false));
        Assert.Equal("the connection is metered", ScheduleConditions.Blocked(cloud, [_drive, _cloud], () => false, () => true, () => true));
        // A mixed backup isn't held back: its local part has work to do.
        var mixed = SchedulePlan.From(new AppConfig { Backups = [Daily(_drive, _cloud)], Destinations = [_drive, _cloud] }).Single(p => p.Kind == ScheduledTaskKind.Backup);
        Assert.Null(ScheduleConditions.Blocked(mixed, [_drive, _cloud], () => false, () => false, () => true));
        // Its waiting cloud part alone is.
        Assert.Equal("the connection is metered", ScheduleConditions.Blocked(mixed with { OnlyTargets = [Key(_cloud)] }, [_drive, _cloud], () => false, () => true, () => true));
    }

    [Fact]
    public async Task Offline_for_days_the_local_destination_keeps_its_schedule_and_the_cloud_runs_when_the_network_allows()
    {
        var backup = Daily(_drive, _cloud);
        var loop = Loop(backup);
        await loop.TickAsync(default);
        _metered = true;
        // The run on a metered connection reports the cloud as waiting.
        _work = plan => ScheduleLoop.ReportWaiting(plan.OnlyTargets is null ? [Key(_cloud)] : _metered ? [Key(_cloud)] : [], needsChange: false);
        for (var day = 4; day <= 6; day++)
        {
            _now = Local(day, 20, 1);
            for (var i = 0; i < 3; i++) await loop.TickAsync(default);
        }
        // Each day the regular run (all destinations) ran; the cloud alone never did while metered.
        Assert.Equal(3, _runs.Count(p => p.OnlyTargets is null));
        Assert.DoesNotContain(_runs, p => p.OnlyTargets is not null);
        // The connection is unmetered again: the cloud runs on its own, at once.
        _metered = false;
        _now = Local(6, 22);
        await loop.TickAsync(default);
        var catchUp = Assert.Single(_runs, p => p.OnlyTargets is not null);
        Assert.Equal(new[] { Key(_cloud) }, catchUp.OnlyTargets);
        // Nothing waits any more: no other run until the regular time.
        await loop.TickAsync(default);
        Assert.Single(_runs, p => p.OnlyTargets is not null);
        _now = Local(7, 20, 1);
        await loop.TickAsync(default);
        Assert.Equal(4, _runs.Count(p => p.OnlyTargets is null));
    }

    [Fact]
    public async Task A_destination_that_could_not_be_reached_is_tried_again_after_a_network_change_or_a_day_never_in_a_loop()
    {
        var backup = Daily(_cloud);
        // Weekly on Wednesdays, so the regular run doesn't come first.
        backup.Schedule.Frequency = ScheduleFrequency.Weekly;
        backup.Schedule.WeeklyDaysBitmask = 1 << (int)DayOfWeek.Wednesday;
        var loop = Loop(backup);
        await loop.TickAsync(default);
        _work = plan => ScheduleLoop.ReportWaiting([Key(_cloud)], needsChange: true);
        _now = Local(4, 20, 1);
        await loop.TickAsync(default);
        Assert.Single(_runs);
        // The network seems fine but nothing changed: no retry, however often it looks.
        for (var i = 0; i < 5; i++) { _now = _now.AddMinutes(10); await loop.TickAsync(default); }
        Assert.Single(_runs);
        // Windows reports a network change: tried once.
        loop.NetworkChanged();
        await loop.TickAsync(default);
        Assert.Equal(2, _runs.Count);
        Assert.Equal(new[] { Key(_cloud) }, _runs[1].OnlyTargets);
        await loop.TickAsync(default);
        Assert.Equal(2, _runs.Count);
        // Not before a day without a change...
        _now = _now.AddHours(23);
        await loop.TickAsync(default);
        Assert.Equal(2, _runs.Count);
        // ...and a day later at most.
        _now = _now.AddHours(2);
        await loop.TickAsync(default);
        Assert.Equal(3, _runs.Count);
        Assert.Equal(new[] { Key(_cloud) }, _runs[2].OnlyTargets);
    }

    [Fact]
    public async Task A_missed_run_always_runs_as_soon_as_possible()
    {
        var backup = Daily(_drive);
        backup.Schedule.CatchUpMissedRuns = false; // an older setting: no longer read
        var loop = Loop(backup);
        await loop.TickAsync(default);
        _now = Local(5, 8); // the computer was off at 20:00 yesterday
        await loop.TickAsync(default);
        Assert.Single(_runs);
    }

    [Fact]
    public void A_check_follows_its_backups_conditions_unless_it_has_its_own()
    {
        var backup = Daily(_cloud);
        backup.Schedule.SkipOnBattery = true;
        backup.WaitForUnmeteredNetwork = true;
        backup.Checks.Add(new BackupCheck { Kind = CheckKind.Structure, Schedule = new BackupSchedule { Frequency = ScheduleFrequency.Weekly, SkipOnBattery = false, UnmeteredOnly = false } });
        var check = SchedulePlan.From(new AppConfig { Backups = [backup], Destinations = [_cloud] }).Single(p => p.Kind == ScheduledTaskKind.Check);
        Assert.True(ScheduleConditions.OnlyOnAcPower(check));
        Assert.True(ScheduleConditions.UnmeteredOnly(check));
        Assert.True(ScheduleLoop.StopsOnBattery(check));
        Assert.Equal("the computer is on battery", ScheduleConditions.Blocked(check, [_cloud], () => true, () => true, () => false));
        // Its own: runs on battery and on a metered connection.
        backup.Checks[0].Schedule!.OwnConditions = true;
        Assert.False(ScheduleConditions.OnlyOnAcPower(check));
        Assert.False(ScheduleConditions.UnmeteredOnly(check));
        Assert.False(ScheduleLoop.StopsOnBattery(check));
        Assert.Null(ScheduleConditions.Blocked(check, [_cloud], () => true, () => true, () => true));
    }

    [Fact]
    public void Pending_destinations_survive_the_regular_time_moving_on()
    {
        var backup = Daily(_drive, _cloud);
        var plan = SchedulePlan.From(new AppConfig { Backups = [backup], Destinations = [_drive, _cloud] }).Single(p => p.Kind == ScheduledTaskKind.Backup);
        var state = ScheduleState.Load(_state);
        state.Set(plan.Key, new ScheduleState.Entry(Local(4, 20), plan.Timing, Pending: [Key(_cloud)], PendingSinceUtc: Local(4, 20)));
        SchedulePlanner.Advance(state, plan, Local(4, 21));
        Assert.Equal(new[] { Key(_cloud) }, state.Entries[plan.Key].Pending);
        Assert.Equal(Local(5, 20), state.Entries[plan.Key].NextUtc);
    }
}
