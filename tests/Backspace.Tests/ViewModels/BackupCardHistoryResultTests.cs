using System;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.ViewModels;

public sealed class BackupCardHistoryResultTests
{
    /// <summary>U265: with the window left open, the tile turns orange once a
    /// scheduled time is more than an hour past without a run, and the banner
    /// hears of it; a run starting brings it back.</summary>
    [AvaloniaFact]
    public void A_scheduled_time_passing_turns_the_open_card_orange()
    {
        var now = DateTime.UtcNow;
        var backup = new Backup { LastRunStatus = BackupRunStatus.Success, Schedule = TestSchedules.DueInAnHour(), Enabled = true,
            LastRunStartUtc = now.AddMinutes(-12), LastRunEndUtc = now.AddMinutes(-10), LastSuccessUtc = now.AddMinutes(-10), LastRunSummary = "Backup completed" };
        using var card = new BackupCardViewModel(backup, false, false);
        Assert.Equal("ok", card.StatusClass);
        var changes = new System.Collections.Generic.List<string?>();
        card.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        card.UtcNow = () => now.AddHours(1).AddMinutes(30);
        card.RefreshTimes();
        Assert.False(card.MissedSchedule, "Half an hour after its time it may still be starting.");
        card.UtcNow = () => now.AddHours(2).AddMinutes(30);
        card.RefreshTimes();
        Assert.True(card.MissedSchedule);
        Assert.Equal("warn", card.StatusClass);
        Assert.Contains(nameof(BackupCardViewModel.MissedSchedule), changes);
        Assert.Contains("did not happen", card.LastRunDetail);
        card.SetRunning();
        Assert.False(card.MissedSchedule);
    }

    [AvaloniaFact]
    public void An_unfinished_record_cannot_end_the_live_card()
    {
        var backup = new Backup();
        using var card = new BackupCardViewModel(backup, false, false);
        card.SetRunning();
        card.Apply(new RunRecord { BackupId = backup.Id, Status = BackupRunStatus.Running });
        Assert.True(card.IsRunning);
        Assert.Equal(BackupRunStatus.Running, card.DisplayedRunStatus);
    }

    [AvaloniaTheory]
    [InlineData("older")]
    [InlineData("other-backup")]
    [InlineData("unfinished")]
    [InlineData("maintenance")]
    [InlineData("invalid-time")]
    public void Unrelated_or_invalid_history_does_not_replace_a_newer_saved_result(string variant)
    {
        var now = DateTime.UtcNow;
        var backup = new Backup { LastRunStatus = BackupRunStatus.Failed, LastRunStartUtc = now.AddMinutes(-2), LastRunEndUtc = now.AddMinutes(-1), LastRunSummary = "Current failure" };
        using var card = new BackupCardViewModel(backup, false, false);
        var record = new RunRecord { BackupId = backup.Id, StartedUtc = now, EndedUtc = now.AddSeconds(1), Status = BackupRunStatus.Success, Summary = "Must not replace failure" };
        switch (variant)
        {
            case "older": record.StartedUtc = now.AddDays(-1); record.EndedUtc = now.AddDays(-1).AddMinutes(1); break;
            case "other-backup": record.BackupId = "another-backup"; break;
            case "unfinished": record.EndedUtc = null; break;
            case "maintenance": record.Kind = RunKind.Prune; break;
            case "invalid-time": record.EndedUtc = now.AddMinutes(-1); break;
        }
        card.ApplyVerifyState(backup, record);
        Assert.Equal(BackupRunStatus.Failed, card.DisplayedRunStatus);
        Assert.Contains("Current failure", card.LastRunLabel);
        Assert.Equal(BackupRunStatus.Failed, backup.LastRunStatus);
    }
}

public sealed class BackupCardClockLabelTests
{
    /// <summary>U136 (4 November 2026): the card kept "1h ago" for a backup
    /// that had finished three hours before, until another tab was opened,
    /// while its health line said "3h ago". The minute timer now redoes it.</summary>
    [AvaloniaFact]
    public void The_last_backup_age_follows_the_clock_but_leaves_a_running_label_alone()
    {
        var now = DateTime.UtcNow;
        var backup = new Backup { LastRunStatus = BackupRunStatus.Success, Schedule = TestSchedules.DueInAnHour(), LastRunStartUtc = now.AddHours(-3), LastRunEndUtc = now.AddMinutes(-90), LastRunSummary = "Backup result: STATUS_SUCCESS." };
        using var card = new BackupCardViewModel(backup, false, false);
        Assert.StartsWith("1h ago · ", card.LastRunLabel);

        // Ninety minutes later: same backup, older.
        var field = typeof(BackupCardViewModel).GetField("_lastRun", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var last = ((string Summary, DateTime When, BackupRunStatus Status)?)field.GetValue(card);
        field.SetValue(card, ((string, DateTime, BackupRunStatus)?)(last!.Value.Summary, now.AddMinutes(-180), last.Value.Status));
        card.RefreshTimes();
        Assert.StartsWith("3h ago · ", card.LastRunLabel);

        card.SetRunning();
        card.RefreshTimes();
        Assert.Equal("Running…", card.LastRunLabel);
    }
}

public sealed class BackupCardStoppedByYouTests
{
    /// <summary>U242 (owner, 13-14 November 2026): a run you stop yourself is
    /// not a warning. The card says "Stopped by you · time" in neutral, the last
    /// completed run under it, and the backup's status stays that run's.</summary>
    [AvaloniaFact]
    public void A_run_you_stopped_is_neutral_and_keeps_the_last_completed_status()
    {
        var now = DateTime.UtcNow;
        var backup = new Backup { LastRunStatus = BackupRunStatus.Success, Schedule = TestSchedules.DueInAnHour(), LastRunStartUtc = now.AddHours(-2), LastRunEndUtc = now.AddHours(-2).AddMinutes(10), LastRunSummary = "Backup result: STATUS_SUCCESS." };
        using var card = new BackupCardViewModel(backup, false, false);
        card.Apply(new RunRecord { BackupId = backup.Id, StartedUtc = now.AddMinutes(-5), EndedUtc = now.AddMinutes(-1),
            Status = BackupRunStatus.Skipped, StoppedByYou = true, Summary = "Stopped by you after 4m" });
        // U242 (15 November 2026): the tile keeps the completed run's green;
        // the first line has the stop icon, the completed run is under it.
        Assert.Equal("ok", card.StatusClass);
        Assert.Equal("stopped", card.RunIconClass);
        Assert.Equal("Stopped by you · 1m ago", card.LastRunLabel);
        Assert.StartsWith("1h ago", card.LastCompletedLabel); // ended 1 h 50m ago
        Assert.Equal("ok", card.LastCompletedClass);
        Assert.Equal(BackupRunStatus.Success, card.DisplayedRunStatus); // what the overall status and tray read

        // The next completed run replaces it.
        card.Apply(new RunRecord { BackupId = backup.Id, StartedUtc = now, EndedUtc = now.AddSeconds(30),
            Status = BackupRunStatus.Success, Summary = "Backup result: STATUS_SUCCESS." });
        Assert.Equal("ok", card.StatusClass);
        Assert.Equal("ok", card.RunIconClass);
        Assert.Equal("", card.LastCompletedLabel);
    }

    [AvaloniaFact]
    public void A_run_stopped_by_backspace_itself_is_still_a_skipped_run()
    {
        var now = DateTime.UtcNow;
        // Without a quiet period; within one it stays quiet (U269, QuietSkipTests).
        var backup = new Backup { LastRunStatus = BackupRunStatus.Success, Schedule = TestSchedules.DueInAnHour(), LastRunStartUtc = now.AddHours(-2), LastRunEndUtc = now.AddHours(-2).AddMinutes(10), LastRunSummary = "Backup result: STATUS_SUCCESS.",
            SkipNotificationGraceDays = 0 };
        using var card = new BackupCardViewModel(backup, false, false);
        card.Apply(new RunRecord { BackupId = backup.Id, StartedUtc = now.AddMinutes(-5), EndedUtc = now.AddMinutes(-1),
            Status = BackupRunStatus.Skipped, Summary = "Network became metered (mobile / tethered); backup paused per backup setting. after 4m" });
        Assert.Equal("skipped", card.StatusClass);
        Assert.Equal(BackupRunStatus.Skipped, card.DisplayedRunStatus);
        Assert.Equal("", card.LastCompletedLabel);
    }
}
