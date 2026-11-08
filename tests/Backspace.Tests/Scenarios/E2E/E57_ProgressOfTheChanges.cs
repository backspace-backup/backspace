using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// U144 with the real engine: after a first backup, Backspace keeps the
/// source's listing; the next backup compares the source with it, and its
/// progress line counts the changes ("changed files: x of ~y backed up") from restic's own
/// current files and bytes, not the source's size.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E57_ProgressOfTheChanges : BackupE2EBase
{
    public E57_ProgressOfTheChanges(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E57-progress-of-the-changes";

    [AvaloniaFact] public async Task Run_E57() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var source = ctx.MakeSource("docs", seed: 561, targetBytes: 3_000_000);
        var dest = new Destination
        {
            Name = "Local — changes", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("changes"),
            Encrypted = true, StoragePasswordRef = ServiceLocator.Secrets.PutNew("e57", $"e57-{Guid.NewGuid():N}"),
        };
        var backup = new Backup
        {
            Name = "Docs (E57)", SourcePaths = { source }, UseVss = false, VssDisabledAcknowledged = true,
            CheckSchedule = new BackupSchedule(), PruneSchedule = new BackupSchedule(), Threads = 2,
            Targets = { new BackupTarget { DestinationId = dest.Id, StorageName = "default" } },
        };
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(backup); });
        var view = new BackupsView { DataContext = new BackupsViewModel() };
        var host = new Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);

        var lists = Path.Combine(AppPaths.ConfigRoot, "change-lists");
        var earlier = Directory.Exists(lists) ? Directory.GetFiles(lists).Length : 0;
        var first = await RunBackupViaUiAsync(ctx, backup, view, CancellationToken.None);
        Assert.Equal(BackupRunStatus.Success, first.Status);
        var saved = Directory.GetFiles(lists);
        Assert.True(saved.Length == earlier + 1 && saved.All(f => f.EndsWith(".bin")), string.Join(" ; ", saved)); // this backup's listing, for the next one

        // A large new file, so restic spends several progress steps on it.
        var large = Path.Combine(source, "zz-large.bin");
        var block = new byte[4 << 20];
        new Random(56).NextBytes(block);
        await using (var file = File.Create(large))
            for (var i = 0; i < 100; i++) { block[i] ^= 1; await file.WriteAsync(block); } // 400 MiB, little dedup
        var interval = BackupLiveProgress.Interval;
        BackupLiveProgress.Interval = TimeSpan.FromMilliseconds(500);
        try
        {
            var second = await RunBackupViaUiAsync(ctx, backup, view, CancellationToken.None);
            Assert.Equal(BackupRunStatus.Success, second.Status);
            // U142: the run records what it added, from restic's summary.
            Assert.Equal(1, second.FilesNew);
            Assert.True(second.BytesUploaded > 0, second.BytesUploaded.ToString()); // compressed and deduplicated: less than the 400 MiB read
            var log = File.ReadAllLines(second.Cells.Single().LogPath);
            var progress = log.Where(l => l.StartsWith(BackupLiveProgress.Prefix, StringComparison.Ordinal)).ToArray();
            ctx.Report.RecordNote("Progress lines of the second backup", string.Join(Environment.NewLine, progress));
            Assert.Contains(progress, l => l.Contains("changed files: ") && l.Contains(" of ~400 MB backed up"));
            // Mid-file: some of the 400 MiB counted as backed up, not all.
            Assert.Contains(progress, l => l.Contains("changed files: ") && !l.Contains("changed files: 0 B") && !l.Contains("changed files: 400 of ~400 MB"));
            // U145: restic runs with --no-scan and reports no totals; the line
            // takes the source's from Backspace's listing, never "of 0".
            Assert.Contains(progress, l => l.Contains(" files checked (~"));
            Assert.DoesNotContain(progress, l => l.Contains(" of 0 files") || l.Contains("of 0 B"));
            Assert.Contains(log, l => l.StartsWith("[recap] Backup complete: ", StringComparison.Ordinal) && !l.StartsWith("[recap] Backup complete: 0 files", StringComparison.Ordinal));
        }
        finally { BackupLiveProgress.Interval = interval; }

        // U143: what the second backup added, from its snapshot and the one before.
        var runs = ServiceLocator.Logs.LoadHistory(backup.Name);
        var latestRun = runs.OrderByDescending(r => r.StartedUtc).First();
        var cell = latestRun.Cells.Single();
        var snapshot = RunChanges.SnapshotOf(latestRun, cell);
        Assert.False(string.IsNullOrEmpty(snapshot), "No snapshot in the recap of " + cell.LogPath);
        var changes = await RunChanges.LoadAsync(fixture.Engine, backup, source, dest, "default", snapshot!, null, CancellationToken.None);
        Assert.NotNull(changes);
        Assert.NotNull(changes!.PreviousId);
        var added = Assert.Single(changes.Files);
        Assert.EndsWith("zz-large.bin", added.Path);
        Assert.True(added.IsNew);
        Assert.Equal(400L << 20, added.Size);
        File.Delete(large);
    }
}
