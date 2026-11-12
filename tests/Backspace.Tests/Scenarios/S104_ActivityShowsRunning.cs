using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// U216 (owner, 12 November 2026): "When I click Activity, it doesn't show any
/// entry for the ongoing action (backup or check). It should already show it,
/// not just completed work. So we can easily be taken to realtime log for the
/// current action." The card's Activity window starts with what runs now and
/// "Open live log"; Activity &amp; logs then shows that backup's running entry.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S104_ActivityShowsRunning : ScenarioBase
{
    protected override string ScenarioName => "S104-activity-shows-running";

    [AvaloniaFact] public async Task Run_S104() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var dest = new Destination { Name = "Google Drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("repo") };
        var backup = new Backup { Name = "local_d", SourcePaths = { ctx.MakeSource("d", targetBytes: 4096) }, LastRunStatus = BackupRunStatus.Success,
            LastRunEndUtc = DateTime.UtcNow.AddHours(-2) };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(backup); });
        ServiceLocator.Logs.AppendRun(new RunRecord { BackupId = backup.Id, BackupName = backup.Name, StartedUtc = DateTime.UtcNow.AddHours(-2),
            EndedUtc = DateTime.UtcNow.AddHours(-2).AddMinutes(2), Status = BackupRunStatus.Success, Summary = "Backup completed" });

        // A check runs: the history window starts with it.
        var history = new BackupHistoryViewModel(backup, ServiceLocator.Logs.LoadTimelineByBackupId(backup.Id), checks: false) { SelectedFilter = "All activity" };
        history.SetLive("Check · Structure + data sample (1 of 2)", "Started at 20:12 · follow it as it happens in its live log.");
        Assert.Equal(2, history.Rows.Count);
        Assert.True(history.Rows[0].IsLive);
        Assert.Equal("Running", history.Rows[0].Result);
        Assert.Equal("Now", history.Rows[0].Timestamp);
        var window = new BackupHistoryWindow { DataContext = history, Width = 800, Height = 560 };
        ctx.Driver.Show(window);
        await ctx.Driver.PumpAsync(200);
        var open = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "OpenLiveButton" && b.IsEffectivelyVisible);
        ctx.Snapshot(window, "activity with the running check",
            "local_d's Activity: \"Running · Check · Structure + data sample (1 of 2) · Now\" at the top with \"Open live log\", then the saved backup.");

        // Open live log: Activity & logs, on that backup's running check.
        var live = new RunRecord { BackupId = backup.Id, BackupName = backup.Name, Kind = RunKind.Check, CheckKind = CheckKind.DataSample,
            StartedUtc = DateTime.UtcNow, Status = BackupRunStatus.Running, Summary = "Running: Structure check + data sample (1 GiB)" };
        ServiceLocator.Activity.Register(live);
        try
        {
            using var logs = new LogsViewModel();
            var asked = (string?)null;
            void Requested(string id) => asked = id;
            AppNavigator.LiveRequested += Requested;
            try { open.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); }
            finally { AppNavigator.LiveRequested -= Requested; }
            await ctx.Driver.PumpAsync(100);
            Assert.Equal(backup.Id, asked);
            Assert.False(window.IsVisible);
            Assert.Equal("local_d", logs.SelectedBackup);
            Assert.Same(live, logs.SelectedRun);
            Assert.True(logs.IsShowingLive);
            var view = new LogsView { DataContext = logs };
            var host = ctx.Driver.Show(new Window { Content = view, Width = 1100, Height = 700 });
            await ctx.Driver.PumpAsync(200);
            ctx.Snapshot(host, "live log of the running check",
                "Activity & logs after \"Open live log\": local_d selected, its running check chosen, the live output shown.");
            host.Close();
        }
        finally { ServiceLocator.Activity.Unregister(live.Id); }

        // Nothing runs: no live row.
        history.SetLive(null, "");
        Assert.DoesNotContain(history.Rows, r => r.IsLive);
    }
}
