using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
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

    protected override async Task RunAsync(ScenarioContext ctx)
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

        // U114: no progress bar under "Running" (the sources and the strip
        // below carry the percentage); the icon turns in steps.
        var summary = host.GetVisualDescendants().OfType<Border>().First(b => b.Name == "BackupSummary");
        Assert.DoesNotContain(summary.GetVisualDescendants().OfType<ProgressBar>(), bar => bar.IsEffectivelyVisible);
        var icon = summary.GetVisualDescendants().OfType<PathIcon>().First(i => i.Classes.Contains("status-icon"));
        Assert.True(RunningPulse.IsTicking);
        RunningPulse.Restart();
        var seenAngles = new System.Collections.Generic.HashSet<double>();
        try
        {
            for (var i = 0; i < 12; i++)
            {
                RunningPulse.Advance();
                seenAngles.Add(Assert.IsType<RotateTransform>(icon.RenderTransform).Angle);
            }
        }
        finally { RunningPulse.Restart(); }
        // One full turn in twelve steps.
        Assert.Equal(12, seenAngles.Count);
        // U155: the time the run has taken so far ticks next to "Running",
        // from the run's start, and survives the settings refresh.
        var card = listVm.Cards[0];
        var startedAt = card.RunStartedUtc!.Value;
        card.UtcNow = () => startedAt.AddSeconds(40);
        card.SetRunning(); // a later progress update keeps the run's start
        Assert.Equal(startedAt, card.RunStartedUtc);
        var running = summary.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "RunningText");
        ctx.Driver.Pump();
        Assert.Equal("Running · 40 s", running.Text);
        card.UtcNow = () => startedAt.AddMinutes(12).AddSeconds(59);
        await ctx.Driver.PumpAsync(1_500); // the card's one-second refresh
        Assert.Equal("Running · 12 min", running.Text);
        Assert.Equal("1 h 05 min", BackupCardViewModel.Elapsed(TimeSpan.FromMinutes(65.5)));
        Assert.Equal("0 s", BackupCardViewModel.Elapsed(TimeSpan.FromSeconds(-2)));
        ctx.Snapshot(host, "card running blue-badge stop-button",
            "After a real settings refresh, the card still says Running, with the time the run has taken so far (12 min), and offers Stop; yesterday’s skipped result has not replaced it. The engine state is simulated in this visual check.");
        listVm.Cards[0].SetQueuedReason("waiting for Photos to finish");
        ServiceLocator.Config.Update(cfg => cfg.Backups[0].Name = "Cloud backup — waiting for a destination");
        ctx.Driver.Pump();
        Assert.Equal("Queued", listVm.Cards[0].LastRunLabel);
        Assert.Equal("Queued: waiting for Photos to finish", listVm.Cards[0].LastRunDetail);
        // Waiting in line is not running: nothing moves.
        summary = host.GetVisualDescendants().OfType<Border>().First(b => b.Name == "BackupSummary");
        icon = summary.GetVisualDescendants().OfType<PathIcon>().First(i => i.Classes.Contains("status-icon"));
        RunningPulse.Advance();
        Assert.Null(icon.RenderTransform);
        Assert.False(summary.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "RunningText").IsEffectivelyVisible);
        ctx.Snapshot(host, "queued status survives settings refresh",
            "Queued and its reason survive the settings refresh too. This is a headless visual check with simulated activity.");
        host.Close();
    }
}
