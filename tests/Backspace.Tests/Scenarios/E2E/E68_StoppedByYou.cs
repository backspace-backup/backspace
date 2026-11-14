using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// U242 (owner, 13 November 2026, approved 14 November): "If I manually stop a
/// backup ... it shouldn't cause the status to be a warning if nothing is
/// overdue." With the real engine, through the card's Run now and Stop: a
/// completed backup, then a second run stopped with Stop. The card says
/// "Stopped by you · time" in neutral, the last completed run under it, the
/// overall status stays healthy and the backup's saved status stays the
/// completed run's. The history keeps the stopped run, marked as yours.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E68_StoppedByYou(ITestOutputHelper output) : BackupE2EBase(output)
{
    protected override string ScenarioName => "E68-stopped-by-you";
    [AvaloniaFact] public Task Run_E68() => Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var source = ctx.MakeSource("docs", 6801, 3_000_000);
        var dest = new Destination { Name = "USB drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("repo") };
        var backup = new Backup { Name = "documents", UseVss = false, VssDisabledAcknowledged = true, SourcePaths = { source },
            PruneSchedule = new(), Targets = { new() { DestinationId = dest.Id } } };
        ServiceLocator.Config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(dest); });

        var view = new BackupsView { DataContext = new BackupsViewModel() };
        var host = new Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);
        var vm = (BackupsViewModel)view.DataContext!;
        var card = vm.Cards.Single(c => c.Id == backup.Id);

        // A completed backup first.
        await vm.RunNowCommand.ExecuteAsync(card);
        Assert.True(ctx.Driver.WaitFor(() => !card.IsRunning, 120_000), "The first backup did not finish.");
        Assert.Equal(BackupRunStatus.Success, card.DisplayedRunStatus);

        // Then a slow one (new data, at 50 KB/s), stopped with the card's Stop.
        var more = new byte[2_000_000];
        new Random(6802).NextBytes(more);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(source, "more.bin"), more);
        ServiceLocator.Config.Update(c => c.Backups.Single(b => b.Id == backup.Id).BandwidthLimitKBps = 50);
        var run = vm.RunNowCommand.ExecuteAsync(card);
        Assert.True(ctx.Driver.WaitFor(() => card.IsRunning, 10_000), "The second backup did not start.");
        await ctx.Driver.PumpAsync(2_000);
        await vm.CancelRunCommand.ExecuteAsync(card);
        await run;
        Assert.True(ctx.Driver.WaitFor(() => !card.IsRunning, 60_000), "Stop did not end the run.");
        await ctx.Driver.PumpAsync(300);

        var history = ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id).OrderByDescending(r => r.StartedUtc).ToList();
        Assert.Equal(2, history.Count);
        Assert.True(history[0].StoppedByYou);
        Assert.Equal(BackupRunStatus.Skipped, history[0].Status);
        Assert.StartsWith("Stopped by you", history[0].Summary);
        Assert.False(history[1].StoppedByYou);

        // Neutral on the card, the completed run under it.
        Assert.Equal("idle", card.StatusClass);
        Assert.StartsWith("Stopped by you · ", card.LastRunLabel);
        Assert.StartsWith("Last completed: ", card.LastCompletedLabel);
        // Not a warning anywhere: the status read by the overall line and the tray.
        Assert.Equal(BackupRunStatus.Success, card.DisplayedRunStatus);
        Assert.Equal(BackupRunStatus.Success, ServiceLocator.Config.Current.Backups.Single(b => b.Id == backup.Id).LastRunStatus);
        Assert.Equal("ok", vm.HealthClass);
        Assert.DoesNotContain("skipped", vm.HealthHeadline, StringComparison.OrdinalIgnoreCase);
        ctx.Snapshot(host, "stopped by you",
            "The card after pressing Stop on a running backup: \"Stopped by you · HH:mm\" in neutral grey, \"Last completed: …\" under it; the line at the top stays healthy (no warning).");
    }
}
