using System;
using System.Linq;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.ViewModels;

/// <summary>
/// U269 (owner, 27 November 2026: "The goal was to not annoy user with
/// warnings if an external drive is not plugged in (up until the grace
/// period)"; B027's original notes: "a grace period number of days selector
/// setting to ignore skipped backups"). Within the quiet period a skipped run
/// warns nowhere: not the card, the banner, the tray, nor a check falling due.
/// Once the period ends, it is orange as a skip, never red.
/// </summary>
public sealed class QuietSkipTests
{
    private static (Backup Backup, RunRecord Good, RunRecord Skipped) Setup(DateTime now, int graceDays = 7)
    {
        var backup = new Backup
        {
            Name = "usb_photos", Enabled = true, Schedule = TestSchedules.DueInAnHour(), SkipNotificationGraceDays = graceDays,
            CreatedUtc = now.AddDays(-30), LastSuccessUtc = now.AddDays(-2), SourcePaths = { @"D:\Photos" }, Targets = { new BackupTarget { DestinationId = "usb" } },
            LastRunStatus = BackupRunStatus.Skipped, LastRunStartUtc = now.AddHours(-23).AddMinutes(-1), LastRunEndUtc = now.AddHours(-23),
            LastRunSummary = "Skipped: the drive «Photos» (E:) is not plugged in",
        };
        var good = new RunRecord { BackupId = backup.Id, Kind = RunKind.Backup, StartedUtc = now.AddDays(-2).AddMinutes(-5), EndedUtc = now.AddDays(-2),
            Status = BackupRunStatus.Success, Summary = "27.42 MB uploaded · 150 new / 195 mod / 0 rem · in 1m 29s" };
        var skipped = new RunRecord { BackupId = backup.Id, Kind = RunKind.Backup, StartedUtc = backup.LastRunStartUtc!.Value, EndedUtc = backup.LastRunEndUtc,
            Status = BackupRunStatus.Skipped, Summary = backup.LastRunSummary };
        return (backup, good, skipped);
    }

    [AvaloniaFact]
    public void Within_the_quiet_period_an_unplugged_drive_keeps_the_card_green_with_the_skip_on_hover()
    {
        var now = DateTime.UtcNow;
        var (backup, good, skipped) = Setup(now);
        using var card = new BackupCardViewModel(backup, false, false) { UtcNow = () => now };
        card.Apply(good); card.Apply(skipped);
        Assert.Equal("ok", card.StatusClass);
        Assert.Equal(BackupRunStatus.Success, card.DisplayedRunStatus);
        Assert.True(card.QuietSkip);
        Assert.False(card.MissedSchedule, "The skip ran at its time: no scheduled run was missed.");
        Assert.StartsWith("2d ago", card.LastRunLabel);
        Assert.StartsWith("Skipped ", card.LastRunDetail);
        Assert.Contains("is not plugged in. Not flagged until", card.LastRunDetail);
        Assert.Contains("skipped runs stay quiet for 7 days", card.LastRunDetail);

        // Once the quiet period ends, the open card turns orange as a skip.
        card.UtcNow = () => now.AddDays(5).AddMinutes(1);
        card.RefreshTimes();
        Assert.Equal("skipped", card.StatusClass);
        Assert.Equal(BackupRunStatus.Skipped, card.DisplayedRunStatus);
        Assert.False(card.QuietSkip);
    }

    [AvaloniaFact]
    public void Without_a_quiet_period_or_for_a_failure_the_card_warns_at_once()
    {
        var now = DateTime.UtcNow;
        var (backup, good, skipped) = Setup(now, graceDays: 0);
        using (var card = new BackupCardViewModel(backup, false, false) { UtcNow = () => now })
        {
            card.Apply(good); card.Apply(skipped);
            Assert.Equal("skipped", card.StatusClass);
        }
        var (other, good2, failed) = Setup(now);
        failed.Status = BackupRunStatus.Failed; failed.Summary = "The drive could not be written";
        using (var card = new BackupCardViewModel(other, false, false) { UtcNow = () => now })
        {
            card.Apply(good2); card.Apply(failed);
            Assert.Equal("fail", card.StatusClass);
            Assert.False(card.QuietSkip);
        }
    }

    [AvaloniaFact]
    public void A_backup_never_backed_up_whose_drive_is_unplugged_stays_neutral_not_orange()
    {
        var now = DateTime.UtcNow;
        var (backup, _, skipped) = Setup(now);
        backup.LastSuccessUtc = null; backup.CreatedUtc = now.AddDays(-1);
        using var card = new BackupCardViewModel(backup, false, false) { UtcNow = () => now };
        card.Apply(skipped);
        Assert.Equal("idle", card.StatusClass);
        Assert.Equal("Not backed up yet", card.LastRunLabel);
        Assert.Contains("is not plugged in", card.LastRunDetail);
    }

    [Fact]
    public void The_tray_says_everything_looks_good_within_the_quiet_period_and_needs_attention_after()
    {
        var now = DateTime.UtcNow;
        var (backup, _, _) = Setup(now);
        var scheduler = new ReadyScheduler();
        var quiet = TrayScheduleStatus.Inspect([backup], scheduler, "Backspace.exe", now);
        Assert.True(quiet.State == ScheduleReadiness.Ready, quiet.Title + " | " + string.Join(" | ", quiet.Lines.Select(l => l.Text + " " + l.Tone)));
        var line = Assert.Single(quiet.Lines);
        Assert.Equal("ok", line.Tone);
        Assert.Equal("Backed up 2d ago · then skipped", line.Text);
        var after = TrayScheduleStatus.Inspect([backup], scheduler, "Backspace.exe", now.AddDays(5).AddMinutes(1));
        Assert.Equal(ScheduleReadiness.Attention, after.State);
        Assert.Equal("skipped", Assert.Single(after.Lines).Tone);
    }

    [Fact]
    public void A_check_falling_due_while_the_drive_is_away_is_not_flagged_within_the_quiet_period()
    {
        var now = DateTime.UtcNow;
        var (backup, _, _) = Setup(now);
        backup.Checks = [new BackupCheck { Kind = CheckKind.Structure, Schedule = new BackupSchedule { Frequency = ScheduleFrequency.Daily, TimeOfDay = TimeSpan.Zero } }];
        var passed = new BackupCheckEntry("c", now.AddDays(-3), BackupRunStatus.Success, CheckDepth.RepositoryStructure, "Structure check passed");
        Assert.Equal("ok", BackupCheckHistory.State(backup, [passed], now).Tone);
        // The negative case: after the quiet period, the overdue check is said.
        Assert.Equal("stale", BackupCheckHistory.State(backup, [passed], now.AddDays(6)).Tone);
        backup.LastRunStatus = BackupRunStatus.Success;
        Assert.Equal("stale", BackupCheckHistory.State(backup, [passed], now).Tone);
    }

    private sealed class ReadyScheduler : IScheduler
    {
        public ScheduleCheck InspectBackup(Backup backup, string exePath) => new(ScheduleReadiness.Ready, "");
        public void UpsertTask(Backup backup, string exePath) { }
        public void DeleteTask(Backup backup) { }
        public string TaskNameFor(Backup backup) => backup.Name;
    }
}
