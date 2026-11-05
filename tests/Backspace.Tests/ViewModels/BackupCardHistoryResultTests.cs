using System;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.ViewModels;

public sealed class BackupCardHistoryResultTests
{
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
        var backup = new Backup { LastRunStatus = BackupRunStatus.Success, LastRunStartUtc = now.AddHours(-3), LastRunEndUtc = now.AddMinutes(-90), LastRunSummary = "Backup result: STATUS_SUCCESS." };
        using var card = new BackupCardViewModel(backup, false, false);
        Assert.EndsWith(" · 1h ago", card.LastRunLabel);

        // Ninety minutes later: same backup, older.
        var field = typeof(BackupCardViewModel).GetField("_lastRun", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var last = ((string Summary, DateTime When)?)field.GetValue(card);
        field.SetValue(card, ((string, DateTime)?)(last!.Value.Summary, now.AddMinutes(-180)));
        card.RefreshTimes();
        Assert.EndsWith(" · 3h ago", card.LastRunLabel);

        card.SetRunning();
        card.RefreshTimes();
        Assert.Equal("Running…", card.LastRunLabel);
    }
}
