using System;
using System.IO;
using System.Linq;
using System.Threading;
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
public sealed class S68_BackgroundBackupResults : ScenarioBase
{
    protected override string ScenarioName => "S68-background-backup-results";
    [AvaloniaFact] public async Task Run_S68() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var now = DateTime.UtcNow;
        var destination = new Destination { Name = "Backup drive", PathOrSubpath = ctx.MakeLocalDestination("repo") };
        var backup = new Backup { Name = "Documents", SourcePaths = { ctx.MakeSource("Documents", targetBytes: 102400) },
            Targets = { new() { DestinationId = destination.Id } }, LastRunStatus = BackupRunStatus.Success,
            LastRunStartUtc = now.AddDays(-2), LastRunEndUtc = now.AddDays(-2).AddMinutes(1), LastRunSummary = "Old desktop result" };
        ServiceLocator.Config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(destination); });
        var saved = File.ReadAllBytes(AppPaths.ConfigFile);
        var failed = new RunRecord { BackupId = backup.Id, BackupName = backup.Name, StartedUtc = now.AddMinutes(-3), EndedUtc = now.AddMinutes(-2), Status = BackupRunStatus.Failed, Summary = "Backup drive ran out of space" };
        var check = new RunRecord { BackupId = backup.Id, BackupName = backup.Name, Kind = RunKind.Check, StartedUtc = now.AddMinutes(-1), EndedUtc = now, Status = BackupRunStatus.Success, Summary = "Repository check completed" };
        // The background service recorded both while the app was closed, in
        // its history inside the portable folder, read as it is (U138).
        var installation = Path.Combine(ctx.RootDir, "portable");
        var service = new LogStore(Path.Combine(SystemService.DataDirectoryFor(installation), "logs", "engine"));
        service.AppendRun(failed); service.AppendRun(check);
        var logs = LogStore.WithServiceOf(installation);
        var refreshed = 0;
        logs.HistoryRefreshed += id => { if (id == backup.Id) System.Threading.Interlocked.Increment(ref refreshed); };
        using var vm = new BackupsViewModel(logs);
        var window = ctx.Driver.Show(new Window { Width = 1024, Height = 768, Content = new BackupsView { DataContext = vm } });
        var card = Assert.Single(vm.Cards);
        for (var n = 0; n < 100 && card.DisplayedRunStatus != BackupRunStatus.Failed; n++) await ctx.Driver.PumpAsync(20);
        Assert.Equal("fail", card.StatusClass); Assert.Equal("fail", vm.HealthClass);
        Assert.Contains("ran out of space", card.LastRunLabel);
        Assert.Equal(saved, File.ReadAllBytes(AppPaths.ConfigFile));
        Assert.Equal(BackupRunStatus.Success, ServiceLocator.Config.Current.Backups.Single().LastRunStatus);
        ctx.Snapshot(window, "latest-service-failure", "The card and top health banner both show the newer background backup failure. The later successful repository check has not turned the failed backup green. Desktop settings still contain the old value.");
        card.SetRunning(); card.SetQueuedReason("waiting for the backup drive");
        // The service records another run: the app sees its files change.
        var before = refreshed;
        service.AppendRun(new RunRecord { BackupId = backup.Id, BackupName = backup.Name, Kind = RunKind.Check, StartedUtc = now, EndedUtc = now, Status = BackupRunStatus.Success, Summary = "Another check" });
        for (var n = 0; n < 150 && refreshed == before; n++) { logs.RefreshBackgroundHistory(backup.Id); await ctx.Driver.PumpAsync(20); }
        Assert.True(refreshed > before);
        vm.Refresh(); await ctx.Driver.PumpAsync(100);
        Assert.True(card.IsRunning); Assert.True(card.IsQueued); Assert.Equal("running", card.StatusClass);
        Assert.Equal("Queued", card.LastRunLabel);
        Assert.Equal("Queued: waiting for the backup drive", card.LastRunDetail);
        ctx.Snapshot(window, "live-run-preserved", "Refreshing saved service history leaves the current queued run in place. An earlier completed result cannot replace a live operation.");
        card.Apply(failed);
        await ctx.Driver.PumpAsync(100);
        Assert.False(card.IsRunning); Assert.Equal("fail", card.StatusClass);
        Assert.Equal(saved, File.ReadAllBytes(AppPaths.ConfigFile));
        ctx.Report.RecordNote("Limits", "The service's real history files in a portable folder, read by LogStore with its change signal, card and health banner in headless Avalonia. The running signal is controlled; this scenario does not prove native windows or live service progress.");
    }
}
