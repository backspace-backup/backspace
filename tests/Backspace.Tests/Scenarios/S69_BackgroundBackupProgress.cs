using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

[Collection(ScenarioCollection.Name)]
public sealed class S69_BackgroundBackupProgress : ScenarioBase
{
    protected override string ScenarioName => "S69-background-backup-progress";
    [AvaloniaFact] public async Task Run_S69() => await Run();
    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var destination = new Destination { Name = "Backup drive", PathOrSubpath = ctx.MakeLocalDestination("repo") };
        var source = ctx.MakeSource("Documents", targetBytes: 102400);
        var backup = new Backup { Name = "Documents", SourcePaths = { source }, Targets = { new() { DestinationId = destination.Id } } };
        ServiceLocator.Config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(destination); });
        using var vm = new BackupsViewModel();
        var window = ctx.Driver.Show(new Window { Width = 1024, Height = 768, Content = new BackupsView { DataContext = vm } });
        var card = Assert.Single(vm.Cards); var runId = Guid.NewGuid().ToString("N");
        var start = DateTime.UtcNow.AddSeconds(-3);
        // U155: the card counts the time from the service run's own start,
        // 1 h 05m ago here, not from when Backspace saw it.
        var serviceStart = start.AddMinutes(-65);
        card.UtcNow = () => serviceStart.AddMinutes(65).AddSeconds(20);
        var activity = new ServiceActivity.Snapshot(1, runId, "running", backup.Id, "backup", serviceStart,
            new(backup.Id, 42, "", [new(source, 42, true, "42 of 100 files", "Backing up")], [new("Backup drive", 42)]));
        card.ApplyBackgroundActivity(ServiceActivity.Parse(ServiceActivity.Encode(activity)), null);
        await ctx.Driver.PumpAsync(100);
        Assert.True(card.IsRunning); Assert.Equal(runId, card.BackgroundRunId); Assert.Equal(42, card.OverallPercent);
        Assert.Equal("Backing up", card.CurrentPhaseLabel); Assert.False(card.CanRunNow);
        Assert.Equal("Running · 1h 5m", card.RunningLabel);
        ctx.Snapshot(window, "background-progress", "The service backup is running with 42% source, destination and overall progress, and has been for 1h 5m (from the service run's start). Run now is replaced by Stop, and the phase says Backing up.");
        card.ApplyBackgroundActivity(activity with { BackupId = "another-backup" }, null);
        Assert.Equal(42, card.OverallPercent);
        card.ApplyBackgroundActivity(activity, "Cannot reach the background service. Completion has not been confirmed.");
        Assert.True(card.IsRunning); Assert.Equal("Service not responding", card.LastRunLabel);
        Assert.Contains("not been confirmed", card.LastRunDetail);
        Assert.Equal("Service not responding", card.RunningLabel); // the line shown while it runs
        Assert.StartsWith("Cannot reach the background service.", card.ProgressTooltip);
        Assert.Contains("Last known: 42.0% done", card.ProgressTooltip.Replace(",", "."));
        ctx.Snapshot(window, "connection-interrupted", "Losing the connection preserves the running state; the card says Service not responding in full, the hover explains that completion is unknown; no false success.");
        // U264: a queued run whose service stops answering: the card line is
        // short, the hover says the same thing and adds the last known wait.
        var queued = activity with { Progress = activity.Progress! with { Queued = "waiting for local_c (same destination)" } };
        card.ApplyBackgroundActivity(queued, "Cannot reach the background service. Completion has not been confirmed.");
        Assert.Equal("Service not responding", card.LastRunLabel);
        Assert.StartsWith("Cannot reach the background service.", card.LastRunDetail);
        Assert.Contains("Last known: queued, waiting for local_c (same destination).", card.LastRunDetail);
        Assert.DoesNotContain("Queued:", card.LastRunDetail);
        card.ApplyBackgroundActivity(queued, null);
        Assert.Equal("Queued: waiting for local_c (same destination)", card.LastRunDetail); // reachable again: no stale error
        Assert.Equal("Running · 1h 5m", card.RunningLabel);
        card.ApplyBackgroundActivity(activity, null);
        var result = new RunRecord { BackupId = backup.Id, BackupName = backup.Name, StartedUtc = start, EndedUtc = DateTime.UtcNow,
            Status = BackupRunStatus.Success, Summary = "100 KB backed up" };
        card.ApplyBackgroundActivity(activity with { State = "completed", Progress = activity.Progress with { Result = result } }, null);
        Assert.False(card.IsRunning); Assert.Null(card.BackgroundRunId); Assert.Equal("ok", card.StatusClass);
        Assert.Null(card.RunStartedUtc); Assert.Equal("Running", card.RunningLabel); // the next run starts its own count
        Assert.Contains("100 KB", card.LastRunLabel);
        await ctx.Driver.PumpAsync(200);
        Assert.Equal("ok", card.StatusClass); Assert.Contains("100 KB", card.LastRunLabel);
        ctx.Report.RecordNote("Limits", "Headless actual view and activity decoding; controlled service snapshots. Real process transport, cancellation and native window behavior require separate VM checks.");
    }
}
