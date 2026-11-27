using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U266 (owner, 27 November 2026: "If after running a backup, a check is
/// overdue, it'd make sense to queue it after the backup so it isn't delayed
/// further") and U268 ("if I run a manual check, a scheduled check within X
/// time should be ignored (no point)"); the proposal approved with "OK for
/// 1-4". A check interrupted, failed or overdue runs once after its backup's
/// next successful run; a scheduled check is passed over when the same check
/// passed less than half its period before.
/// </summary>
public sealed class CheckTimingTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "backspace-checks-" + Guid.NewGuid().ToString("N") + ".json");
    public void Dispose() { try { File.Delete(_state); } catch { } }

    private static DateTime Local(int day, int hour, int minute = 0)
        => new DateTime(2026, 11, day, hour, minute, 0, DateTimeKind.Local).ToUniversalTime();

    // Wednesday 25 November 2026; the check runs on Sundays at 21:00.
    private static (Backup Backup, BackupCheck Check) Setup()
    {
        var check = new BackupCheck
        {
            Kind = CheckKind.DataSample,
            Schedule = new BackupSchedule { Frequency = ScheduleFrequency.Weekly, WeeklyDaysBitmask = 1 << (int)DayOfWeek.Sunday, TimeOfDay = TimeSpan.FromHours(21) },
        };
        var backup = new Backup
        {
            Name = "local_c", CreatedUtc = Local(1, 9),
            Schedule = new BackupSchedule { Frequency = ScheduleFrequency.Daily, TimeOfDay = TimeSpan.FromHours(20), RequireNetwork = false },
            Checks = [check],
        };
        return (backup, check);
    }

    private static RunRecord BackupRun(Backup b, DateTime ended, BackupRunStatus status = BackupRunStatus.Success)
        => new() { BackupId = b.Id, Kind = RunKind.Backup, StartedUtc = ended.AddMinutes(-10), EndedUtc = ended, Status = status };

    private static RunRecord CheckRun(Backup b, BackupCheck c, DateTime ended, BackupRunStatus status = BackupRunStatus.Success, bool stoppedByYou = false, CheckKind? kind = null)
        => new() { BackupId = b.Id, Kind = RunKind.Check, CheckId = c.Id, CheckKind = kind ?? c.Kind, StartedUtc = ended.AddMinutes(-5), EndedUtc = ended, Status = status, StoppedByYou = stoppedByYou };

    [Fact]
    public void A_check_that_failed_runs_after_the_next_successful_backup_once()
    {
        var (b, c) = Setup();
        var now = Local(25, 20, 30);
        // local_c on 27 November: its check of 17 November failed; then it backed up.
        var history = new List<RunRecord> { CheckRun(b, c, Local(17, 7), BackupRunStatus.Failed), BackupRun(b, Local(25, 20, 10)) };
        Assert.Equal("its last run did not pass", CheckTiming.FollowUp(b, c, history, now)?.Why);
        Assert.Equal(Local(25, 20, 10), CheckTiming.FollowUp(b, c, history, now)?.BackupUtc);
        // Tried after that backup, whatever it found: not again until the next backup.
        history.Add(CheckRun(b, c, Local(25, 20, 40), BackupRunStatus.Failed));
        Assert.Null(CheckTiming.FollowUp(b, c, history, Local(25, 21)));
        history.Add(BackupRun(b, Local(26, 20, 10)));
        Assert.NotNull(CheckTiming.FollowUp(b, c, history, Local(26, 20, 30)));
    }

    [Fact]
    public void A_check_stopped_or_skipped_runs_after_the_next_backup()
    {
        var (b, c) = Setup();
        // local_d on 25 November: its checks were stopped during the cleanup trouble.
        var stopped = new List<RunRecord> { CheckRun(b, c, Local(22, 21, 30)), CheckRun(b, c, Local(25, 9), BackupRunStatus.Skipped, stoppedByYou: true), BackupRun(b, Local(25, 20, 10)) };
        Assert.Equal("its last run did not finish", CheckTiming.FollowUp(b, c, stopped, Local(25, 20, 30))?.Why);
        var skipped = new List<RunRecord> { CheckRun(b, c, Local(22, 21, 30)), CheckRun(b, c, Local(23, 9), BackupRunStatus.Skipped), BackupRun(b, Local(25, 20, 10)) };
        Assert.Equal("its last run did not finish", CheckTiming.FollowUp(b, c, skipped, Local(25, 20, 30))?.Why);
    }

    [Fact]
    public void An_overdue_check_runs_after_the_backup_and_one_on_time_does_not()
    {
        var (b, c) = Setup();
        // Passed on Sunday 15 November; Sunday 22 November's run never happened.
        var overdue = new List<RunRecord> { CheckRun(b, c, Local(15, 21, 30)), BackupRun(b, Local(25, 20, 10)) };
        Assert.Equal("it is overdue", CheckTiming.FollowUp(b, c, overdue, Local(25, 20, 30))?.Why);
        // The negative cases: passed on 22 November (next is the 29th), no backup since the check, a failed backup.
        var onTime = new List<RunRecord> { CheckRun(b, c, Local(22, 21, 30)), BackupRun(b, Local(25, 20, 10)) };
        Assert.Null(CheckTiming.FollowUp(b, c, onTime, Local(25, 20, 30)));
        var noBackupSince = new List<RunRecord> { BackupRun(b, Local(14, 20, 10)), CheckRun(b, c, Local(15, 21, 30), BackupRunStatus.Failed) };
        Assert.Null(CheckTiming.FollowUp(b, c, noBackupSince, Local(25, 20, 30)));
        var backupFailed = new List<RunRecord> { CheckRun(b, c, Local(15, 21, 30), BackupRunStatus.Failed), BackupRun(b, Local(25, 20, 10), BackupRunStatus.Failed) };
        Assert.Null(CheckTiming.FollowUp(b, c, backupFailed, Local(25, 20, 30)));
        // Without a schedule, a check is not this rule's.
        c.Schedule = null;
        Assert.Null(CheckTiming.FollowUp(b, c, overdue, Local(25, 20, 30)));
    }

    [Fact]
    public void A_scheduled_check_is_passed_over_after_a_pass_within_half_its_period()
    {
        var (b, c) = Setup();
        var due = Local(29, 21); // Sunday; half a week is 3.5 days
        Assert.Equal(Local(27, 10), CheckTiming.PassedRecently(b, c, [CheckRun(b, c, Local(27, 10))], due));
        // A deeper check answers for it too: a full read is also a data sample.
        Assert.NotNull(CheckTiming.PassedRecently(b, c, [CheckRun(b, c, Local(27, 10), kind: CheckKind.FullDataRead)], due));
        // The negative cases: 4 days before, failed, stopped, or only a shallower check.
        Assert.Null(CheckTiming.PassedRecently(b, c, [CheckRun(b, c, Local(25, 20))], due));
        Assert.Null(CheckTiming.PassedRecently(b, c, [CheckRun(b, c, Local(28, 10), BackupRunStatus.Failed)], due));
        Assert.Null(CheckTiming.PassedRecently(b, c, [CheckRun(b, c, Local(28, 10), BackupRunStatus.Skipped, stoppedByYou: true)], due));
        Assert.Null(CheckTiming.PassedRecently(b, c, [CheckRun(b, c, Local(28, 10), kind: CheckKind.Structure)], due));
    }

    // ---- through the schedule ----

    private DateTime _now;
    private readonly List<(SchedulePlan Plan, DateTime At)> _runs = [];
    private readonly List<RunRecord> _history = [];

    private ScheduleLoop Loop(Backup backup) => new(new(_state,
        () => SchedulePlan.From(new AppConfig { Backups = [backup] }),
        (plan, _) => { _runs.Add((plan, _now)); return Task.FromResult(true); },
        _ => null, () => false, () => _now, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(20),
        History: _ => _history));

    private ScheduleState.Entry CheckEntry(SchedulePlan plan) => ScheduleState.Load(_state).Entries[plan.Key];

    [Fact]
    public async Task The_schedule_runs_a_failed_check_after_the_backup_once_and_keeps_its_sunday_time()
    {
        var (b, c) = Setup();
        var loop = Loop(b);
        _now = Local(25, 19);
        await loop.TickAsync(default); // first sight: backup at 20:00, check on Sunday
        var checkPlan = SchedulePlan.From(new AppConfig { Backups = [b] }).Single(p => p.Kind == ScheduledTaskKind.Check);
        var sunday = CheckEntry(checkPlan).NextUtc;
        Assert.Equal(Local(29, 21), sunday);
        _history.Add(CheckRun(b, c, Local(17, 7), BackupRunStatus.Failed));
        await loop.TickAsync(default);
        Assert.Empty(_runs); // no backup since: nothing to follow

        // You ran the backup: the check follows it.
        _now = Local(25, 19, 30);
        _history.Add(BackupRun(b, Local(25, 19, 20)));
        await loop.TickAsync(default);
        var run = Assert.Single(_runs);
        Assert.Equal(ScheduledTaskKind.Check, run.Plan.Kind);
        Assert.Equal("its last run did not pass", run.Plan.FollowUp);
        Assert.Equal(sunday, CheckEntry(checkPlan).NextUtc); // its Sunday time stays

        // Once per backup, even if the try recorded nothing.
        _now = Local(25, 19, 40);
        await loop.TickAsync(default);
        Assert.Single(_runs);
    }

    [Fact]
    public async Task The_schedule_passes_over_a_sunday_check_after_one_passed_on_friday_but_runs_it_after_a_tuesday_pass()
    {
        var (b, c) = Setup();
        b.Schedule.Frequency = ScheduleFrequency.Weekly; b.Schedule.WeeklyDaysBitmask = 1 << (int)DayOfWeek.Monday; // keep backups out of the way
        var loop = Loop(b);
        _now = Local(25, 19);
        await loop.TickAsync(default);
        var checkPlan = SchedulePlan.From(new AppConfig { Backups = [b] }).Single(p => p.Kind == ScheduledTaskKind.Check);
        // You ran the check on Friday: Sunday's is passed over, the next is a week later.
        _history.Add(CheckRun(b, c, Local(27, 10)));
        _now = Local(29, 21);
        await loop.TickAsync(default);
        Assert.DoesNotContain(_runs, r => r.Plan.Kind == ScheduledTaskKind.Check);
        Assert.Equal(Local(29, 21).AddDays(7), CheckEntry(checkPlan).NextUtc);
        // A pass on Tuesday 1 December is more than half a week before Sunday 6 December: it runs.
        _history.Add(CheckRun(b, c, new DateTime(2026, 12, 1, 10, 0, 0, DateTimeKind.Local).ToUniversalTime()));
        _now = new DateTime(2026, 12, 6, 21, 0, 0, DateTimeKind.Local).ToUniversalTime();
        await loop.TickAsync(default); // Monday's missed backup first, one at a time
        await loop.TickAsync(default);
        Assert.Contains(_runs, r => r.Plan.Kind == ScheduledTaskKind.Check && r.Plan.FollowUp is null);
    }
}
