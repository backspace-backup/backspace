using System;
using System.IO;
using System.Linq;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class BackupCheckHistoryTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 20, 0, 0, DateTimeKind.Utc);
    private static Backup Backup() => new() { Id = "history", Enabled = false, Targets = { new() { DestinationId = "a" } } };
    private static RunRecord Check(Backup b, CheckDepth depth, BackupRunStatus status, int minutes = 1) => new()
    {
        BackupId = b.Id, BackupName = b.Name, Kind = depth is CheckDepth.RestoreAllFiles or CheckDepth.RestoreSample ? RunKind.TestRestore : RunKind.Check,
        StartedUtc = Now.AddMinutes(-minutes - 1), EndedUtc = Now.AddMinutes(-minutes), Status = status,
        Checks = { new() { Depth = depth, Status = status, EndedUtc = Now.AddMinutes(-minutes), TargetKey = "a/default", Summary = "Recorded result" } },
    };

    [Fact] public void Structure_success_is_green_without_manual_test_and_does_not_claim_data_was_read()
    {
        var b = Backup(); var entries = BackupCheckHistory.Entries(b, new[] { Check(b, CheckDepth.RepositoryStructure, BackupRunStatus.Success) });
        var state = BackupCheckHistory.State(b, entries, Now);
        Assert.Equal("ok", state.Tone); Assert.Contains("1m ago", state.Label);
        Assert.Contains("file contents not read", state.Scope); Assert.Null(b.LastVerifyUtc);
    }
    [Fact] public void New_structure_success_cannot_hide_old_restore_or_saved_data_failure()
    {
        var b = Backup();
        foreach (var failedDepth in new[] { CheckDepth.RestoreAllFiles, CheckDepth.SavedDataSample })
        {
            var entries = BackupCheckHistory.Entries(b, new[] { Check(b, CheckDepth.RepositoryStructure, BackupRunStatus.Success), Check(b, failedDepth, BackupRunStatus.Failed, 10) });
            Assert.Equal("fail", BackupCheckHistory.State(b, entries, Now).Tone);
        }
    }
    [Fact] public void Sample_success_does_not_clear_full_restore_failure_but_full_success_can_clear_sample_failure()
    {
        var b = Backup();
        var fullFailure = BackupCheckHistory.Entries(b, new[] { Check(b, CheckDepth.RestoreSample, BackupRunStatus.Success), Check(b, CheckDepth.RestoreAllFiles, BackupRunStatus.Failed, 5) });
        Assert.Equal("fail", BackupCheckHistory.State(b, fullFailure, Now).Tone);
        var fullSuccess = BackupCheckHistory.Entries(b, new[] { Check(b, CheckDepth.RestoreAllFiles, BackupRunStatus.Success), Check(b, CheckDepth.RestoreSample, BackupRunStatus.Failed, 5) });
        Assert.Equal("ok", BackupCheckHistory.State(b, fullSuccess, Now).Tone);
    }
    [Fact] public void Missed_scheduled_check_and_unchecked_destination_are_not_green()
    {
        var b = Backup(); b.Enabled = true; b.CheckSchedule = new() { Frequency = ScheduleFrequency.Hourly, HourlyInterval = 1, TimeOfDay = TimeSpan.Zero };
        var old = BackupCheckHistory.Entries(b, new[] { Check(b, CheckDepth.RepositoryStructure, BackupRunStatus.Success, 180) });
        Assert.Equal("stale", BackupCheckHistory.State(b, old, Now).Tone);
        b.Targets.Add(new() { DestinationId = "b" });
        var run = Check(b, CheckDepth.RepositoryStructure, BackupRunStatus.Success); run.Kind = RunKind.Backup;
        var incomplete = BackupCheckHistory.Entries(b, new[] { run });
        Assert.NotEqual(BackupRunStatus.Success, incomplete[0].Status);
    }
    [Fact] public void Timeline_survives_log_retention_and_rename_without_mixing_backup_ids()
    {
        using var ws = new TempWorkspace();
        var logs = new LogStore { RetainLastNRuns = 1 }; var b = Backup(); b.Name = "Before rename";
        var failed = Check(b, CheckDepth.RestoreAllFiles, BackupRunStatus.Failed, 10); logs.AppendRun(failed);
        var recent = Check(b, CheckDepth.RepositoryStructure, BackupRunStatus.Success); logs.AppendRun(recent);
        Assert.Single(logs.LoadHistory(b.Name));
        b.Name = "After rename"; logs.AppendRun(Check(b, CheckDepth.RepositoryStructure, BackupRunStatus.Success, 0));
        logs.AppendRun(new() { BackupId = "unrelated", BackupName = b.Name, EndedUtc = Now, Status = BackupRunStatus.Success });
        var timeline = new LogStore().LoadTimelineByBackupId(b.Id);
        Assert.Equal(3, timeline.Count); Assert.Contains(timeline, r => r.Id == failed.Id);
        Assert.All(timeline, r => Assert.Equal(b.Id, r.BackupId));
        Assert.Equal("fail", BackupCheckHistory.State(b, BackupCheckHistory.Entries(b, timeline), Now).Tone);
    }
    [Fact] public void History_filters_are_independent_and_newest_first()
    {
        var b = Backup(); var check = Check(b, CheckDepth.RepositoryStructure, BackupRunStatus.Success);
        var run = new RunRecord { BackupId = b.Id, Kind = RunKind.Backup, StartedUtc = Now.AddHours(-2), EndedUtc = Now.AddHours(-1), Status = BackupRunStatus.Skipped };
        var vm = new BackupHistoryViewModel(b, new[] { run, check }, false);
        Assert.Equal(run.Id, Assert.Single(vm.Rows).RunId);
        vm.SelectedFilter = "Checks"; Assert.Equal(check.Id, Assert.Single(vm.Rows).RunId);
        vm.SelectedFilter = "All activity"; Assert.Equal(new[] { check.Id, run.Id }, vm.Rows.Select(r => r.RunId));
    }
    [Fact] public void Cancelled_retry_does_not_clear_failure_and_legacy_scope_is_not_invented()
    {
        var b = Backup();
        var entries = BackupCheckHistory.Entries(b, new[] { Check(b, CheckDepth.RestoreAllFiles, BackupRunStatus.Skipped), Check(b, CheckDepth.RestoreAllFiles, BackupRunStatus.Failed, 10) });
        Assert.Equal("fail", BackupCheckHistory.State(b, entries, Now).Tone);
        var legacy = new RunRecord { BackupId = b.Id, Kind = RunKind.TestRestore, EndedUtc = Now, Status = BackupRunStatus.Success };
        Assert.Equal(CheckDepth.RestoreUnspecified, Assert.Single(BackupCheckHistory.Entries(b, new[] { legacy })).Depth);
    }
    [Fact] public void Updating_a_run_keeps_one_result_and_corrupt_timeline_is_preserved()
    {
        using var ws = new TempWorkspace(); var logs = new LogStore(); var b = Backup();
        var run = Check(b, CheckDepth.RestoreAllFiles, BackupRunStatus.Success); logs.AppendRun(run);
        run.Status = BackupRunStatus.Skipped; logs.AppendRun(run);
        Assert.Equal(BackupRunStatus.Skipped, Assert.Single(logs.LoadHistoryByBackupId(b.Id)).Status);
        Assert.Single(logs.LoadTimelineByBackupId(b.Id));
        var timeline = Path.Combine(Path.GetDirectoryName(logs.GetHistoryFile(b.Name))!, "timeline.json");
        File.WriteAllText(timeline, "broken history");
        Assert.Throws<System.Text.Json.JsonException>(() => logs.LoadTimelineByBackupId(b.Id));
        Assert.Throws<System.Text.Json.JsonException>(() => logs.AppendRun(Check(b, CheckDepth.RepositoryStructure, BackupRunStatus.Success)));
        Assert.Equal("broken history", File.ReadAllText(timeline));
        Assert.Single(logs.LoadHistoryByBackupId(b.Id));
    }
    [Theory] [InlineData(-1)] [InlineData(1)]
    public void Changed_original_date_skips_comparison_in_either_direction(int days)
    {
        using var ws = new TempWorkspace(); var source = ws.Sub("source"); var recovered = ws.Sub("recovered");
        File.WriteAllText(Path.Combine(source, "f"), "live"); File.WriteAllText(Path.Combine(recovered, "f"), "saved");
        File.SetLastWriteTimeUtc(Path.Combine(source, "f"), Now.AddDays(days));
        var result = BackupVerifier.VerifyOneFile(new("f", 5, Now), source, recovered, new());
        Assert.Equal(FileVerifyStatus.OkNoCompare, result.Status);
    }
    [Fact] public void Locked_timeline_defers_retention_without_losing_results()
    {
        using var ws = new TempWorkspace(); var logs = new LogStore { RetainLastNRuns = 1 }; var b = Backup();
        var first = Check(b, CheckDepth.RestoreAllFiles, BackupRunStatus.Failed, 20); logs.AppendRun(first);
        var timeline = Path.Combine(Path.GetDirectoryName(logs.GetHistoryFile(b.Name))!, "timeline.json");
        using (var held = new FileStream(timeline, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            logs.AppendRun(Check(b, CheckDepth.RepositoryStructure, BackupRunStatus.Success, 10));
            Assert.Equal(2, logs.LoadHistoryByBackupId(b.Id).Count);
        }
        logs.AppendRun(Check(b, CheckDepth.RepositoryStructure, BackupRunStatus.Success, 1));
        Assert.Single(logs.LoadHistoryByBackupId(b.Id));
        Assert.Equal(3, logs.LoadTimelineByBackupId(b.Id).Count);
        Assert.Equal("fail", BackupCheckHistory.State(b, BackupCheckHistory.Entries(b, logs.LoadTimelineByBackupId(b.Id)), Now).Tone);
    }
}
