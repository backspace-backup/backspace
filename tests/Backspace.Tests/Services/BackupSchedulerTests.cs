using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform;
using Backspace.Tests.TestHelpers;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Backspace.Tests.Services;

/// <summary>U043 (31 October 2026): Backspace keeps the schedule itself, in
/// the tray app or the background service, instead of Windows tasks. These
/// drive the shared loop with a fake clock and runner.</summary>
public sealed class BackupSchedulerTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "backspace-schedule-" + Guid.NewGuid().ToString("N") + ".json");
    private DateTime _now = new DateTime(2026, 11, 4, 19, 0, 0, DateTimeKind.Local).ToUniversalTime(); // Wednesday 19:00
    private bool _battery, _offline, _metered, _admit = true;
    private string? _blocked;
    private readonly List<(string Name, ScheduledTaskKind Kind, DateTime At)> _runs = [];
    private Func<CancellationToken, Task> _work = _ => Task.CompletedTask;

    public void Dispose() { try { File.Delete(_state); } catch { } }

    private static Backup Daily(string name = "Documents", int hour = 20) => new()
    {
        Name = name,
        Schedule = new BackupSchedule { Frequency = ScheduleFrequency.Daily, TimeOfDay = TimeSpan.FromHours(hour), RequireNetwork = false },
    };

    private ScheduleLoop Loop(params Backup[] backups) => new(new(_state,
        () => SchedulePlan.From(new AppConfig { Backups = [.. backups] }),
        async (plan, ct) =>
        {
            if (!_admit) return false;
            _runs.Add((plan.Backup.Name, plan.Kind, _now));
            await _work(ct);
            return true;
        },
        plan => _blocked ?? ScheduleConditions.Blocked(plan, [], () => _battery, () => !_offline),
        () => _battery, () => _now, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(20), Metered: () => _metered));

    private static DateTime Local(int day, int hour, int minute = 0, int second = 0)
        => new DateTime(2026, 11, day, hour, minute, second, DateTimeKind.Local).ToUniversalTime();

    [Fact]
    public async Task A_backup_runs_once_at_its_time_and_not_before()
    {
        var loop = Loop(Daily());
        Assert.Equal(TimeSpan.FromMinutes(1), await loop.TickAsync(default)); // first sight: next is 20:00
        _now = Local(4, 19, 59);
        await loop.TickAsync(default);
        Assert.Empty(_runs);
        _now = Local(4, 20, 0);
        Assert.Equal(TimeSpan.Zero, await loop.TickAsync(default));
        _now = Local(4, 20, 1);
        await loop.TickAsync(default);
        Assert.Single(_runs);
        Assert.Equal(Local(4, 20), _runs[0].At);
    }

    [Fact]
    public async Task A_run_stopped_by_a_metered_connection_or_battery_runs_again_once_the_condition_ends()
    {
        // U185 (owner, 11 November 2026): stop a check mid-run on battery or a
        // metered connection, then relaunch it once the conditions are met.
        var backup = Daily();
        var loop = Loop(backup);
        await loop.TickAsync(default);
        _now = Local(4, 20, 0);
        _work = _ => { _metered = true; _blocked = "the connection is metered"; return Task.CompletedTask; };
        await loop.TickAsync(default);
        Assert.Single(_runs);
        _work = _ => Task.CompletedTask;
        _now = Local(4, 20, 30);
        await loop.TickAsync(default); // still metered: waits
        Assert.Single(_runs);
        _metered = false; _blocked = null;
        _now = Local(4, 21, 0);
        await loop.TickAsync(default); // unmetered: the same slot runs again
        Assert.Equal(2, _runs.Count);
        await loop.TickAsync(default); // finished normally: not again
        Assert.Equal(2, _runs.Count);

        // Battery the same way, with "stop if the computer switches to battery".
        _now = Local(5, 20, 0);
        _work = _ => { _battery = true; return Task.CompletedTask; };
        await loop.TickAsync(default);
        Assert.Equal(3, _runs.Count);
        _work = _ => Task.CompletedTask;
        _now = Local(5, 20, 10);
        await loop.TickAsync(default);
        Assert.Equal(3, _runs.Count); // on battery: waits
        _battery = false;
        await loop.TickAsync(default);
        Assert.Equal(4, _runs.Count);

        // A run that completed, and the connection became metered just
        // after: it is not run again (owner, 11 November 2026: "Yes do it").
        _now = Local(6, 20, 0);
        _work = _ => { ScheduleLoop.ReportCompleted(); _metered = true; _blocked = "the connection is metered"; return Task.CompletedTask; };
        await loop.TickAsync(default);
        Assert.Equal(5, _runs.Count);
        _work = _ => Task.CompletedTask;
        _metered = false; _blocked = null;
        _now = Local(6, 20, 30);
        await loop.TickAsync(default);
        Assert.Equal(5, _runs.Count);
    }

    [Fact]
    public void A_backup_that_stops_on_a_metered_connection_does_not_start_on_one()
    {
        var cloud = new Destination { Kind = DestinationKind.GoogleDrive, Name = "Drive" };
        var backup = Daily();
        backup.Targets.Add(new BackupTarget { DestinationId = cloud.Id });
        var plan = SchedulePlan.From(new AppConfig { Backups = [backup], Destinations = [cloud] }).Single(p => p.Kind == ScheduledTaskKind.Backup);
        Assert.Equal("the connection is metered", ScheduleConditions.Blocked(plan, [cloud], () => false, () => true, () => true));
        Assert.Null(ScheduleConditions.Blocked(plan, [cloud], () => false, () => true, () => false));
        backup.AbortOnMeteredNetwork = false;
        Assert.Null(ScheduleConditions.Blocked(plan, [cloud], () => false, () => true, () => true));
    }

    [Fact]
    public async Task Idle_sleep_lasts_until_the_next_run_but_at_most_a_minute()
    {
        var loop = Loop(Daily());
        await loop.TickAsync(default);
        _now = Local(4, 19, 59, 30);
        Assert.Equal(TimeSpan.FromSeconds(30), await loop.TickAsync(default));
    }

    [Fact]
    public async Task A_run_missed_while_off_runs_once_on_return_with_catch_up_and_not_without()
    {
        var loop = Loop(Daily());
        await loop.TickAsync(default);
        _now = Local(7, 9); // three slots missed: off for the weekend
        await loop.TickAsync(default);
        await loop.TickAsync(default);
        Assert.Single(_runs); // once, not three times

        var noCatchUp = Daily("Photos");
        noCatchUp.Schedule.CatchUpMissedRuns = false;
        _runs.Clear();
        _now = Local(4, 19);
        File.Delete(_state);
        var strict = Loop(noCatchUp);
        await strict.TickAsync(default);
        _now = Local(4, 20, 9); // nine minutes late: still runs
        await strict.TickAsync(default);
        Assert.Single(_runs);
        _now = Local(5, 20, 11); // eleven minutes late: missed
        await strict.TickAsync(default);
        await strict.TickAsync(default);
        Assert.Single(_runs);
    }

    [Fact]
    public async Task The_state_survives_a_restart_so_a_quit_run_is_not_repeated()
    {
        var backup = Daily(); // the same saved backup each time Backspace opens
        await Loop(backup).TickAsync(default);
        _now = Local(4, 20);
        await Loop(backup).TickAsync(default);
        Assert.Single(_runs);
        _now = Local(4, 21); // reopened later the same evening
        await Loop(backup).TickAsync(default);
        Assert.Single(_runs);
    }

    /// <summary>U131 (4 November 2026): the PC went down during the night in
    /// the middle of a backup. The slot was already marked done, so it would
    /// have waited until the next evening. A run cut short is resumed once.</summary>
    [Fact]
    public async Task A_run_cut_short_by_a_crash_is_resumed_once_on_the_next_start()
    {
        var backup = Daily();
        await Loop(backup).TickAsync(default);
        _now = Local(4, 20);
        _work = ct => Task.Delay(Timeout.Infinite, ct); // still running when the PC goes down
        using var crash = new CancellationTokenSource();
        var running = Loop(backup).TickAsync(crash.Token);
        Assert.True(SpinWait.SpinUntil(() => _runs.Count == 1, 5000));
        // The service ends without finishing the run (its process is gone).
        crash.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        // The PC is back at 09:52: the interrupted 20:00 run is due again.
        _now = Local(5, 9, 52);
        var afterCrash = Loop(backup);
        afterCrash.ResumeInterruptedRuns();
        await afterCrash.TickAsync(crash.Token).ContinueWith(_ => { }); // cut short again
        Assert.Equal(2, _runs.Count);
        Assert.Equal(Local(5, 9, 52), _runs[1].At);

        // Cut short a second time: no third attempt before the next slot.
        _work = _ => Task.CompletedTask;
        var again = Loop(backup);
        again.ResumeInterruptedRuns();
        _now = Local(5, 10);
        await again.TickAsync(default);
        Assert.Equal(2, _runs.Count);
        _now = Local(5, 20);
        await again.TickAsync(default);
        Assert.Equal(3, _runs.Count);

        // A run that ends normally is never resumed.
        _now = Local(5, 21);
        var reopened = Loop(backup);
        reopened.ResumeInterruptedRuns();
        await reopened.TickAsync(default);
        Assert.Equal(3, _runs.Count);
    }

    /// <summary>U131: when Windows shuts down or restarts, the service stops
    /// the run and the run returns normally; it is still resumed, even with
    /// "Catch up missed runs" off, since it wasn't missed.</summary>
    [Fact]
    public async Task A_run_stopped_by_Windows_shutting_down_is_resumed_even_without_catch_up()
    {
        var backup = Daily();
        backup.Schedule.CatchUpMissedRuns = false;
        await Loop(backup).TickAsync(default);
        _now = Local(4, 20);
        using var shutdown = new CancellationTokenSource();
        _work = async ct => { try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { } }; // ends cleanly when stopped
        var running = Loop(backup).TickAsync(shutdown.Token);
        Assert.True(SpinWait.SpinUntil(() => _runs.Count == 1, 5000));
        shutdown.Cancel();
        await running;

        _now = Local(5, 8); // 12 hours later: far past the 10-minute grace
        _work = _ => Task.CompletedTask;
        var restarted = Loop(backup);
        restarted.ResumeInterruptedRuns();
        await restarted.TickAsync(default);
        Assert.Equal(2, _runs.Count);
        Assert.Equal(Local(5, 8), _runs[1].At);
        // Then the normal schedule: 20:00, not again before.
        _now = Local(5, 19);
        await restarted.TickAsync(default);
        Assert.Equal(2, _runs.Count);
        _now = Local(5, 20);
        await restarted.TickAsync(default);
        Assert.Equal(3, _runs.Count);
    }

    /// <summary>U131: a backup the service started outside its schedule
    /// (Backspace --run), cut short (Windows
    /// restarting or the computer losing power), is resumed by the schedule;
    /// the resume cut short in turn is not.</summary>
    [Fact]
    public async Task A_backup_run_by_hand_and_cut_short_is_resumed_once()
    {
        var backup = Daily();
        await Loop(backup).TickAsync(default); // the schedule knows it: next 20:00
        Assert.False(ScheduleState.MarkCutShort(_state, "no-such-backup"));
        Assert.True(ScheduleState.MarkCutShort(_state, backup.Id)); // cut short at 19:00

        _now = Local(4, 19, 30);
        var restarted = Loop(backup);
        restarted.ResumeInterruptedRuns();
        _work = ct => Task.Delay(Timeout.Infinite, ct);
        using var powerLoss = new CancellationTokenSource();
        var resumed = restarted.TickAsync(powerLoss.Token);
        Assert.True(SpinWait.SpinUntil(() => _runs.Count == 1, 5000));
        Assert.Equal(Local(4, 19, 30), _runs[0].At);
        // The resume is cut short too: after a crash or power loss the service
        // is told it can't resume (no endless restarts), after a clean stop
        // (Windows shutting down, an update) it can.
        Assert.False(ScheduleState.MarkCutShort(_state, backup.Id));
        powerLoss.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resumed);

        _now = Local(4, 19, 40);
        _work = _ => Task.CompletedTask;
        var again = Loop(backup);
        again.ResumeInterruptedRuns();
        await again.TickAsync(default);
        Assert.Single(_runs);
        _now = Local(4, 20);
        await again.TickAsync(default); // the normal slot still runs
        Assert.Equal(2, _runs.Count);
    }

    /// <summary>U131: on 4 November 2026 an update of Backspace stopped a
    /// resumed local_c run, which then waited until 20:00. A resume stopped
    /// cleanly resumes again; only a crash or power loss ends the retries.</summary>
    [Fact]
    public async Task A_resume_stopped_cleanly_resumes_again()
    {
        var backup = Daily();
        await Loop(backup).TickAsync(default);
        Assert.True(ScheduleState.MarkCutShort(_state, backup.Id));
        _now = Local(4, 19, 10);
        var first = Loop(backup);
        first.ResumeInterruptedRuns();
        using var update = new CancellationTokenSource();
        _work = async ct => { try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { } };
        var resumed = first.TickAsync(update.Token);
        Assert.True(SpinWait.SpinUntil(() => _runs.Count == 1, 5000));
        Assert.True(ScheduleState.MarkCutShort(_state, backup.Id, stopped: true)); // the service stops for an update
        update.Cancel();
        await resumed;

        _now = Local(4, 19, 20);
        _work = _ => Task.CompletedTask;
        var second = Loop(backup);
        second.ResumeInterruptedRuns();
        await second.TickAsync(default);
        Assert.Equal(2, _runs.Count);
        Assert.Equal(Local(4, 19, 20), _runs[1].At);
    }

    [Fact]
    public async Task A_due_run_waits_for_power_network_and_Quit_then_runs_once()
    {
        var cloud = Daily();
        cloud.Schedule.RequireNetwork = true;
        cloud.Targets.Add(new BackupTarget { DestinationId = "cloud" }); // not a local disk
        var loop = Loop(cloud);
        await loop.TickAsync(default);
        _now = Local(4, 20);
        _battery = true;
        Assert.Equal(TimeSpan.FromMinutes(1), await loop.TickAsync(default));
        _battery = false; _offline = true; _now = Local(4, 22);
        await loop.TickAsync(default);
        _offline = false; _admit = false; // Quit saved a pause: not admitted
        var due = ScheduleState.Load(_state).Entries.Values.Single().NextUtc;
        // Waits like a blocked run. Retrying at once spun a CPU core in the
        // VM while the service was paused (31 October 2026).
        Assert.Equal(TimeSpan.FromMinutes(1), await loop.TickAsync(default));
        Assert.Equal(TimeSpan.FromMinutes(1), await loop.TickAsync(default));
        Assert.Equal(due, ScheduleState.Load(_state).Entries.Values.Single().NextUtc);
        Assert.Empty(_runs);
        _admit = true; _now = Local(5, 21); // past the next slot too: still one run
        await loop.TickAsync(default);
        await loop.TickAsync(default);
        Assert.Single(_runs);
    }

    [Fact]
    public async Task Disabled_and_snoozed_backups_pass_over_their_slot()
    {
        var disabled = Daily("Off"); disabled.Enabled = false;
        var snoozed = Daily("Snoozed"); snoozed.Schedule.PausedUntilUtc = Local(5, 12);
        var loop = Loop(disabled, snoozed);
        await loop.TickAsync(default);
        _now = Local(4, 20, 30);
        await loop.TickAsync(default);
        Assert.Empty(_runs);
        _now = Local(5, 13); // snooze over, but the missed slot was passed over
        await loop.TickAsync(default);
        Assert.Empty(_runs);
        _now = Local(5, 20);
        await loop.TickAsync(default);
        Assert.Equal("Snoozed", Assert.Single(_runs).Name);
    }

    [Fact]
    public async Task Changing_the_time_takes_effect_without_running_early()
    {
        var backup = Daily();
        var loop = Loop(backup);
        await loop.TickAsync(default);
        backup.Schedule.TimeOfDay = TimeSpan.FromHours(22);
        _now = Local(4, 20, 30);
        await loop.TickAsync(default);
        Assert.Empty(_runs);
        _now = Local(4, 22);
        await loop.TickAsync(default);
        Assert.Single(_runs);
    }

    [Fact]
    public async Task Runs_never_overlap_and_slots_passed_during_a_long_run_are_skipped()
    {
        var hourly = new Backup { Name = "Hourly", Schedule = new BackupSchedule { Frequency = ScheduleFrequency.Hourly, HourlyInterval = 1, TimeOfDay = TimeSpan.Zero, RequireNetwork = false } };
        var loop = Loop(hourly);
        await loop.TickAsync(default);
        _now = Local(4, 20);
        _work = _ => { _now = Local(4, 22, 30); return Task.CompletedTask; }; // ran 2.5 hours
        await loop.TickAsync(default);
        _work = _ => Task.CompletedTask;
        await loop.TickAsync(default);
        Assert.Single(_runs);
        _now = Local(4, 23);
        await loop.TickAsync(default);
        Assert.Equal(2, _runs.Count);
    }

    [Fact]
    public async Task Check_and_prune_follow_their_own_schedules()
    {
        var backup = Daily();
        backup.PruneSchedule = new BackupSchedule { Frequency = ScheduleFrequency.Weekly, WeeklyDaysBitmask = 1 << 0, TimeOfDay = TimeSpan.FromHours(3), RequireNetwork = false };
        backup.CheckSchedule = new BackupSchedule { Frequency = ScheduleFrequency.Monthly, TimeOfDay = TimeSpan.FromHours(4), RequireNetwork = false };
        var loop = Loop(backup);
        await loop.TickAsync(default);
        _now = Local(8, 3, 30); // Sunday
        for (var i = 0; i < 12; i++) await loop.TickAsync(default);
        Assert.Contains(_runs, r => r.Kind == ScheduledTaskKind.Prune);
        Assert.DoesNotContain(_runs, r => r.Kind == ScheduledTaskKind.Check);
        Assert.Equal(2, _runs.Count); // one catch-up backup for the missed evenings, and the prune
    }

    [Fact]
    public async Task Time_limit_and_going_on_battery_stop_a_running_backup()
    {
        var backup = Daily();
        backup.Schedule.ExecutionTimeLimit = TimeSpan.FromMilliseconds(100);
        var loop = Loop(backup);
        await loop.TickAsync(default);
        _now = Local(4, 20);
        var stopped = false;
        _work = async ct => { try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { stopped = true; } };
        await loop.TickAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stopped);

        backup.Schedule.ExecutionTimeLimit = TimeSpan.FromHours(72);
        stopped = false;
        _now = Local(5, 20);
        _work = async ct =>
        {
            _battery = true;
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { stopped = true; }
        };
        await loop.TickAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stopped);

        // StopOnBattery off: the run finishes.
        backup.Schedule.StopOnBattery = false; backup.Schedule.SkipOnBattery = false;
        stopped = false;
        _now = Local(6, 20);
        _work = async ct => { try { await Task.Delay(200, ct); } catch (OperationCanceledException) { stopped = true; } };
        await loop.TickAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stopped);
    }

    [Fact]
    public async Task An_unreadable_state_file_starts_from_the_next_times_without_running()
    {
        File.WriteAllText(_state, "not json");
        var loop = Loop(Daily());
        _now = Local(4, 21);
        await loop.TickAsync(default);
        Assert.Empty(_runs);
        _now = Local(5, 20);
        await loop.TickAsync(default);
        Assert.Single(_runs);
    }

    [Fact]
    public async Task Wake_ends_the_idle_wait_after_a_settings_change()
    {
        var loop = Loop(Daily());
        using var stop = new CancellationTokenSource();
        var running = loop.RunAsync(stop.Token);
        await Task.Delay(100);
        _blocked = "settings changed";
        _now = Local(4, 20);
        loop.Wake();
        await Task.Delay(300);
        Assert.Empty(_runs); // woke, saw the block, did not run
        _blocked = null;
        loop.Wake();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_runs.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Single(_runs);
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Start_at_sign_in_is_added_kept_for_another_copy_and_taken_over_only_when_asked()
    {
        var entry = new RecordingSignInStart();
        var exe = Path.Combine(Path.GetTempPath(), "Portable", "Backspace.exe");
        var other = typeof(BackupSchedulerTests).Assembly.Location; // an existing file: another copy

        SignInStart.Apply(entry, wanted: true, exe, takeOver: false, mayAdd: false);
        Assert.Null(entry.Executable); // development builds never add it on their own
        SignInStart.Apply(entry, wanted: true, exe, takeOver: false);
        Assert.Equal(exe, entry.Executable);

        entry.Executable = other;
        SignInStart.Apply(entry, wanted: true, exe, takeOver: false);
        Assert.Equal(other, entry.Executable); // another existing copy keeps it
        SignInStart.Apply(entry, wanted: false, exe, takeOver: false);
        Assert.Equal(other, entry.Executable); // turning it off here never removes another copy's
        SignInStart.Apply(entry, wanted: true, exe, takeOver: true);
        Assert.Equal(exe, entry.Executable); // turned on in this copy

        entry.Executable = Path.Combine(Path.GetTempPath(), "Moved away", "Backspace.exe");
        SignInStart.Apply(entry, wanted: true, exe, takeOver: false);
        Assert.Equal(exe, entry.Executable); // the copy it named is gone

        SignInStart.Apply(entry, wanted: false, exe, takeOver: false);
        Assert.Null(entry.Executable);
        Assert.True(SignInStart.StartsThisCopy(new RecordingSignInStart { Executable = exe }, exe));
    }

    /// <summary>U071, U171: the service reaches a share only by its network
    /// path with a saved user name and password. It runs every backup, so a
    /// share without them is no longer left to the tray app: Settings names it,
    /// and Run now opens the destination to enter them.</summary>
    [Theory]
    [InlineData(@"\\nas\backups\pc", "backup", "dest:x:netpw", true)]
    [InlineData(@"\\nas\backups", "backup", "dest:x:netpw", true)]
    [InlineData(@"\\nas\backups\pc", null, "dest:x:netpw", false)]
    [InlineData(@"\\nas\backups\pc", "backup", null, false)]
    [InlineData(@"Z:\pc", "backup", "dest:x:netpw", false)]
    [InlineData(@"\\nas", "backup", "dest:x:netpw", false)]
    public void A_network_share_the_service_cannot_sign_in_to_is_named_up_front(string path, string? user, string? passwordRef, bool reachable)
    {
        var share = new Destination { Name = "NAS", Kind = DestinationKind.NetworkShare, PathOrSubpath = path, NetworkUsername = user, NetworkPasswordRef = passwordRef };
        Assert.Equal(reachable, NetworkSharePath.ServiceCanReach(share));
        var backup = new Backup { Name = "b" };
        backup.Targets.Add(new BackupTarget { DestinationId = share.Id });
        var config = new AppConfig { Backups = [backup], Destinations = [share] };
        var notice = NetworkSharePath.ServiceSignInNotice(config);
        if (reachable) Assert.Equal("", notice);
        else Assert.Equal("The background service runs every backup and signs in to network shares with their own user name and password. Edit «NAS» in Destinations and enter them, or backups to it fail.", notice);
        // A share no backup uses is not mentioned.
        backup.Targets.Clear();
        Assert.Equal("", NetworkSharePath.ServiceSignInNotice(config));
    }

    /// <summary>U185 (owner, 12 November 2026: "make it event based"): with a
    /// day as its longest sleep, the schedule sleeps exactly until its next
    /// due time, a day while a plan waits for its conditions, and a wake-up
    /// (power, network, settings) makes it look again at once.</summary>
    [Fact]
    public async Task The_schedule_sleeps_until_it_is_due_or_woken()
    {
        var backup = Daily();
        var loop = new ScheduleLoop(new(_state,
            () => SchedulePlan.From(new AppConfig { Backups = [backup] }),
            async (plan, ct) => { _runs.Add((plan.Backup.Name, plan.Kind, _now)); await _work(ct); return true; },
            plan => _blocked ?? ScheduleConditions.Blocked(plan, [], () => _battery, () => !_offline),
            () => _battery, () => _now, TimeSpan.FromDays(1), TimeSpan.FromMilliseconds(20)));
        // 19:00, due at 20:00: it sleeps one hour, not a minute.
        Assert.Equal(TimeSpan.FromHours(1), await loop.TickAsync(default));
        // Due but waiting for its conditions: it sleeps until woken (or a day).
        _now = Local(4, 20, 0);
        _blocked = "the computer is on battery";
        Assert.Equal(TimeSpan.FromDays(1), await loop.TickAsync(default));
        Assert.Empty(_runs);

        // The loop itself: a wake-up makes it look again at once.
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = loop.RunAsync(stop.Token);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Empty(_runs);
        _blocked = null; // on AC power again
        loop.Wake();
        for (var i = 0; i < 100 && _runs.Count == 0; i++) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Single(_runs);
        stop.Cancel();
        await running;
    }

#if WINDOWS
    /// <summary>U185: the background service reads the window's settings
    /// file, so a change there (or to the Quit state) wakes its schedule.</summary>
    [Fact]
    public async Task A_settings_change_wakes_the_schedule()
    {
        using var ws = new TempWorkspace("schedule-signals");
        var folder = ws.Sub("Backspace.config");
        var woken = 0;
        using var watch = Backspace.Services.Platform.Windows.ScheduleSignals.WatchSettings(folder, () => Interlocked.Increment(ref woken))!;
        File.WriteAllText(Path.Combine(folder, "unrelated.txt"), "x");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref woken));
        File.WriteAllText(Path.Combine(folder, "config.json"), "{}");
        for (var i = 0; i < 100 && Volatile.Read(ref woken) == 0; i++) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(Volatile.Read(ref woken) > 0);
        var before = Volatile.Read(ref woken);
        File.WriteAllText(Path.Combine(folder, "task-pause-ABC.json"), "{}");
        for (var i = 0; i < 100 && Volatile.Read(ref woken) == before; i++) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(Volatile.Read(ref woken) > before);

        // Windows accepts the power notifications (AC/battery and waking
        // from sleep), with callbacks that need no window.
        Backspace.Services.Platform.Windows.ScheduleSignals.Start(folder);
        Assert.Equal(((uint)0, (uint)0), Backspace.Services.Platform.Windows.ScheduleSignals.PowerRegistration);
    }
#endif
}
