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
        // One source, one revision (U162): its files at the drive root.
        // U166: restic's summary counts the 6,000 files, the listing counts the
        // same files (not the 331 folders), so the read ends at 100%; any
        // percentage before that only rises and stays at 99% at most.
        Assert.Contains("Reading the backup's folders: 100%, 6,000 files so far", said);
        var percents = said.Select(s => System.Text.RegularExpressions.Regex.Match(s, @": (\d+)%")).Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.Equal(percents.OrderBy(p => p), percents);
        Assert.All(percents.Take(percents.Count - 1), p => Assert.True(p <= 99));
        ctx.Report.RecordNote("Mount progress (U166)", string.Join("\n", said));
        var root = drive + @"\";
        Assert.Equal(30, Directory.GetDirectories(root).Length);
        Assert.Empty(Directory.GetFiles(root));

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
            // Only opened files are fetched (the owner, 7 November 2026): a
            // sibling is restored when it is opened, then read from the cache.
            var sibling = Path.Combine(root, "area 07", "topic 3", "note 06.txt");
            Assert.StartsWith("area 7 topic 3 note 6", File.ReadAllText(sibling));
            var again = Stopwatch.StartNew();
            Assert.StartsWith("area 7 topic 3 note 6", File.ReadAllText(sibling));
            var siblingRead = again.Elapsed;

            ctx.Report.RecordNote("Measured on this machine",
                $"Mount ready {ready.TotalSeconds:F1} s (folder list read before the drive appeared). " +
                $"First visit of 12 folders: max {firstVisits.Max():F0} ms; second visit: max {revisits.Max():F1} ms. " +
                $"Listing all 6,000 files in 330 folders: {walked.TotalMilliseconds:F0} ms. " +
                $"First file read {firstRead.TotalSeconds:F1} s; an opened file read again: {siblingRead.TotalMilliseconds:F0} ms.");
            Assert.True(ready < TimeSpan.FromSeconds(60), $"Mount took {ready}.");
            Assert.True(firstVisits.Max() < 500, $"First visit took {firstVisits.Max()} ms.");
            Assert.True(revisits.Max() < 100, $"A repeated listing took {revisits.Max()} ms.");
            Assert.True(walked < TimeSpan.FromSeconds(10), $"The whole walk took {walked}.");
            Assert.True(siblingRead < TimeSpan.FromMilliseconds(500), $"Reading an opened file again took {siblingRead}.");

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
            // The new snapshot does not appear in the drive either.
            Assert.Equal(30, Directory.GetDirectories(root).Length);
            Assert.False(Directory.Exists(Path.Combine(root, "All revisions")));

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

        await MountOneOlderRevisionAsync(ctx, fixture, backup, source, destination, watched, ct);
    }

    /// <summary>U162: the person picks one older revision. Only its tree is
    /// read (counted at the engine, which reports every tree it reads from the
    /// repository); afterwards walking, sizing and re-listing every folder
    /// reads nothing more and stays instant, and its files are that revision's.</summary>
    private static async Task MountOneOlderRevisionAsync(ScenarioContext ctx, RealEngineFixture fixture, Backup backup,
        string source, Destination destination, string watched, CancellationToken ct)
    {
        backup.KeepPolicy = "0:365 30:90 7:30 1:7"; // keep every snapshot from here on
        foreach (var version in new[] { "version three", "version four" })
        {
            File.WriteAllText(watched, version);
            Assert.Equal(BackupRunStatus.Success, (await fixture.Engine.BackupPruneCheckAsync(backup, source, destination, "default", ct)).Status);
        }
        var revisions = await ServiceLocator.Revisions.ListRevisionsAsync(backup, source, destination, "default", ct);
        Assert.Equal(3, revisions.Count);
        var oldest = revisions[^1];

        var treesRead = new List<string>();
        void Heard(string id) { lock (treesRead) treesRead.Add(id); }
        fixture.Engine.TreeListingStarted += Heard;
        var registry = new MountedSnapshotRegistry { OpenExplorer = false };
        try
        {
            var clock = Stopwatch.StartNew();
            var mounted = await registry.MountForAsync(backup,
                new MountSelection(new[] { new MountSelection.Source(source, new[] { oldest }) }), ct);
            var ready = clock.Elapsed;
            Assert.True(mounted.Succeeded, mounted.Message);
            var drive = mounted.DriveLetter! + @"\";
            Assert.Equal(MountSelection.When(oldest.CreatedUtc), registry.GetMountedDescription(backup.Id));
            Assert.Equal(new[] { oldest.SnapshotId! }, treesRead);

            // Everything Explorer, thumbnails, folder sizes and the indexer do.
            var walk = Stopwatch.StartNew();
            var entries = Directory.EnumerateFileSystemEntries(drive, "*", SearchOption.AllDirectories).ToList();
            var bytes = new DirectoryInfo(drive).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            foreach (var folder in Directory.EnumerateDirectories(drive, "*", SearchOption.AllDirectories))
                _ = File.GetAttributes(folder);
            var walked = walk.Elapsed;
            Assert.Equal(6000 + 330, entries.Count);
            Assert.True(bytes > 6000 * 200);
            var listings = new List<double>();
            foreach (var a in new[] { 2, 17, 28 })
            {
                var sw = Stopwatch.StartNew();
                Assert.Equal(20, Directory.GetFiles(Path.Combine(drive, $"area {a:D2}", "topic 4")).Length);
                listings.Add(sw.Elapsed.TotalMilliseconds);
            }
            Assert.False(Directory.Exists(Path.Combine(drive, "All revisions")));
            Assert.False(Directory.Exists(Path.Combine(drive, "Latest revision")));
            var attributes = File.GetAttributes(Path.Combine(drive, "area 07", "topic 3", "note 05.txt"));
            Assert.True(attributes.HasFlag(FileAttributes.ReadOnly) && attributes.HasFlag(FileAttributes.NotContentIndexed), attributes.ToString());
            // The oldest revision's bytes, fetched when opened.
            Assert.Equal("version two", File.ReadAllText(Path.Combine(drive, "area 07", "topic 3", "note 05.txt")));
            Assert.Equal(new[] { oldest.SnapshotId! }, treesRead);

            ctx.Report.RecordNote("One older revision (U162)",
                $"Mounted only the oldest of 3 revisions ({MountSelection.When(oldest.CreatedUtc)}) in {ready.TotalSeconds:F1} s. " +
                $"Trees read from the repository: {treesRead.Count} (snapshot {oldest.SnapshotId![..8]}), before and after walking all {entries.Count:N0} entries, " +
                $"summing every file's size and reading each folder's attributes ({walked.TotalMilliseconds:F0} ms). " +
                $"Folder listings afterwards: max {listings.Max():F1} ms. Its file read \"version two\"; the drive root holds the files, with no All revisions folder. " +
                $"Files are marked read-only and not to be indexed ({attributes}). " +
                $"The picker's details for this revision: {oldest.FileCount:N0} files, {oldest.TotalBytes:N0} bytes (0 = not recorded by the snapshot).");
            Assert.True(walked < TimeSpan.FromSeconds(10), $"The walk took {walked}.");
            Assert.True(listings.Max() < 100, $"A listing took {listings.Max()} ms.");
        }
        finally
        {
            fixture.Engine.TreeListingStarted -= Heard;
            await registry.DisposeAllAsync();
        }
    }
}
