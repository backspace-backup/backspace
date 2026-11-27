using System;
using System.Linq;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class TrayScheduleStatusTests
{
    private static Backup Backup() => new() { Name = "Documents", SourcePaths = { "C:\\Documents" }, Targets = { new BackupTarget() },
        LastRunStatus = BackupRunStatus.Success, LastRunEndUtc = DateTime.UtcNow.AddHours(-1), LastSuccessUtc = DateTime.UtcNow.AddHours(-1) };

    /// <summary>U084: ready schedules alone never earn the check mark; a failed
    /// or never-run backup shows on the tray icon.</summary>
    [Fact] public void A_failed_or_never_run_backup_is_not_shown_as_ready()
    {
        var scheduler = new Scheduler(ScheduleReadiness.Ready);
        var failed = Backup(); failed.Name = "Photos"; failed.LastRunStatus = BackupRunStatus.Failed; failed.LastRunSummary = "The destination refused the password.";
        var status = TrayScheduleStatus.Inspect([Backup(), failed], scheduler, "app.exe", DateTime.UtcNow);
        Assert.Equal(ScheduleReadiness.Attention, status.State);
        Assert.Equal("1 backup needs attention", status.Title);
        var never = new Backup { Name = "Music", SourcePaths = { @"C:\Music" }, Targets = { new BackupTarget() } };
        status = TrayScheduleStatus.Inspect([Backup(), never], scheduler, "app.exe", DateTime.UtcNow);
        Assert.Equal(ScheduleReadiness.Unknown, status.State);
        Assert.Equal("Not backed up yet", status.Title);
        // A skipped run is orange on its card, so it is not "everything looks good".
        var skipped = Backup(); skipped.LastRunStatus = BackupRunStatus.Skipped; skipped.LastRunSummary = @"M:\ not mounted · skipped after 1s";
        Assert.Equal(ScheduleReadiness.Attention, TrayScheduleStatus.Inspect([skipped], scheduler, "app.exe", DateTime.UtcNow).State);
    }

    /// <summary>U258 (owner, 16 November 2026): "Last backup 2h ago in menu is
    /// not useful if we don't even know for which backup it is. Try to be as
    /// consistent as possible with how info is shown on the Backups tab". The
    /// headline as on the tab, then each backup with its card's Last backup
    /// line, the ones needing attention first, at most six.</summary>
    [Fact] public void The_menu_lists_each_backup_as_its_card_says_it()
    {
        var scheduler = new Scheduler(ScheduleReadiness.Ready);
        var now = DateTime.UtcNow;
        var ok = Backup(); ok.Name = "local_d"; ok.LastRunEndUtc = now.AddHours(-2); ok.LastRunSummary = "27.42 MB uploaded · 150 new / 195 mod / 0 rem · in 1m 29s";
        var status = TrayScheduleStatus.Inspect([ok], scheduler, "app.exe", now);
        Assert.Equal("Everything looks good", status.Title);
        var line = Assert.Single(status.Lines);
        Assert.Equal(("local_d", "2h ago · 27.42 MB uploaded", "ok"), (line.Name, line.Text, line.Tone));

        var failed = Backup(); failed.Name = "local_c"; failed.LastRunStatus = BackupRunStatus.Failed; failed.LastRunEndUtc = now.AddDays(-1);
        failed.LastRunSummary = "Google Drive sign-in expired. Sign in again in the destination's settings.";
        status = TrayScheduleStatus.Inspect([ok, failed], scheduler, "app.exe", now);
        Assert.Equal("1 backup needs attention", status.Title);
        Assert.Equal(("local_c", "Google Drive sign-in expired · 1d ago", "fail"), (status.Lines[0].Name, status.Lines[0].Text, status.Lines[0].Tone));
        Assert.Equal("local_d", status.Lines[1].Name);

        // Many backups: six lines and a count, never a menu taller than the screen.
        var many = Enumerable.Range(1, 10).Select(i => { var b = Backup(); b.Name = $"b{i:00}"; return b; }).ToArray();
        status = TrayScheduleStatus.Inspect(many, scheduler, "app.exe", now);
        Assert.Equal(TrayScheduleStatus.MaxLines, status.Lines.Count);
        Assert.Equal(4, status.MoreBackups);
    }

    /// <summary>U265: a daily backup last completed 9 days ago is orange in the
    /// menu as on its card, and the headline asks for attention rather than
    /// saying it finished with warnings; one completed today stays green.</summary>
    [Fact] public void A_missed_schedule_is_orange_as_on_the_card()
    {
        var scheduler = new Scheduler(ScheduleReadiness.Ready);
        var now = DateTime.UtcNow;
        var stale = Backup(); stale.Name = "local_c"; stale.LastRunEndUtc = now.AddDays(-9); stale.LastRunSummary = "Backup completed";
        var fresh = Backup(); fresh.Name = "local_d";
        var status = TrayScheduleStatus.Inspect([stale, fresh], scheduler, "app.exe", now);
        Assert.Equal(ScheduleReadiness.Attention, status.State);
        Assert.Equal("1 backup needs attention", status.Title);
        Assert.Equal(("local_c", "warn"), (status.Lines[0].Name, status.Lines[0].Tone));
        Assert.StartsWith("9d ago", status.Lines[0].Text);
        Assert.Equal(("local_d", "ok"), (status.Lines[1].Name, status.Lines[1].Tone));
        Assert.Equal("Everything looks good", TrayScheduleStatus.Inspect([fresh], scheduler, "app.exe", now).Title);
    }
    private sealed class Scheduler(ScheduleReadiness result) : IScheduler
    {
        public int Reads;
        public void UpsertTask(Backup b, string p) => throw new InvalidOperationException("Status checks must not register tasks");
        public void DeleteTask(Backup b) => throw new InvalidOperationException("Status checks must not remove tasks");
        public string TaskNameFor(Backup b) => b.Name;
        public ScheduleCheck InspectBackup(Backup b,string p) { Reads++; return new(result,"Task missing"); }
    }
    [Theory]
    [InlineData(ScheduleReadiness.Ready)]
    [InlineData(ScheduleReadiness.Attention)]
    [InlineData(ScheduleReadiness.Unknown)]
    public void ReflectsOsResultInsteadOfConfigAlone(ScheduleReadiness state)
    {
        var scheduler = new Scheduler(state);
        Assert.Equal(state,TrayScheduleStatus.Inspect([Backup()],scheduler,"app.exe",DateTime.UtcNow).State);
        Assert.Equal(1,scheduler.Reads);
    }
    [Fact] public void DisabledAndSnoozedBackupsAreNotArmed()
    {
        var b=Backup();var scheduler=new Scheduler(ScheduleReadiness.Ready);b.Enabled=false;
        Assert.Equal(ScheduleReadiness.Off,TrayScheduleStatus.Inspect([b],scheduler,"app.exe",DateTime.UtcNow).State);
        b.Enabled=true;b.Schedule.PausedUntilUtc=DateTime.UtcNow.AddHours(1);
        Assert.Equal(ScheduleReadiness.Paused,TrayScheduleStatus.Inspect([b],scheduler,"app.exe",DateTime.UtcNow).State);
        Assert.Equal(0,scheduler.Reads);
        b.Schedule.PausedUntilUtc=DateTime.UtcNow.AddMinutes(-1);
        Assert.Equal(ScheduleReadiness.Ready,TrayScheduleStatus.Inspect([b],scheduler,"app.exe",DateTime.UtcNow).State);
    }
    [Fact] public void PartialPauseIsVisibleAndIncompleteBackupCannotBeReady()
    {
        var b=Backup();var paused=Backup();paused.Schedule.PausedUntilUtc=DateTime.UtcNow.AddHours(1);
        var scheduler=new Scheduler(ScheduleReadiness.Ready);
        Assert.Equal("Everything looks good · 1 paused",TrayScheduleStatus.Inspect([b,paused],scheduler,"app.exe",DateTime.UtcNow).Title);
        b.Targets.Clear();
        Assert.Equal(ScheduleReadiness.Attention,TrayScheduleStatus.Inspect([b,paused],scheduler,"app.exe",DateTime.UtcNow).State);
    }
}
