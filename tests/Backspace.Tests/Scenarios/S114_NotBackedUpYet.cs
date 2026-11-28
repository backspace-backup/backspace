using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// U257 (owner, 28 November 2026): "shouldn't Last backup show a warning right
/// away if the backup never ran (regardless of the grace period), or maybe 24h
/// later? As zero backup means zero security, unlike a delayed backup within
/// the grace period." A backup made two hours ago and never run stays neutral;
/// one made three days ago, its drive never plugged in and its grace period
/// not over, is orange, and the banner names it.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S114_NotBackedUpYet : ScenarioBase
{
    protected override string ScenarioName => "S114-not-backed-up-yet";

    [AvaloniaFact] public async Task Run_S114() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var now = DateTime.UtcNow;
        var drive = new Destination { Name = "My Passport", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("passport") };
        Backup Make(string name, TimeSpan age, bool skipped)
        {
            var b = new Backup { Name = name, SourcePaths = { ctx.MakeSource(name, targetBytes: 4096) }, Schedule = TestSchedules.DueInAnHour(),
                CreatedUtc = now - age, SkipNotificationGraceDays = 180 };
            if (skipped)
            {
                b.LastRunStatus = BackupRunStatus.Skipped;
                b.LastRunStartUtc = now.AddHours(-20);
                b.LastRunEndUtc = now.AddHours(-20);
                b.LastRunSummary = "The drive «My Passport» is not plugged in";
            }
            b.Targets.Add(new BackupTarget { DestinationId = drive.Id, StorageName = "default" });
            return b;
        }
        var fresh = Make("new_backup", TimeSpan.FromHours(2), skipped: false);
        var forgotten = Make("local_dropbox", TimeSpan.FromDays(3), skipped: true);
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(drive); cfg.Backups.Add(fresh); cfg.Backups.Add(forgotten); });

        using var main = new MainWindowViewModel();
        var host = ctx.Driver.Show(new MainWindow { Width = 1300, Height = 900, DataContext = main });
        var backups = host.GetVisualDescendants().OfType<BackupsView>().Single();
        var vm = (BackupsViewModel)backups.DataContext!;
        await Task.WhenAll(vm.Cards.Select(c => c.CheckHistoryRead)).WaitAsync(TimeSpan.FromSeconds(10));
        await ctx.Driver.PumpAsync(300);

        var freshCard = vm.Cards.Single(c => c.Id == fresh.Id);
        var forgottenCard = vm.Cards.Single(c => c.Id == forgotten.Id);
        Assert.NotEqual("warn", freshCard.StatusClass);
        Assert.Equal("warn", forgottenCard.StatusClass);
        Assert.StartsWith("Not backed up yet: this backup was made", forgottenCard.LastRunDetail);
        Assert.False(forgottenCard.ShowRunWarnLogs);
        Assert.Equal("1 backup needs attention", vm.HealthHeadline);
        ctx.Snapshot(host, "not-backed-up-yet",
            $"new_backup (made 2 hours ago, never run): neutral. local_dropbox (made 3 days ago, its drive never plugged in, 180-day grace period): orange \"Not backed up yet\", no View logs; the banner says \"1 backup needs attention\" and names it. Hover: \"{forgottenCard.LastRunDetail?.Replace("\n", " / ")}\".");
    }
}
