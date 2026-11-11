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
/// U123 and U124 (4 November 2026), on the real backup card. local_c showed
/// "about 791.54 GB" although its own rules leave out the Dropbox folder;
/// while it ran, the card showed 91% and "under a minute left" with most of
/// the drive still uncounted, at "622 MB/s" to Google Drive.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S81_SizeWithRulesAndHonestProgress : ScenarioBase
{
    protected override string ScenarioName => "S81-size-with-rules-and-honest-progress";

    [AvaloniaFact] public async Task Run_S81() => await Run();

    private static BackupLiveProgress.Sample? Status(double fraction, long done, long total, long filesDone, long files)
        => BackupLiveProgress.FromResticStatus(JsonSerializer.SerializeToElement(new { message_type = "status",
            percent_done = fraction, total_bytes = total.ToString(), bytes_done = done.ToString(),
            total_files = files.ToString(), files_done = filesDone.ToString() }));

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var dest = new Destination { Name = "Google Drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("repo") };
        // A source shaped like local_c: the person's documents and a large
        // Dropbox folder that the backup's imported rules leave out.
        var drive = Path.Combine(ctx.RootDir, "drive");
        Directory.CreateDirectory(Path.Combine(drive, "Users", "me", "Documents"));
        Directory.CreateDirectory(Path.Combine(drive, "Users", "me", "Dropbox"));
        File.WriteAllBytes(Path.Combine(drive, "Users", "me", "Documents", "report.pdf"), new byte[3_000_000]);
        File.WriteAllBytes(Path.Combine(drive, "Users", "me", "Dropbox", "holiday.mp4"), new byte[60_000_000]);
        var withRules = new Backup { Name = "local_c", SourcePaths = { drive }, ExcludeCloudFiles = true,
            // local_c's imported rule, converted once (U151) for this folder.
            FiltersText = Backspace.Services.DuplicacyFilterConverter.Convert("# already backed up and versioned (Dropbox)\ne:(?i)^Users/[^/]+/Dropbox/\n", new[] { drive }) };
        var noRules = new Backup { Name = "Same folder, no exclusion rules", SourcePaths = { drive } };
        foreach (var b in new[] { withRules, noRules }) b.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(withRules); cfg.Backups.Add(noRules); });

        using var main = new MainWindowViewModel();
        var host = ctx.Driver.Show(new MainWindow { Width = 1100, Height = 820, DataContext = main });
        await ctx.Driver.PumpAsync(200);
        var cards = host.GetVisualDescendants().OfType<BackupCard>().Select(c => (BackupCardViewModel)c.DataContext!).ToList();
        var ruled = cards.Single(c => c.Snapshot.Name == withRules.Name);
        var plain = cards.Single(c => c.Snapshot.Name == noRules.Name);
        Assert.True(ctx.Driver.WaitFor(() => ruled.Sources.Single().HasSize && plain.Sources.Single().HasSize, 20_000), "No size estimate appeared.");
        await ctx.Driver.PumpAsync(100);
        // Only the documents count; the excluded Dropbox folder doesn't.
        Assert.Equal("~2.9 MB", ruled.Sources.Single().SizeLabel);
        Assert.Equal("~60 MB", plain.Sources.Single().SizeLabel);
        Assert.Contains("its exclusions applied", ruled.Sources.Single().SizeTooltip);
        ctx.Snapshot(host, "size counts what the backup holds",
            $"local_c's rules leave out the Dropbox folder: its card shows {ruled.Sources.Single().SizeLabel}, the documents only. The same folder without rules shows {plain.Sources.Single().SizeLabel}.");

        // ---- U124: the numbers local_c's engine reported, while restic was still counting ----
        var progress = ServiceLocator.Progress;
        progress.RegisterRunCells(withRules.Id, [drive], [dest.Name]);
        progress.ReportCellStarted(withRules.Id, drive, dest.Name);
        ruled.SetRunning();
        var live = new BackupLiveProgress();
        var t = new DateTime(2026, 11, 4, 8, 9, 50, DateTimeKind.Utc);
        string? line = null;
        // Restic said 90.7% of the first 97 MB, then 67.8% of 28 GB found so far.
        foreach (var (pct, done, total, files, found) in new[] { (0.907, 88_000_000L, 97_000_000L, 614L, 683L), (0.678, 19_000_000_000, 28_000_000_000, 37_113, 62_564) })
        {
            line = live.NextStatus(Status(pct, done, total, files, found), t)!;
            progress.ReportLine(withRules.Id, drive, dest.Name, line);
            t = t.AddSeconds(5);
        }
        await ctx.Driver.PumpAsync(200);
        Assert.Equal(0, ruled.Sources.Single().PercentComplete);
        Assert.Equal("Backing up · 37,113 of 62,564+ files checked (18 GB)", ruled.ProgressDetail);
        ctx.Snapshot(host, "still counting no false percentage",
            "local_c's own numbers while the engine was still counting the drive: 0% and 'still counting' instead of 91% and 'under a minute left'; no speed made of files that needed nothing sent.");

        // With a size expected (the estimate counted with its rules, 190.8 GB on
        // the real local_c, or the last backup's), the percentage is of that.
        progress.RegisterRunCells(withRules.Id, [drive], [dest.Name]);
        progress.ReportCellStarted(withRules.Id, drive, dest.Name);
        long sent = 0;
        var expected = new BackupLiveProgress(expectedBytes: 190_800_000_000, sentBytes: () => sent);
        progress.ReportLine(withRules.Id, drive, dest.Name, expected.NextStatus(Status(0.678, 19_000_000_000, 28_000_000_000, 37_113, 62_564), t)!);
        sent = 55_000_000;
        progress.ReportLine(withRules.Id, drive, dest.Name, expected.NextStatus(Status(0.70, 19_300_000_000, 28_500_000_000, 37_900, 63_000), t.AddSeconds(5))!);
        await ctx.Driver.PumpAsync(200);
        Assert.Equal(10.1, ruled.Sources.Single().PercentComplete, 1);
        Assert.Equal("Backing up · 37,900 of 63,000+ files checked (18 of ~178 GB) · 52 MB of new data uploaded, 10 MB/s", ruled.ProgressDetail);
        ctx.Snapshot(host, "percentage of the expected size and real upload speed",
            "With the size expected known, the card shows 10% (18 of about 178 GB, as Windows counts them) and the speed of the data actually sent to the storage (10 MB/s), not of the files read.");
        host.Close();
    }
}
