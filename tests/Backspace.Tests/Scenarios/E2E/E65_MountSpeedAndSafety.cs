using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.WebDav;
using Backspace.Services.WinFsp;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// U158 and U159 with the bundled engines and a real WinFsp drive.
/// <list type="bullet">
/// <item>Speed: the snapshot's folders are read before the drive appears;
/// afterwards every folder lists from memory, and a file's folder comes in
/// one restore.</item>
/// <item>Safety: while the drive is mounted, a backup into the same
/// destination adds its snapshot but skips cleanup with the reason; a
/// stand-alone cleanup is skipped; removing a snapshot is refused. The drive
/// keeps showing the snapshot it opened. After unmounting, cleanup runs.</item>
/// <item>Exit: unmounting everything (what Quit does) removes the drive.</item>
/// </list>
/// Thresholds are generous for a loaded test machine; the measured figures
/// are in the report.
/// </summary>
[Collection(ScenarioCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class E65_MountSpeedAndSafety : BackupE2EBase
{
    public E65_MountSpeedAndSafety(ITestOutputHelper output) : base(output) { }
    protected override string ScenarioName => "E65-mount-speed-and-safety";
    [AvaloniaFact] public async Task Run_E65() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        if (!WinFspRuntimeDetector.IsInstalled()) { ctx.Skip("WinFsp is not installed on this machine."); return; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = deadline.Token;

        // 6,000 files in 330 folders, three levels deep.
        var source = Path.Combine(ctx.SourcesDir, "Documents");
        for (var a = 0; a < 30; a++)
            for (var b = 0; b < 10; b++)
            {
                var folder = Path.Combine(source, $"area {a:D2}", $"topic {b}");
                Directory.CreateDirectory(folder);
                for (var f = 0; f < 20; f++)
                    File.WriteAllText(Path.Combine(folder, $"note {f:D2}.txt"), $"area {a} topic {b} note {f} " + new string('x', 200 + f));
            }
        var watched = Path.Combine(source, "area 07", "topic 3", "note 05.txt");
        File.WriteAllText(watched, "version one");

        var destination = new Destination { Name = "Mount test drive", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("repository"), Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e64", Guid.NewGuid().ToString("N")) };
        var backup = new Backup { Name = "Mounted documents", SourcePaths = { source }, UseVss = false, VssDisabledAcknowledged = true,
            KeepPolicy = "1", CheckSchedule = new BackupSchedule(),
            Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } } };
        ServiceLocator.Config.Update(c => { c.Destinations.Add(destination); c.Backups.Add(backup); });
        var first = await fixture.Engine.BackupPruneCheckAsync(backup, source, destination, "default", ct);
        Assert.Equal(BackupRunStatus.Success, first.Status);

        // ---- mount, with the progress the card shows ----
        var registry = new MountedSnapshotRegistry { OpenExplorer = false };
        var said = new List<string>();
        registry.Changed += id => { if (registry.GetMountProgress(id) is { } text) lock (said) if (said.LastOrDefault() != text) said.Add(text); };
        var clock = Stopwatch.StartNew();
        var mounted = await registry.MountForAsync(backup, ct);
        var ready = clock.Elapsed;
        Assert.True(mounted.Succeeded, mounted.Message);
        var drive = mounted.DriveLetter!;
        Assert.Equal(drive, registry.GetMountedDrive(backup.Id));
        Assert.Null(registry.GetMountProgress(backup.Id));
        Assert.Contains("Starting", said);
        Assert.Contains(said, s => s.StartsWith("Reading the backup's folders", StringComparison.Ordinal));
        var root = Path.Combine(drive + @"\", "Latest revision", "Documents");

        try
        {
            // ---- speed ----
            var firstVisits = new List<double>();
            var revisits = new List<double>();
            foreach (var a in new[] { 0, 11, 23, 29 })
                foreach (var b in new[] { 1, 6, 9 })
                {
                    var folder = Path.Combine(root, $"area {a:D2}", $"topic {b}");
                    var sw = Stopwatch.StartNew();
                    Assert.Equal(20, Directory.GetFiles(folder).Length);
                    firstVisits.Add(sw.Elapsed.TotalMilliseconds);
                    sw.Restart();
                    Assert.Equal(20, Directory.GetFiles(folder).Length);
                    revisits.Add(sw.Elapsed.TotalMilliseconds);
                }
            var walk = Stopwatch.StartNew();
            var everything = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count();
            var walked = walk.Elapsed;
            Assert.Equal(6000, everything);
            var read = Stopwatch.StartNew();
            Assert.Equal("version one", File.ReadAllText(Path.Combine(root, "area 07", "topic 3", "note 05.txt")));
            var firstRead = read.Elapsed;
            // Its folder arrives in one background restore.
            var sibling = Path.Combine(root, "area 07", "topic 3", "note 06.txt");
            var siblingWait = Stopwatch.StartNew();
            string? siblingText = null;
            while (siblingWait.Elapsed < TimeSpan.FromSeconds(60))
            {
                var once = Stopwatch.StartNew();
                siblingText = File.ReadAllText(sibling);
                if (once.Elapsed < TimeSpan.FromMilliseconds(200)) break;
            }
            var lastSibling = Stopwatch.StartNew();
            Assert.StartsWith("area 7 topic 3 note 18", File.ReadAllText(Path.Combine(root, "area 07", "topic 3", "note 18.txt")));
            var siblingRead = lastSibling.Elapsed;
            Assert.StartsWith("area 7 topic 3 note 6", siblingText);

            ctx.Report.RecordNote("Measured on this machine",
                $"Mount ready {ready.TotalSeconds:F1} s (folder list read before the drive appeared). " +
                $"First visit of 12 folders: max {firstVisits.Max():F0} ms; second visit: max {revisits.Max():F1} ms. " +
                $"Listing all 6,000 files in 330 folders: {walked.TotalMilliseconds:F0} ms. " +
                $"First file read {firstRead.TotalSeconds:F1} s; a file of the same folder after its background restore: {siblingRead.TotalMilliseconds:F0} ms.");
            Assert.True(ready < TimeSpan.FromSeconds(60), $"Mount took {ready}.");
            Assert.True(firstVisits.Max() < 500, $"First visit took {firstVisits.Max()} ms.");
            Assert.True(revisits.Max() < 100, $"A repeated listing took {revisits.Max()} ms.");
            Assert.True(walked < TimeSpan.FromSeconds(10), $"The whole walk took {walked}.");
            Assert.True(siblingRead < TimeSpan.FromMilliseconds(500), $"A prefetched file took {siblingRead}.");

            // ---- safety while mounted ----
            File.WriteAllText(watched, "version two");
            var during = await fixture.Engine.BackupPruneCheckAsync(backup, source, destination, "default", ct);
            Assert.Equal(BackupRunStatus.Success, during.Status);
            Assert.Contains("Cleanup waits: a snapshot of this backup is mounted as a drive.", during.Summary);
            Assert.Equal(0, during.SnapshotsRemoved);
            var revisions = await ServiceLocator.Revisions.ListRevisionsAsync(backup, source, destination, "default", ct);
            Assert.Equal(2, revisions.Count); // "keep the latest" was not applied
            // The drive still shows the snapshot it opened.
            Assert.Equal("version one", File.ReadAllText(Path.Combine(root, "area 07", "topic 3", "note 05.txt")));
            Assert.Single(Directory.GetDirectories(Path.Combine(drive + @"\", "All revisions")));
            Assert.Equal("All revisions\\Documents (1 revision)", Path.GetRelativePath(drive + @"\", Directory.GetDirectories(Path.Combine(drive + @"\", "All revisions"))[0]));

            var prune = await fixture.Engine.RunPruneOnlyAsync(backup, source, destination, "default", ct);
            Assert.Equal(BackupRunStatus.Skipped, prune.Status);
            Assert.StartsWith("Cleanup waits: a snapshot of this backup is mounted as a drive.", prune.Summary);
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Engine.PruneSnapshotIdAsync(
                backup, source, destination, "default", revisions[^1].SnapshotId!, ct));
            Assert.StartsWith("Removing a snapshot has to wait: a snapshot of this backup is mounted as a drive.", refused.Message);
            var stillThere = await ServiceLocator.Revisions.ListRevisionsAsync(backup, source, destination, "default", ct);
            Assert.Equal(2, stillThere.Count);
            ctx.Report.RecordNote("While mounted",
                $"A backup into the same destination succeeded and said: \"{during.Summary}\". The mounted drive still read \"version one\" from the snapshot it opened. " +
                $"Stand-alone cleanup: {prune.Status}, \"{prune.Summary}\". Removing a snapshot was refused: \"{refused.Message}\". Both snapshots remain.");
        }
        finally
        {
            // What Quit and every other exit do.
            await registry.DisposeAllAsync();
        }
        Assert.False(Directory.Exists(drive + @"\"));
        Assert.Null(registry.GetMountedDrive(backup.Id));

        var after = await fixture.Engine.RunPruneOnlyAsync(backup, source, destination, "default", ct);
        Assert.Equal(BackupRunStatus.Success, after.Status);
        Assert.Single(await ServiceLocator.Revisions.ListRevisionsAsync(backup, source, destination, "default", ct));
        ctx.Report.RecordNote("After unmounting",
            $"Unmounting everything removed {drive}. The cleanup then ran ({after.Status}) and applied \"keep the latest\", leaving one snapshot.");
    }
}
