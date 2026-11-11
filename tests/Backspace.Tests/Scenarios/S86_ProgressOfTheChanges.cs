using System;
using System.Collections.Generic;
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
/// U144 (5 November 2026) on the card: local_c's shape (600,000 files, about
/// 9 GB of changes, most in one large file near the end). Before, restic's
/// percentage of the source said 91% when it reached the large file and then
/// waited for an hour. With the changes known, the bar follows the work.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S86_ProgressOfTheChanges : ScenarioBase
{
    protected override string ScenarioName => "S86-progress-of-the-changes";

    [AvaloniaFact] public async Task Run_S86() => await Run();

    private static BackupLiveProgress.Sample? Status(long bytesDone, long totalBytes, long filesDone, long totalFiles, string current)
        => BackupLiveProgress.FromResticStatus(JsonSerializer.SerializeToElement(new { message_type = "status",
            percent_done = bytesDone / (double)totalBytes, total_bytes = totalBytes.ToString(), bytes_done = bytesDone.ToString(),
            total_files = totalFiles.ToString(), files_done = filesDone.ToString(), current_files = new[] { current } }));

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var dest = new Destination { Name = "Google Drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("repo") };
        var drive = ctx.MakeSource("drive", targetBytes: 4096);
        var backup = new Backup { Name = "local_c", SourcePaths = { drive } };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        backup.SourceLastSizeBytes[drive] = 190_000_000_000;
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(backup); });

        var files = new List<ChangePlan.FileState>();
        var before = new Dictionary<ulong, (long, long)>();
        for (var i = 0; i < 600_000; i++)
        {
            var path = $"d{i / 1000:D4}/f{i % 1000:D4}";
            files.Add(new(path, 300_000, 1));
            before[ChangePlan.Hash(path)] = (300_000, 1);
        }
        files.Add(new("d0550/zz-large.vhdx", 9_000_000_000, 2));
        var plan = new ChangePlan(drive, files, before);

        using var main = new MainWindowViewModel();
        var host = ctx.Driver.Show(new MainWindow { Width = 1100, Height = 820, DataContext = main });
        await ctx.Driver.PumpAsync(200);
        var card = host.GetVisualDescendants().OfType<BackupCard>().Select(c => (BackupCardViewModel)c.DataContext!).Single();
        var progress = ServiceLocator.Progress;
        progress.RegisterRunCells(backup.Id, [drive], [dest.Name]);
        progress.ReportCellStarted(backup.Id, drive, dest.Name);
        long sent = 0;
        var live = new BackupLiveProgress(() => 190_000_000_000, () => sent, () => plan, () => null);
        var start = new DateTime(2026, 11, 5, 10, 34, 0, DateTimeKind.Utc);
        // U155: the card's "Running · 6 min" follows the simulated clock.
        var clock = start;
        card.UtcNow = () => clock;
        card.SetRunning(start);
        var total = plan.TotalBytes;
        long filesDone = 0, bytesDone = 0;
        async Task Until(int seconds, int from)
        {
            for (var t = from; t <= seconds; t += 5)
            {
                clock = start.AddSeconds(t);
                string current;
                if (filesDone < 551_000)
                {
                    filesDone = Math.Min(551_000, filesDone + 7_500);
                    bytesDone = filesDone * 300_000;
                    current = System.IO.Path.Combine(drive, $"d{filesDone / 1000:D4}", $"f{filesDone % 1000:D4}");
                }
                else
                {
                    bytesDone += 5 * 3_800_000L;
                    sent += 5 * 3_600_000L;
                    current = System.IO.Path.Combine(drive, "d0550", "zz-large.vhdx");
                }
                if (live.NextStatus(Status(bytesDone, total, filesDone, 600_001, current), start.AddSeconds(t)) is { } line)
                    progress.ReportLine(backup.Id, drive, dest.Name, line);
            }
            await ctx.Driver.PumpAsync(200);
        }
        await Until(365, 0);
        var checkedAll = card.Sources.Single().PercentComplete;
        Assert.InRange(checkedAll, 5, 40);
        Assert.Contains("0 B of ~8.4 GB changed files backed up", card.ProgressDetail);
        Assert.Contains("551,000 of 600,001 files checked (176 GB)", card.ProgressDetail);
        await ctx.Driver.PumpAsync(1_200); // the card's one-second refresh of the time running
        Assert.Equal("Running · 6 min", card.RunningLabel);
        ctx.Snapshot(host, "unchanged files checked",
            $"Six minutes in, the 551,000 unchanged files before the large one are checked: {checkedAll:0}%, not 91%. Nothing of the 8.4 GB of changes is backed up yet, and the time left counts them at the expected upload pace.");
        await Until(1_800, 370);
        var later = card.Sources.Single().PercentComplete;
        Assert.InRange(later, checkedAll + 25, 95);
        Assert.Contains("min left", card.ProgressDetail);
        await ctx.Driver.PumpAsync(1_200);
        var runningText = host.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Single(t => t.Name == "RunningText");
        Assert.Equal("Running · 30 min", runningText.Text);
        // U155: said as the actions, in the order the work happens.
        Assert.Matches(@"^Backing up · 551,000 of 600,001 files checked \(176 GB\) · [0-9.]+ of ~8\.4 GB changed files backed up · [0-9.]+ [KMG]?B of new data uploaded, [0-9.]+ MB/s · about .+ left$", card.ProgressDetail);
        // Hovering the line explains why the upload is smaller than the changes.
        var detailText = host.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Single(t => t.Text == card.ProgressDetail);
        Assert.True(detailText.IsEffectivelyVisible);
        Assert.EndsWith(BackupCardViewModel.UploadNote, Avalonia.Controls.ToolTip.GetTip(detailText) as string);
        ctx.Snapshot(host, "sending the changes",
            $"24 minutes into the large file at 3.8 MB/s: {later:0}%. The line under the bar reads \"{card.ProgressDetail}\": files checked, changed files backed up, then the new data uploaded, smaller because only data the backup doesn't already hold is sent, compressed (the hover note says so).");
        host.Close();
    }
}
