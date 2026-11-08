using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
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
        // U166: restic's summary counts the 6,000 files, the listing counts the
        // same files (not the 331 folders), so the read ends at 100%; any
        // percentage before that only rises and stays at 99% at most.
        Assert.Contains("Reading the backup's folders: 100%, 6,000 files so far", said);
        var percents = said.Select(s => System.Text.RegularExpressions.Regex.Match(s, @": (\d+)%")).Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.Equal(percents.OrderBy(p => p), percents);
        Assert.All(percents.Take(percents.Count - 1), p => Assert.True(p <= 99));
        ctx.Report.RecordNote("Mount progress (U166)", string.Join("\n", said));
        // One source, one revision (U162): its files at the drive root.
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
        await CopyALargeFileOutAsync(ctx, fixture, ct);
    }

    /// <summary>U167: copying a large file out of the drive behaves like a
    /// slow network share. A download limit on the backup stands in for a
    /// cloud destination (restic reads the repository at that rate). One
    /// <c>CopyFileEx</c>, the call Windows' own copy engine is built on,
    /// copies a 512 MiB file while a second thread keeps listing folders and
    /// opening files on the drive, as Explorer's window does. Explorer itself
    /// cannot be driven here.</summary>
    private async Task CopyALargeFileOutAsync(ScenarioContext ctx, RealEngineFixture fixture, CancellationToken ct)
    {
        const long size = 512L << 20;
        const int limitKBps = 40_000;
        var source = Path.Combine(ctx.SourcesDir, "Large");
        Directory.CreateDirectory(source);
        var large = Path.Combine(source, "film.bin");
        using (var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256))
        await using (var file = new FileStream(large, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var random = new Random(167);
            var chunk = new byte[1 << 20];
            for (long written = 0; written < size; written += chunk.Length)
            {
                random.NextBytes(chunk); // incompressible, as a film is
                await file.WriteAsync(chunk, ct);
            }
        }
        const int folders = 400;
        for (var i = 0; i < folders; i++)
        {
            var folder = Path.Combine(source, "folders", $"folder {i:D3}");
            Directory.CreateDirectory(folder);
            for (var f = 0; f < 3; f++) File.WriteAllText(Path.Combine(folder, $"file {f}.txt"), $"{i} {f}");
        }
        var destination = new Destination { Name = "Slow drive", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("slow repository"), Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("u167", Guid.NewGuid().ToString("N")) };
        var backup = new Backup { Name = "Large file", SourcePaths = { source }, UseVss = false, VssDisabledAcknowledged = true,
            CheckSchedule = new BackupSchedule(),
            Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } } };
        ServiceLocator.Config.Update(c => { c.Destinations.Add(destination); c.Backups.Add(backup); });
        Assert.Equal(BackupRunStatus.Success, (await fixture.Engine.BackupPruneCheckAsync(backup, source, destination, "default", ct)).Status);
        backup.BandwidthLimitKBps = limitKBps; // reading the repository now runs at about 40 MB/s

        var registry = new MountedSnapshotRegistry { OpenExplorer = false };
        try
        {
            var mounted = await registry.MountForAsync(backup, ct);
            Assert.True(mounted.Succeeded, mounted.Message);
            var drive = mounted.DriveLetter! + @"\";
            var target = Path.Combine(ctx.MakeRestoreTarget("copied"), "film.bin");

            // Explorer's window: list a folder it has not shown yet, and open
            // a small file and the file being copied, every 25 ms.
            using var copying = new CancellationTokenSource();
            var answers = new List<double>();
            Exception? listerFailure = null;
            var (listings, smallOpens, largeOpens) = (new List<double>(), new List<double>(), new List<double>());
            var lister = new Thread(() =>
            {
                var small = Path.Combine(drive, "folders", "folder 000", "file 0.txt");
                for (var i = 0; !copying.IsCancellationRequested && listerFailure is null; i++)
                try
                {
                    var sw = Stopwatch.StartNew();
                    Assert.Equal(3, Directory.GetFiles(Path.Combine(drive, "folders", $"folder {i % folders:D3}")).Length);
                    var listed = sw.Elapsed.TotalMilliseconds;
                    _ = File.GetAttributes(small);
                    var opened = sw.Elapsed.TotalMilliseconds;
                    Assert.Equal(size, new FileInfo(Path.Combine(drive, "film.bin")).Length);
                    var ms = sw.Elapsed.TotalMilliseconds;
                    lock (answers)
                    {
                        answers.Add(ms);
                        listings.Add(listed);
                        smallOpens.Add(opened - listed);
                        largeOpens.Add(ms - opened);
                    }
                    Thread.Sleep(25);
                }
                catch (Exception ex) { listerFailure = ex; }
            }) { IsBackground = true, Name = "U167 lister" };

            long firstBytes = -1, progressCalls = 0;
            var clock = Stopwatch.StartNew();
            uint Progress(long total, long done, long streamTotal, long streamDone, uint stream, uint reason, IntPtr a, IntPtr b, IntPtr data)
            {
                if (done > 0) { Interlocked.CompareExchange(ref firstBytes, clock.ElapsedMilliseconds, -1); Interlocked.Increment(ref progressCalls); }
                return 0; // PROGRESS_CONTINUE
            }
            var downloads = new List<string>();
            void Downloading(string path) { lock (downloads) downloads.Add(path); }
            fixture.Engine.FileDumpStarted += Downloading;
            lister.Start();
            var copy = Task.Run(() => CopyFileEx(Path.Combine(drive, "film.bin"), target, Progress, IntPtr.Zero, IntPtr.Zero, 0)
                ? 0 : Marshal.GetLastWin32Error(), ct);
            // A second program reads the same file meanwhile: it shares the download.
            // (Reading far ahead of the download would wait for it to get there.)
            await Task.Delay(1500, ct);
            var head = new byte[1 << 16];
            await using (var second = new FileStream(Path.Combine(drive, "film.bin"), FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.None))
            {
                await second.ReadExactlyAsync(head, ct);
            }
            var copied = await copy;
            var total = clock.Elapsed;
            copying.Cancel();
            lister.Join();
            fixture.Engine.FileDumpStarted -= Downloading;
            double[] times;
            lock (answers) times = answers.ToArray();
            static double Median(IEnumerable<double> values) { var sorted = values.OrderBy(v => v).ToArray(); return sorted[sorted.Length / 2]; }
            var note = $"512 MiB copied with CopyFileEx from {drive} at a download limit of {limitKBps / 1000} MB/s: result {(copied == 0 ? "success" : "Windows error " + copied)} on the first attempt. " +
                $"First bytes after {firstBytes / 1000.0:F1} s, finished after {total.TotalSeconds:F1} s, {progressCalls} progress updates. " +
                $"A second program read the file's first 64 KiB during the copy; downloads from the repository: {downloads.Count}. " +
                $"Meanwhile, every 25 ms ({times.Length} rounds): a listing of a folder not shown before took median {Median(listings):F1} ms, slowest {listings.Max():F1} ms; " +
                $"the attributes of a small file median {Median(smallOpens):F1} ms, slowest {smallOpens.Max():F1} ms; " +
                $"the size of the file being copied median {Median(largeOpens):F1} ms, slowest {largeOpens.Max():F1} ms " +
                "(WinFsp opens a file only when none of its reads is in progress, so this waits for the copy's reads).";
            ctx.Report.RecordNote("Copying a large file out (U167)", note);
            Output.WriteLine(note);
            Assert.Null(listerFailure);
            Assert.Equal(0, copied);
            Assert.Equal(size, new System.IO.FileInfo(target).Length);
            Assert.Equal(Sha256(large), Sha256(target));
            await using (var original = File.OpenRead(large))
            {
                var expected = new byte[head.Length];

                await original.ReadExactlyAsync(expected, ct);
                Assert.Equal(expected, head);
            }
            Assert.Single(downloads);
            // Before U167 the first bytes came only once the whole file had
            // been restored (13.7 s of 14.1 s), and asking for the size of the
            // file being copied waited as long.
            Assert.True(firstBytes >= 0 && firstBytes < total.TotalMilliseconds / 4, $"First bytes after {firstBytes} ms of {total.TotalMilliseconds:F0} ms.");
            Assert.True(progressCalls >= 20, $"{progressCalls} progress updates.");
            Assert.True(times.Length >= 20, $"{times.Length} rounds.");
            Assert.True(listings.Max() < 100, $"A listing during the copy took {listings.Max():F1} ms.");
            Assert.True(smallOpens.Max() < 100, $"Opening another file during the copy took {smallOpens.Max():F1} ms.");
            Assert.True(largeOpens.Max() < Math.Min(5000, total.TotalMilliseconds / 2), $"Opening the file being copied took {largeOpens.Max():F1} ms.");
        }
        finally { await registry.DisposeAllAsync(); }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private delegate uint CopyProgressRoutine(long totalFileSize, long totalBytesTransferred, long streamSize,
        long streamBytesTransferred, uint streamNumber, uint callbackReason, IntPtr sourceFile, IntPtr destinationFile, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileEx(string existingFileName, string newFileName, CopyProgressRoutine? progressRoutine,
        IntPtr data, IntPtr cancel, uint copyFlags);

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
