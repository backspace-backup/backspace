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
/// U265 (owner, 26 November 2026): local_c showed a green check for a daily
/// backup last completed 9 days earlier. A backup whose last success is older
/// backup whose next scheduled time after its last run passed more than an
/// hour ago without a run has an orange tile, and the hover of Last backup
/// says which scheduled run did not happen. A backup that ran on time stays
/// green, and a paused schedule does not count as missed.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S111_MissedScheduleCard : ScenarioBase
{
    protected override string ScenarioName => "S111-missed-schedule-card";

    [AvaloniaFact] public async Task Run_S111() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var now = DateTime.UtcNow;
        var drive = new Destination { Name = "Backup drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("drive") };
        Backup Make(string name, double daysAgo, DateTime? pausedUntil = null)
        {
            var end = now.AddDays(-daysAgo);
            var b = new Backup { Name = name, SourcePaths = { ctx.MakeSource(name, targetBytes: 4096) },
                Schedule = new BackupSchedule { Frequency = ScheduleFrequency.Daily, TimeOfDay = TimeSpan.FromHours(20), PausedUntilUtc = pausedUntil },
                LastRunStatus = BackupRunStatus.Success, LastRunStartUtc = end.AddMinutes(-2), LastRunEndUtc = end,
                LastSuccessUtc = end, LastRunSummary = "Backup completed" };
            b.Targets.Add(new BackupTarget { DestinationId = drive.Id, StorageName = "default" });
            return b;
        }
        var missed = Make("local_c", 9);
        var onTime = Make("local_d", 0.02); // half an hour ago: no scheduled time has passed since
        var paused = Make("local_paused", 9, pausedUntil: now.AddDays(3));
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(drive);
            cfg.Backups.Add(missed); cfg.Backups.Add(onTime); cfg.Backups.Add(paused);
        });

        using var main = new MainWindowViewModel();
        var host = ctx.Driver.Show(new MainWindow { Width = 1300, Height = 900, DataContext = main });
        var backups = host.GetVisualDescendants().OfType<BackupsView>().Single();
        var vm = (BackupsViewModel)backups.DataContext!;
        BackupCardViewModel Card(Backup b) => vm.Cards.Single(c => c.Id == b.Id);
        await ctx.Driver.PumpAsync(300);

        Assert.Equal("warn", Card(missed).StatusClass);
        Assert.StartsWith("No backup since ", Card(missed).LastRunDetail);
        Assert.Contains(": the scheduled run of ", Card(missed).LastRunDetail);
        Assert.Contains("did not happen (daily at", Card(missed).LastRunDetail);
        // The banner agrees with the orange tile: not "All 3 backups are healthy".
        Assert.Equal("warn", vm.HealthClass);
        Assert.Equal("1 backup needs attention", vm.HealthHeadline);
        Assert.Equal("local_c", Assert.Single(vm.AffectedBackups).Name);
        // The negative cases: on time, and paused.
        Assert.Equal("ok", Card(onTime).StatusClass);
        Assert.DoesNotContain("did not happen", Card(onTime).LastRunDetail);
        Assert.Equal("ok", Card(paused).StatusClass);
        Assert.DoesNotContain("did not happen", Card(paused).LastRunDetail);
        ctx.Snapshot(host, "missed-schedule",
            "The banner says 1 backup needs attention with a local_c pill; local_c (daily, last backup 9 days ago) has an orange tile instead of a green check; local_d (ran today) stays green; local_paused (9 days, schedule paused) stays green.");
        ctx.Report.RecordNote("Hover of local_c's Last backup", Card(missed).LastRunDetail);
        // Waiting its turn behind another backup is not missed.
        Card(missed).SetRunning();
        Card(missed).SetQueuedReason("waiting for local_d");
        await ctx.Driver.PumpAsync(100);
        Assert.False(Card(missed).MissedSchedule);
        Assert.Empty(vm.AffectedBackups);
        ctx.Report.RecordNote("Limits", "Headless view with saved run states. The rule: the first scheduled time after the last completed backup, passed more than an hour ago without a run. A failed run is red and a skipped one orange at once; this rule is for runs that never happened.");
    }
}
