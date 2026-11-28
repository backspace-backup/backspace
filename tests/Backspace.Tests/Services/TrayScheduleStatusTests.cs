using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>The tray menu is built from the backups' cards, as the Backups
/// tab shows them (U258).</summary>
public sealed class TrayScheduleStatusTests
{
    private static Backup Backup(string name = "Documents") => new()
    {
        Name = name, SourcePaths = { "C:\\" + name }, Targets = { new BackupTarget() }, Schedule = TestSchedules.DueInAnHour(),
        LastRunStatus = BackupRunStatus.Success, LastRunEndUtc = DateTime.UtcNow.AddHours(-1), LastSuccessUtc = DateTime.UtcNow.AddHours(-1),
        LastRunSummary = "Backup completed",
    };

    /// <summary>The menu for these backups, from cards made as the tray makes
    /// them, with the Backups tab's banner for the same settings.</summary>
    internal static async Task<(TrayScheduleStatus Tray, string Tab)> Both(IReadOnlyDictionary<string, ScheduleCheck>? schedules, params Backup[] backups)
    {
        ServiceLocator.Config.Update(c => { c.Backups.Clear(); c.Destinations.Clear(); foreach (var b in backups) c.Backups.Add(b); });
        var saved = ServiceLocator.Config.Current.Backups.ToList();
        var tray = await TrayScheduleStatus.FromCardsAsync(saved, DateTime.UtcNow, schedules, TimeSpan.FromSeconds(10));
        using var tab = new BackupsViewModel();
        await Task.WhenAll(tab.Cards.Select(c => c.CheckHistoryRead)).WaitAsync(TimeSpan.FromSeconds(10));
        return (tray, tab.HealthHeadline);
    }

    /// <summary>The owner's menu of 28 November 2026 said "Not backed up yet"
    /// while the Backups tab said "2 backups need attention": local_c's restore
    /// check had failed and local_dropbox had been skipped. The menu now says
    /// what the tab says, local_c's line names the failed check, and the
    /// backups the banner names come first.</summary>
    [AvaloniaFact] public async Task The_menu_says_what_the_Backups_tab_says_a_failed_restore_check_included()
    {
        var now = DateTime.UtcNow;
        var c = Backup("local_c"); c.LastRunEndUtc = now.AddHours(-16); c.LastSuccessUtc = c.LastRunEndUtc;
        c.LastRunSummary = "387.14 MB uploaded · 37 new / 201 mod / 0 rem · in 9m 13s";
        c.LastVerifyUtc = now.AddHours(-4); c.LastVerifyPass = false;
        c.LastVerifySummary = "Restore check (1% of files)\n«Google Drive»: rev 25 · 4 files could not be recovered correctly.";
        var d = Backup("local_d"); d.LastRunEndUtc = now.AddHours(-16); d.LastSuccessUtc = d.LastRunEndUtc;
        d.LastRunSummary = "Completed in 7m 25s · nothing new to upload";
        var dropbox = Backup("local_dropbox"); dropbox.LastSuccessUtc = null; dropbox.LastRunStatus = BackupRunStatus.Skipped;
        dropbox.LastRunEndUtc = now.AddHours(-2); dropbox.LastRunSummary = @"M:\ not mounted · skipped after 1s"; dropbox.SkipNotificationGraceDays = 0;

        var (tray, tab) = await Both(null, c, d, dropbox);
        Assert.Equal("2 backups need attention", tab);
        Assert.Equal(tab, tray.Title);
        Assert.NotEqual("Not backed up yet", tray.Title);
        Assert.Equal(ScheduleReadiness.Attention, tray.State);
        Assert.Equal(("local_c", "fail"), (tray.Lines[0].Name, tray.Lines[0].Tone));
        Assert.Matches("^Restore check.* failed · 4h ago$", tray.Lines[0].Text);
        Assert.Equal("local_dropbox", tray.Lines[1].Name);
        Assert.NotEqual("ok", tray.Lines[1].Tone);
        Assert.Equal(("local_d", "ok"), (tray.Lines[2].Name, tray.Lines[2].Tone));
        Assert.StartsWith("16h ago", tray.Lines[2].Text);
    }

    /// <summary>U084, U258: the headline and lines follow the cards; a failed
    /// run shows its reason, never-run backups are not "everything looks good",
    /// and many backups make six lines and a count.</summary>
    [AvaloniaFact] public async Task Each_line_is_the_cards_and_the_headline_the_tabs()
    {
        var now = DateTime.UtcNow;
        var ok = Backup("local_d"); ok.LastRunEndUtc = now.AddHours(-2); ok.LastSuccessUtc = ok.LastRunEndUtc;
        ok.LastRunSummary = "27.42 MB uploaded · 150 new / 195 mod / 0 rem · in 1m 29s";
        var (tray, tab) = await Both(null, ok);
        Assert.Equal(tab, tray.Title);
        Assert.Equal(ScheduleReadiness.Ready, tray.State);
        Assert.Equal(("local_d", "2h ago · 27.42 MB uploaded", "ok"), (tray.Lines[0].Name, tray.Lines[0].Text, tray.Lines[0].Tone));

        var failed = Backup("local_c"); failed.LastRunStatus = BackupRunStatus.Failed; failed.LastRunEndUtc = now.AddDays(-1);
        failed.LastRunSummary = "Google Drive sign-in expired. Sign in again in the destination's settings.";
        (tray, tab) = await Both(null, ok, failed);
        Assert.Equal(tab, tray.Title);
        Assert.Equal(ScheduleReadiness.Attention, tray.State);
        Assert.Equal(("local_c", "fail"), (tray.Lines[0].Name, tray.Lines[0].Tone));
        Assert.Equal("local_d", tray.Lines[1].Name);

        var never = new Backup { Name = "Music", SourcePaths = { @"C:\Music" }, Targets = { new BackupTarget() }, Schedule = TestSchedules.DueInAnHour() };
        (tray, tab) = await Both(null, ok, never);
        Assert.Equal(tab, tray.Title);
        Assert.NotEqual(ScheduleReadiness.Ready, tray.State);

        var many = Enumerable.Range(1, 10).Select(i => Backup($"b{i:00}")).ToArray();
        (tray, _) = await Both(null, many);
        Assert.Equal(TrayScheduleStatus.MaxLines, tray.Lines.Count);
        Assert.Equal(4, tray.MoreBackups);
    }

    /// <summary>U265: a daily backup last completed 9 days ago is orange in the
    /// menu as on its card; one completed today stays green.</summary>
    [AvaloniaFact] public async Task A_missed_schedule_is_orange_as_on_the_card()
    {
        var now = DateTime.UtcNow;
        var stale = Backup("local_c"); stale.LastRunEndUtc = now.AddDays(-9); stale.LastSuccessUtc = stale.LastRunEndUtc;
        var fresh = Backup("local_d");
        var (tray, tab) = await Both(null, stale, fresh);
        Assert.Equal(tab, tray.Title);
        Assert.Equal(ScheduleReadiness.Attention, tray.State);
        Assert.Equal(("local_c", "warn"), (tray.Lines[0].Name, tray.Lines[0].Tone));
        Assert.StartsWith("9d ago", tray.Lines[0].Text);
        Assert.Equal(("local_d", "ok"), (tray.Lines[1].Name, tray.Lines[1].Tone));
    }

    /// <summary>A schedule that cannot run (as Windows reports it) needs
    /// attention although no card shows it; one not verified is not "good".</summary>
    [AvaloniaTheory]
    [InlineData(ScheduleReadiness.Ready)]
    [InlineData(ScheduleReadiness.Attention)]
    [InlineData(ScheduleReadiness.Unknown)]
    public async Task The_registered_schedule_counts(ScheduleReadiness state)
    {
        var b = Backup();
        var scheduler = new Scheduler(state);
        var schedules = TrayScheduleStatus.Schedules([b], scheduler, "app.exe", DateTime.UtcNow);
        Assert.Equal(1, scheduler.Reads);
        var (tray, _) = await Both(schedules, b);
        Assert.Equal(state, tray.State);
        if (state == ScheduleReadiness.Attention) Assert.Equal(("1 backup needs attention", "fail"), (tray.Title, tray.Lines[0].Tone));
    }

    [AvaloniaFact] public async Task Disabled_paused_and_incomplete_backups()
    {
        var b = Backup(); b.Enabled = false;
        var scheduler = new Scheduler(ScheduleReadiness.Ready);
        Assert.Equal(ScheduleReadiness.Off, (await Both(null, b)).Tray.State);
        b.Enabled = true; b.Schedule.PausedUntilUtc = DateTime.UtcNow.AddHours(1);
        Assert.Equal(ScheduleReadiness.Paused, (await Both(null, b)).Tray.State);
        Assert.Empty(TrayScheduleStatus.Schedules([b], scheduler, "app.exe", DateTime.UtcNow));
        Assert.Equal(0, scheduler.Reads);
        var running = Backup("other");
        var (tray, tab) = await Both(null, running, b);
        Assert.Equal(tab + " · 1 paused", tray.Title);
        running.Targets.Clear();
        Assert.Equal(ScheduleReadiness.Attention, (await Both(null, running, b)).Tray.State);
    }

    private sealed class Scheduler(ScheduleReadiness result) : IScheduler
    {
        public int Reads;
        public void UpsertTask(Backup b, string p) => throw new InvalidOperationException("Status checks must not register tasks");
        public void DeleteTask(Backup b) => throw new InvalidOperationException("Status checks must not remove tasks");
        public string TaskNameFor(Backup b) => b.Name;
        public ScheduleCheck InspectBackup(Backup b, string p) { Reads++; return new(result, "Task missing"); }
    }
}
