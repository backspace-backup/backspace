using System;
using System.IO;
using System.Linq;
using System.Text.Json;
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
/// U140 and U141 (5 November 2026), local_c's card while it ran: "about 2 min
/// left" while sending a large changed file at 3.8 MB/s with 18 GB to go, and
/// "C:\ (179 GB)" (the last backup's size) above "172 / 190 GB" (this run's).
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S85_RunningSizeAndTimeLeft : ScenarioBase
{
    protected override string ScenarioName => "S85-running-size-and-time-left";

    [AvaloniaFact] public async Task Run_S85() => await Run();

    private static JsonElement Status(double fraction, long done, long total, long filesDone, long files)
        => JsonSerializer.SerializeToElement(new { lastStatus = new { status = new {
            percentDone = fraction, totalBytes = total.ToString(), bytesDone = done.ToString(),
            totalFiles = files.ToString(), filesDone = filesDone.ToString() } } });

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var dest = new Destination { Name = "Google Drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("repo") };
        var drive = ctx.MakeSource("drive", targetBytes: 4096);
        var backup = new Backup { Name = "local_c", SourcePaths = { drive } };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        backup.SourceLastSizeBytes[drive] = 179_000_000_000; // as the last backup measured it
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(backup); });

        using var main = new MainWindowViewModel();
        var host = ctx.Driver.Show(new MainWindow { Width = 1100, Height = 820, DataContext = main });
        await ctx.Driver.PumpAsync(200);
        var card = host.GetVisualDescendants().OfType<BackupCard>().Select(c => (BackupCardViewModel)c.DataContext!).Single();
        var source = card.Sources.Single();
        Assert.Equal("167 GB", source.SizeLabel); // 179 billion bytes, as Windows counts them

        var progress = ServiceLocator.Progress;
        progress.RegisterRunCells(backup.Id, [drive], [dest.Name]);
        progress.ReportCellStarted(backup.Id, drive, dest.Name);
        card.SetRunning();
        long sent = 0;
        var live = new BackupLiveProgress(expectedBytes: 179_000_000_000, sentBytes: () => sent);
        var start = new DateTime(2026, 11, 5, 10, 34, 0, DateTimeKind.Utc);
        const long total = 204_000_000_000; // 190 GB as Windows counts them
        string? line = null;
        for (var t = 0; t <= 440; t += 5)
        {
            // Unchanged files at 400 MB/s, then a large changed file sent at 3.8 MB/s.
            long done = t <= 360 ? 40_000_000_000 + t * 400_000_000L : 184_000_000_000 + (t - 360) * 3_800_000L;
            if (t > 360) sent += 19_000_000;
            line = live.Next(Status(done / (double)total, done, total, 500_000, 601_922), start.AddSeconds(t)) ?? line;
            progress.ReportLine(backup.Id, drive, dest.Name, line!);
        }
        await ctx.Driver.PumpAsync(200);
        Assert.Equal("190 GB", source.SizeLabel);
        Assert.Equal("Size counted by the backup in progress.", source.SizeTooltip);
        Assert.Contains("172 / 190 GB", card.ProgressDetail);
        Assert.Contains("about 1 h", card.ProgressDetail);
        ctx.Snapshot(host, "counted size and time left after a slowdown",
            "local_c 80 s after reaching a large changed file sent at 3.8 MB/s: the source shows 190 GB, the size this run counted (it said 179 GB, the last backup's, before), and the time left is over an hour, not 2 min.");
        host.Close();
    }
}
