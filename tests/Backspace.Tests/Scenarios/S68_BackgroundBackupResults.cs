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
        var records = new[] { check, failed };
        var first = new TaskCompletionSource<ServiceHistoryProtocol.Response>();
        var cache = new ServiceHistoryCache(Path.Combine(ctx.RootDir, "service-history"), (_, _) => first.Task);
        var logs = new LogStore(cache);
        using var vm = new BackupsViewModel(logs);
        var window = ctx.Driver.Show(new Window { Width = 1024, Height = 768, Content = new BackupsView { DataContext = vm } });
        var card = Assert.Single(vm.Cards);
        first.SetResult(new(1, records));
        for (var n = 0; n < 100 && card.DisplayedRunStatus != BackupRunStatus.Failed; n++) await ctx.Driver.PumpAsync(20);
        Assert.Equal("fail", card.StatusClass); Assert.Equal("fail", vm.HealthClass);
        Assert.Contains("ran out of space", card.LastRunLabel);
        Assert.Equal(saved, File.ReadAllBytes(AppPaths.ConfigFile));
        Assert.Equal(BackupRunStatus.Success, ServiceLocator.Config.Current.Backups.Single().LastRunStatus);
        ctx.Snapshot(window, "latest-service-failure", "The card and top health banner both show the newer background backup failure. The later successful repository check has not turned the failed backup green. Desktop settings still contain the old value.");
        card.SetRunning(); card.SetQueuedReason("waiting for the backup drive");
        await cache.RefreshAsync(backup.Id, CancellationToken.None);
        vm.Refresh(); await ctx.Driver.PumpAsync(100);
        Assert.True(card.IsRunning); Assert.True(card.IsQueued); Assert.Equal("running", card.StatusClass);
        Assert.Equal("Queued: waiting for the backup drive", card.LastRunLabel);
        ctx.Snapshot(window, "live-run-preserved", "Refreshing saved service history leaves the current queued run in place. An earlier completed result cannot replace a live operation.");
        card.Apply(failed);
        await ctx.Driver.PumpAsync(100);
        Assert.False(card.IsRunning); Assert.Equal("fail", card.StatusClass);
        Assert.Equal(saved, File.ReadAllBytes(AppPaths.ConfigFile));
        ctx.Report.RecordNote("Limits", "Actual persistent service-history cache, LogStore refresh events, card and health banner in headless Avalonia. Service replies and the running signal are controlled. The earlier VM report separately proves the authenticated service-to-desktop history transport; this scenario does not prove native windows or live service progress.");
    }
}
