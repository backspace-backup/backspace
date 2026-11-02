using System;
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
        var failed = Backup(); failed.LastRunStatus = BackupRunStatus.Failed; failed.LastRunSummary = "The destination refused the password.";
        var status = TrayScheduleStatus.Inspect([Backup(), failed], scheduler, "app.exe", DateTime.UtcNow);
        Assert.Equal(ScheduleReadiness.Attention, status.State);
        Assert.Equal("Last backup failed", status.Title);
        Assert.Contains("refused the password", status.Detail);
        var never = new Backup { Name = "Photos", SourcePaths = { "C:\\Photos" }, Targets = { new BackupTarget() } };
        status = TrayScheduleStatus.Inspect([Backup(), never], scheduler, "app.exe", DateTime.UtcNow);
        Assert.Equal(ScheduleReadiness.Unknown, status.State);
        Assert.Equal("Not backed up yet", status.Title);
        // A skipped run after an earlier success is still protected.
        var skipped = Backup(); skipped.LastRunStatus = BackupRunStatus.Skipped;
        Assert.Equal(ScheduleReadiness.Ready, TrayScheduleStatus.Inspect([skipped], scheduler, "app.exe", DateTime.UtcNow).State);
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
        Assert.Equal("1 ready · 1 paused",TrayScheduleStatus.Inspect([b,paused],scheduler,"app.exe",DateTime.UtcNow).Title);
        b.Targets.Clear();
        Assert.Equal(ScheduleReadiness.Attention,TrayScheduleStatus.Inspect([b,paused],scheduler,"app.exe",DateTime.UtcNow).State);
    }
}
