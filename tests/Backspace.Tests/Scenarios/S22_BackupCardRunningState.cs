using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Backup card while a run is in flight: the Run button hides,
/// the Stop button appears, the inline running indicator pulses,
/// and a per-source progress bar shows current bytes/total.
///
/// This visual scenario seeds the running state and exercises a real
/// Config.Changed refresh. It does not run a backup engine; live backup
/// coverage is separate.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S22_BackupCardRunningState : ScenarioBase
{
    protected override string ScenarioName => "S22-backup-card-running";

    [AvaloniaFact] public async Task Run_S22() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        var dest = new Destination { Name = "Local", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("d") };
        var backup = new Backup
        {
            Name = "Cloud backup",
            SourcePaths = { ctx.MakeSource("src", seed: 1, targetBytes: 100_000) },
            LastRunStatus = BackupRunStatus.Skipped,
            LastRunSummary = "Connection refused by destination",
            LastRunEndUtc = DateTime.UtcNow.AddDays(-1),
        };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });

        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(backup); });

        using var listVm = new BackupsViewModel();
        // Force the card into a running state — mirrors what
        // OnRunStarted does at runtime.
        if (listVm.Cards.Count > 0) listVm.Cards[0].SetRunning();

        var view = new BackupsView { DataContext = listVm };
        var host = new Avalonia.Controls.Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);
        ServiceLocator.Config.Update(cfg => cfg.Backups[0].Name = "Cloud backup — settings refreshed");
        ctx.Driver.Pump();
        Assert.Equal("Running…", listVm.Cards[0].LastRunLabel);
        Assert.Equal("running", listVm.Cards[0].StatusClass);
        ctx.Snapshot(host, "card running blue-badge stop-button",
            "After a real settings refresh, the card still says Running and offers Stop; yesterday’s skipped result has not replaced it. The engine state is simulated in this visual check.");
        listVm.Cards[0].SetQueuedReason("waiting for Photos to finish");
        ServiceLocator.Config.Update(cfg => cfg.Backups[0].Name = "Cloud backup — waiting for a destination");
        ctx.Driver.Pump();
        Assert.Equal("Queued: waiting for Photos to finish", listVm.Cards[0].LastRunLabel);
        ctx.Snapshot(host, "queued status survives settings refresh",
            "Queued and its reason survive the settings refresh too. This is a headless visual check with simulated activity.");
        host.Close();
        return Task.CompletedTask;
    }
}
