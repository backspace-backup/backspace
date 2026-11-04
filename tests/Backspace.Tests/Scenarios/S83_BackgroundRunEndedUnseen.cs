using System;
using System.Linq;
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

/// <summary>
/// U128 (4 November 2026): "local_d just failed (i got the notification) but it
/// still shows Running...". As Backspace opened, the background service ran
/// local_d, which failed in half a second, then went straight on to
/// local_dropbox. The card had seen local_d running, then only ever heard
/// about local_dropbox's run, so it never saw local_d end.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S83_BackgroundRunEndedUnseen : ScenarioBase
{
    protected override string ScenarioName => "S83-background-run-ended-unseen";
    [AvaloniaFact] public async Task Run_S83() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var dropbox = new Destination { Name = "Dropbox - erwin_local_d", PathOrSubpath = ctx.MakeLocalDestination("d") };
        var drive = new Destination { Name = "External - M:", PathOrSubpath = ctx.MakeLocalDestination("m") };
        var localD = new Backup { Name = "local_d", SourcePaths = { ctx.MakeSource("D") }, Targets = { new() { DestinationId = dropbox.Id } } };
        var localDropbox = new Backup { Name = "local_dropbox", SourcePaths = { ctx.MakeSource("Dropbox") }, Targets = { new() { DestinationId = drive.Id } } };
        ServiceLocator.Config.Update(c => { c.Destinations.Add(dropbox); c.Destinations.Add(drive); c.Backups.Add(localD); c.Backups.Add(localDropbox); });
        using var vm = new BackupsViewModel();
        var window = ctx.Driver.Show(new Window { Width = 1024, Height = 820, Content = new BackupsView { DataContext = vm } });
        var card = vm.Cards.Single(c => c.Id == localD.Id);
        var otherCard = vm.Cards.Single(c => c.Id == localDropbox.Id);

        // 01:40:08: the service runs local_d; the card sees it.
        var started = DateTime.UtcNow.AddSeconds(-3);
        var ours = new ServiceActivity.Snapshot(1, Guid.NewGuid().ToString("N"), "running", localD.Id, "backup", started,
            new(localD.Id, 0, "", [], []));
        card.ApplyBackgroundActivity(ours, null);
        Assert.True(card.IsRunning);

        // 01:40:09: it fails (the service records it); 01:40:10: the service
        // runs local_dropbox. The card only hears about that run.
        var failed = new RunRecord { BackupId = localD.Id, BackupName = localD.Name, StartedUtc = started, EndedUtc = started.AddSeconds(1),
            Status = BackupRunStatus.Failed, Summary = "1 error · in 1s" };
        failed.Errors.Add("«Dropbox - erwin_local_d» isn't set up yet: connect your Dropbox account and choose a storage password.");
        ServiceLocator.Logs.AppendRun(failed);
        var next = new ServiceActivity.Snapshot(1, Guid.NewGuid().ToString("N"), "running", localDropbox.Id, "backup", DateTime.UtcNow,
            new(localDropbox.Id, 0, "", [], []));
        foreach (var c in new[] { card, otherCard }) c.ApplyBackgroundActivity(next, null);
        await ctx.Driver.PumpAsync(200);

        Assert.False(card.IsRunning, "local_d still shows Running after the service moved on.");
        Assert.Null(card.BackgroundRunId);
        Assert.Equal("fail", card.StatusClass);
        Assert.True(otherCard.IsRunning);
        ctx.Snapshot(window, "failed run shown after the service moved on",
            "local_d failed while the service went straight on to local_dropbox: local_d's card shows the failure, not Running…; local_dropbox's card shows its own run.");

        // The same, before the service's history has the failure: the card
        // stops saying Running at once, then shows the result when it arrives.
        var lateCard = card;
        var third = new ServiceActivity.Snapshot(1, Guid.NewGuid().ToString("N"), "running", localD.Id, "backup", DateTime.UtcNow, new(localD.Id, 0, "", [], []));
        lateCard.ApplyBackgroundActivity(third, null);
        Assert.True(lateCard.IsRunning);
        lateCard.ApplyBackgroundActivity(next with { RunId = Guid.NewGuid().ToString("N") }, null);
        Assert.False(lateCard.IsRunning);
        // A connection problem is not a sign the run ended.
        lateCard.ApplyBackgroundActivity(third, null);
        lateCard.ApplyBackgroundActivity(next, "Cannot reach the background service.");
        Assert.True(lateCard.IsRunning);
    }
}
