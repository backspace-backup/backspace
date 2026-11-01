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
    private bool _battery, _offline, _admit = true;
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
        () => _battery, () => _now, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(20)));

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

    /// <summary>U058: the service cannot reach external drives or network
    /// shares, so those backups stay with the tray app; every other backup
    /// runs in the service. One backup to a USB drive used to block turning
    /// administrator access on, and every later settings change, for all.</summary>
    [Theory]
    [InlineData(DestinationKind.LocalFolder, true)]
    [InlineData(DestinationKind.DropboxAppScoped, true)]
    [InlineData(DestinationKind.S3Compatible, true)]
    [InlineData(DestinationKind.ExternalDrive, true)]
    [InlineData(DestinationKind.NetworkShare, false)]
    public void Only_backups_the_service_can_reach_run_in_the_service(DestinationKind kind, bool inService)
    {
        var cloud = new Destination { Kind = DestinationKind.DropboxAppScoped };
        var other = new Destination { Kind = kind };
        var backup = new Backup { Name = "b" };
        backup.Targets.Add(new BackupTarget { DestinationId = cloud.Id });
        backup.Targets.Add(new BackupTarget { DestinationId = other.Id });
        Assert.Equal(inService, SchedulePlan.ServiceCanRun(backup, new[] { cloud, other }));
    }
}
