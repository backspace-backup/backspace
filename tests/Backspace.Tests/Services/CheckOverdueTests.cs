using System;
using System.Collections.Generic;
using System.Linq;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U174 (9 November 2026): a monthly check (and its test restore)
/// that found local_dropbox's drive unplugged runs right after the next
/// backup that reaches the drive, once; nothing else changes.</summary>
public sealed class CheckOverdueTests
{
    private static readonly DateTime T0 = new(2026, 12, 1, 7, 0, 0, DateTimeKind.Utc);
    private readonly Destination _drive = new() { Name = "My Passport", Kind = DestinationKind.ExternalDrive, ExpectedDriveLetter = "M", ExpectedVolumeLabel = "My Passport" };
    private readonly Destination _cloud = new() { Name = "Google Drive", Kind = DestinationKind.GoogleDrive };

    private Backup Backup()
    {
        var backup = new Backup { Name = "local_dropbox", Checks = BackupCheck.FromLegacy(new BackupSchedule { Frequency = ScheduleFrequency.Monthly }, false, true, null) };
        backup.Targets.Add(new BackupTarget { DestinationId = _drive.Id, StorageName = "default" });
        backup.Targets.Add(new BackupTarget { DestinationId = _cloud.Id, StorageName = "default" });
        return backup;
    }

    private static RunRecord Run(Backup backup, RunKind kind, DateTime at, BackupRunStatus status, params CellOutcome[] cells)
    {
        var run = new RunRecord { BackupId = backup.Id, Kind = kind, StartedUtc = at, EndedUtc = at.AddMinutes(5), Status = status };
        run.Cells.AddRange(cells);
        return run;
    }

    private static CellOutcome Cell(Destination d, BackupRunStatus status, bool unavailable = false) => new()
    {
        DestinationName = d.Name, StorageName = "default", Status = status, DestinationUnavailable = unavailable,
        Summary = unavailable ? "Drive M: is not connected" : "",
    };

    private IEnumerable<Destination> Destinations => new[] { _drive, _cloud };

    [Fact]
    public void A_check_that_missed_the_unplugged_drive_runs_after_the_next_backup_that_reaches_it()
    {
        var backup = Backup();
        var history = new List<RunRecord>
        {
            Run(backup, RunKind.Check, T0, BackupRunStatus.Warning, Cell(_drive, BackupRunStatus.Skipped, unavailable: true), Cell(_cloud, BackupRunStatus.Success)),
        };
        // A check recorded before checks were a list counts for both entries.
        var waiting = CheckOverdue.For(backup, history, Destinations);
        Assert.Equal(2, waiting.Count);
        Assert.All(waiting, w => Assert.Equal(_drive.Id, w.Target.DestinationId));

        // The daily backup with the drive still unplugged: still waiting.
        var unplugged = Run(backup, RunKind.Backup, T0.AddHours(14), BackupRunStatus.Warning, Cell(_drive, BackupRunStatus.Skipped), Cell(_cloud, BackupRunStatus.Success));
        Assert.Empty(CheckOverdue.DueAfter(backup, unplugged, history, Destinations));
        // A failed backup: still waiting, and still shown as overdue.
        var failed = Run(backup, RunKind.Backup, T0.AddDays(1), BackupRunStatus.Failed, Cell(_drive, BackupRunStatus.Failed), Cell(_cloud, BackupRunStatus.Success));
        Assert.Empty(CheckOverdue.DueAfter(backup, failed, history, Destinations));
        history.AddRange([unplugged, failed]);
        Assert.Equal(2, CheckOverdue.For(backup, history, Destinations).Count);

        // The drive is back and the backup reaches it: the check is due, for that drive only.
        var plugged = Run(backup, RunKind.Backup, T0.AddDays(9), BackupRunStatus.Success, Cell(_drive, BackupRunStatus.Success), Cell(_cloud, BackupRunStatus.Success));
        var due = CheckOverdue.DueAfter(backup, plugged, history, Destinations);
        Assert.Equal(2, due.Count);
        Assert.All(due, w => Assert.Equal(_drive.Id, w.Target.DestinationId));

        // Once it ran there, whatever its result, nothing is due any more.
        history.Add(plugged);
        history.Add(Run(backup, RunKind.Check, T0.AddDays(9).AddMinutes(10), BackupRunStatus.Failed, Cell(_drive, BackupRunStatus.Failed)));
        Assert.Empty(CheckOverdue.For(backup, history, Destinations));
        Assert.Empty(CheckOverdue.DueAfter(backup, plugged, history, Destinations));
    }

    [Fact]
    public void A_check_that_failed_for_another_reason_or_ran_never_adds_a_run()
    {
        var backup = Backup();
        var plugged = Run(backup, RunKind.Backup, T0.AddDays(2), BackupRunStatus.Success, Cell(_drive, BackupRunStatus.Success), Cell(_cloud, BackupRunStatus.Success));
        // Not missed at all.
        var ran = new List<RunRecord> { Run(backup, RunKind.Check, T0, BackupRunStatus.Success, Cell(_drive, BackupRunStatus.Success), Cell(_cloud, BackupRunStatus.Success)) };
        Assert.Empty(CheckOverdue.DueAfter(backup, plugged, ran, Destinations));
        // Failed on the drive for another reason (the repository is damaged).
        var broken = new List<RunRecord> { Run(backup, RunKind.Check, T0, BackupRunStatus.Failed, Cell(_drive, BackupRunStatus.Failed), Cell(_cloud, BackupRunStatus.Success)) };
        Assert.Empty(CheckOverdue.DueAfter(backup, plugged, broken, Destinations));
        // Skipped for another reason (stopped, metered network).
        var stopped = new List<RunRecord> { Run(backup, RunKind.Check, T0, BackupRunStatus.Skipped, Cell(_drive, BackupRunStatus.Skipped), Cell(_cloud, BackupRunStatus.Skipped)) };
        Assert.Empty(CheckOverdue.DueAfter(backup, plugged, stopped, Destinations));
        // No check schedule any more: nothing waits.
        var missed = new List<RunRecord> { Run(backup, RunKind.Check, T0, BackupRunStatus.Skipped, Cell(_drive, BackupRunStatus.Skipped, unavailable: true)) };
        Assert.Equal(2, CheckOverdue.DueAfter(backup, plugged, missed, Destinations).Count);
        foreach (var check in backup.Checks) check.Schedule = null;
        Assert.Empty(CheckOverdue.DueAfter(backup, plugged, missed, Destinations));
        Assert.Empty(CheckOverdue.For(backup, missed, Destinations));
    }

    [Fact]
    public void Each_check_entry_waits_on_its_own()
    {
        // U179: the structure check ran on the drive after it came back; the
        // test restore did not. Only the test restore still waits.
        var backup = Backup();
        var structure = backup.Checks.Single(c => c.Kind == CheckKind.Structure);
        var restore = backup.Checks.Single(c => c.Kind == CheckKind.TestRestore);
        var missed = Run(backup, RunKind.Check, T0, BackupRunStatus.Skipped, Cell(_drive, BackupRunStatus.Skipped, unavailable: true));
        missed.CheckId = restore.Id;
        var history = new List<RunRecord> { missed };
        Assert.Equal(restore.Id, Assert.Single(CheckOverdue.For(backup, history, Destinations)).Check.Id);
        var ranStructure = Run(backup, RunKind.Check, T0.AddDays(1), BackupRunStatus.Success, Cell(_drive, BackupRunStatus.Success));
        ranStructure.CheckId = structure.Id;
        history.Add(ranStructure);
        Assert.Equal(restore.Id, Assert.Single(CheckOverdue.For(backup, history, Destinations)).Check.Id);
        var ranRestore = Run(backup, RunKind.Check, T0.AddDays(2), BackupRunStatus.Success, Cell(_drive, BackupRunStatus.Success));
        ranRestore.CheckId = restore.Id;
        history.Add(ranRestore);
        Assert.Empty(CheckOverdue.For(backup, history, Destinations));
    }

    [Fact]
    public void The_card_says_it_waits_for_the_drive_without_alarm_but_a_failure_comes_first()
    {
        var backup = Backup();
        var history = new[] { Run(backup, RunKind.Check, T0, BackupRunStatus.Skipped, Cell(_drive, BackupRunStatus.Skipped, unavailable: true)) };
        var waiting = CheckOverdue.For(backup, history, Destinations);
        var incomplete = new BackupCheckState("Check incomplete · 3d ago", "stale", "", "");
        var shown = CheckOverdue.Shown(incomplete, backup, waiting);
        Assert.Equal("Checks overdue: waits for the drive", shown.Label);
        Assert.NotEqual("fail", shown.Tone);
        Assert.Contains("«My Passport»", shown.Details);
        Assert.Contains("right after the next backup", shown.Details);
        backup.Checks.RemoveAll(c => c.Kind == CheckKind.TestRestore);
        Assert.Equal("Structure check overdue: waits for the drive", CheckOverdue.Shown(incomplete, backup, CheckOverdue.For(backup, history, Destinations)).Label);

        var failure = new BackupCheckState("Check needs attention · 3d ago", "fail", "", "");
        Assert.Same(failure, CheckOverdue.Shown(failure, backup, waiting));
        Assert.Same(incomplete, CheckOverdue.Shown(incomplete, backup, []));
    }
}
