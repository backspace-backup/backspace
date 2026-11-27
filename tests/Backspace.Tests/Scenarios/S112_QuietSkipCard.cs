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
/// U269 (owner, 27 November 2026: "The goal was to not annoy user with
/// warnings if an external drive is not plugged in (up until the grace
/// period)"). usb_photos' last run was skipped because its drive was not
/// plugged in, within its 7-day quiet period: its tile stays green with its
/// last backup, the banner says all is well. usb_archive's quiet period has
/// ended: orange as a skip. local_c failed: red at once.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S112_QuietSkipCard : ScenarioBase
{
    protected override string ScenarioName => "S112-quiet-skip-card";

    [AvaloniaFact] public async Task Run_S112() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var now = DateTime.UtcNow;
        var drive = new Destination { Name = "Photos drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("drive") };
        Backup Make(string name, double lastSuccessDaysAgo, BackupRunStatus last, string summary)
        {
            // Daily at the next full hour: the skip ran at its time, none was missed.
            var next = DateTime.Now.AddHours(1);
            var b = new Backup { Name = name, SourcePaths = { ctx.MakeSource(name, targetBytes: 4096) },
                Schedule = new BackupSchedule { Frequency = ScheduleFrequency.Daily, TimeOfDay = new TimeSpan(next.Hour, 0, 0) },
                CreatedUtc = now.AddDays(-60), LastSuccessUtc = now.AddDays(-lastSuccessDaysAgo), SkipNotificationGraceDays = 7,
                LastRunStatus = last, LastRunStartUtc = now.AddHours(-3).AddMinutes(-1), LastRunEndUtc = now.AddHours(-3), LastRunSummary = summary };
            b.Targets.Add(new BackupTarget { DestinationId = drive.Id, StorageName = "default" });
            return b;
        }
        const string unplugged = "Skipped: the drive «Photos» (E:) is not plugged in";
        var quiet = Make("usb_photos", 2, BackupRunStatus.Skipped, unplugged);
        var expired = Make("usb_archive", 9, BackupRunStatus.Skipped, unplugged);
        var failed = Make("local_c", 1, BackupRunStatus.Failed, "The backup could not write to the destination");
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(drive);
            cfg.Backups.Add(quiet); cfg.Backups.Add(expired); cfg.Backups.Add(failed);
        });

        using var main = new MainWindowViewModel();
        var host = ctx.Driver.Show(new MainWindow { Width = 1300, Height = 900, DataContext = main });
        var backups = host.GetVisualDescendants().OfType<BackupsView>().Single();
        var vm = (BackupsViewModel)backups.DataContext!;
        BackupCardViewModel Card(Backup b) => vm.Cards.Single(c => c.Id == b.Id);
        await ctx.Driver.PumpAsync(300);

        Assert.Equal("ok", Card(quiet).StatusClass);
        Assert.True(Card(quiet).QuietSkip);
        Assert.StartsWith("2d ago", Card(quiet).LastRunLabel);
        Assert.Contains("is not plugged in. Not flagged until", Card(quiet).LastRunDetail);
        Assert.Equal("skipped", Card(expired).StatusClass);
        Assert.Equal("fail", Card(failed).StatusClass);
        // The banner names the two that need attention, not usb_photos.
        Assert.DoesNotContain(vm.AffectedBackups, a => a.Name == "usb_photos");
        Assert.Contains(vm.AffectedBackups, a => a.Name == "usb_archive");
        Assert.Contains(vm.AffectedBackups, a => a.Name == "local_c");
        ctx.Snapshot(host, "quiet-skip",
            "usb_photos (drive not plugged in 3 hours ago, last backup 2 days ago, within its 7-day quiet period) has a green tile; usb_archive (skipped the same way, last backup 9 days ago, quiet period over) is orange; local_c (failed) is red. The banner's pills name usb_archive and local_c only.");
        ctx.Report.RecordNote("Hover of usb_photos' Last backup", Card(quiet).LastRunDetail);
        ctx.Report.RecordNote("Limits", "Headless view with saved run states (as the background service records them). The tray follows the same rule (QuietSkipTests).");
    }
}
