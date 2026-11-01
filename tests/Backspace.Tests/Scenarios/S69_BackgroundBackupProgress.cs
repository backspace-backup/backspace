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
        var activity = new ServiceActivity.Snapshot(1, runId, "running", backup.Id, "backup", start,
            new(backup.Id, 42, "", [new(source, 42, true, "42 of 100 files", "Backing up")], [new("Backup drive", 42)]));
        card.ApplyBackgroundActivity(ServiceActivity.Parse(ServiceActivity.Encode(activity)), null);
        await ctx.Driver.PumpAsync(100);
        Assert.True(card.IsRunning); Assert.Equal(runId, card.BackgroundRunId); Assert.Equal(42, card.OverallPercent);
        Assert.Equal("Backing up", card.CurrentPhaseLabel); Assert.False(card.CanRunNow);
        ctx.Snapshot(window, "background-progress", "The service backup is running with 42% source, destination and overall progress. Run now is replaced by Stop, and the phase says Backing up.");
        card.ApplyBackgroundActivity(activity with { BackupId = "another-backup" }, null);
        Assert.Equal(42, card.OverallPercent);
        card.ApplyBackgroundActivity(activity, "Cannot reach the background service. Completion has not been confirmed.");
        Assert.True(card.IsRunning); Assert.Contains("not been confirmed", card.LastRunLabel);
        ctx.Snapshot(window, "connection-interrupted", "Losing the connection preserves the running state and explains that completion is unknown; it does not display a false success.");
        var result = new RunRecord { BackupId = backup.Id, BackupName = backup.Name, StartedUtc = start, EndedUtc = DateTime.UtcNow,
            Status = BackupRunStatus.Success, Summary = "100 KB backed up" };
        card.ApplyBackgroundActivity(activity with { State = "completed", Progress = activity.Progress with { Result = result } }, null);
        Assert.False(card.IsRunning); Assert.Null(card.BackgroundRunId); Assert.Equal("ok", card.StatusClass);
        Assert.Contains("100 KB", card.LastRunLabel);
        await ctx.Driver.PumpAsync(200);
        Assert.Equal("ok", card.StatusClass); Assert.Contains("100 KB", card.LastRunLabel);
        ctx.Report.RecordNote("Limits", "Headless actual view and activity decoding; controlled service snapshots. Real process transport, cancellation and native window behavior require separate VM checks.");
    }
}
