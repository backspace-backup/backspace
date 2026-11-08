using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.WebDav;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U162: the drive shows only the revisions the person chose, and every level
/// with a single choice is left out.
/// <list type="bullet">
/// <item>One source, one revision: its files at the drive root.</item>
/// <item>One source, several: one folder per revision, named by date and time.</item>
/// <item>Several sources: a folder per source, then the same rule inside.</item>
/// </list>
/// Only the chosen trees are read, each once, before anything is listed;
/// browsing everything afterwards reads no tree and no revision list.
/// </summary>
public class BackupMountTreeProviderTests : IDisposable
{
    private readonly string _tmpRoot;
    private readonly InMemoryBackupEngine _engine;
    private readonly Backup _backup;
    private readonly Destination _destination;
    private readonly string _cacheDir;
    private readonly string _alpha;
    private readonly string _beta;
    private readonly IReadOnlyList<RevisionSummary> _alphaRevisions;
    private readonly IReadOnlyList<RevisionSummary> _betaRevisions;

    // Distinct, known times so folder names are predictable (the in-memory
    // engine's snapshots are all made within the same second).
    private static readonly DateTime R1 = new(2026, 11, 5, 9, 12, 0, DateTimeKind.Utc);
    private static readonly DateTime R2 = new(2026, 11, 6, 18, 40, 0, DateTimeKind.Utc);
    private static readonly DateTime R3 = new(2026, 11, 7, 14, 30, 0, DateTimeKind.Utc);

    public BackupMountTreeProviderTests()
    {
        _tmpRoot = Path.Combine(Path.GetTempPath(), "backspace-mounttree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpRoot);

        _alpha = Path.Combine(_tmpRoot, "alpha");
        Directory.CreateDirectory(Path.Combine(_alpha, "Documents"));
        File.WriteAllText(Path.Combine(_alpha, "readme.txt"), "alpha-root");
        File.WriteAllText(Path.Combine(_alpha, "Documents", "notes.txt"), "alpha-notes");

        _beta = Path.Combine(_tmpRoot, "beta");
        Directory.CreateDirectory(_beta);
        File.WriteAllText(Path.Combine(_beta, "file.bin"), "beta-r1");

        var storage = Path.Combine(_tmpRoot, "storage");
        Directory.CreateDirectory(storage);

        _engine = new InMemoryBackupEngine();
        _backup = new Backup { Id = "bk-mounttree", Name = "MountTree" };
        _backup.SourcePaths.Add(_alpha);
        _backup.SourcePaths.Add(_beta);
        _destination = new Destination { Id = "dst-mt", Name = "Local", Kind = DestinationKind.LocalFolder, PathOrSubpath = storage };
        _backup.Targets.Add(new BackupTarget { DestinationId = _destination.Id, StorageName = "default" });
        _cacheDir = Path.Combine(_tmpRoot, "cache");

        // Three revisions of alpha, one of beta.
        _engine.BackupPruneCheckAsync(_backup, _alpha, _destination, "default", CancellationToken.None).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(_alpha, "readme.txt"), "alpha-root-r2");
        _engine.BackupPruneCheckAsync(_backup, _alpha, _destination, "default", CancellationToken.None).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(_alpha, "readme.txt"), "alpha-root-r3");
        _engine.BackupPruneCheckAsync(_backup, _alpha, _destination, "default", CancellationToken.None).GetAwaiter().GetResult();
        _engine.BackupPruneCheckAsync(_backup, _beta, _destination, "default", CancellationToken.None).GetAwaiter().GetResult();

        DateTime At(int number) => number switch { 1 => R1, 2 => R2, _ => R3 };
        _alphaRevisions = _engine.ListSnapshotsAsync(_backup, _alpha, _destination, "default", CancellationToken.None)
            .GetAwaiter().GetResult().Select(r => r with { CreatedUtc = At(r.Number) }).ToList();
        _betaRevisions = _engine.ListSnapshotsAsync(_backup, _beta, _destination, "default", CancellationToken.None)
            .GetAwaiter().GetResult().Select(r => r with { CreatedUtc = R2 }).ToList();
        while (_engine.Calls.TryDequeue(out _)) { }
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpRoot, recursive: true); } catch { }
    }

    private RevisionSummary Alpha(int number) => _alphaRevisions.Single(r => r.Number == number);
    private static string Folder(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH-mm", System.Globalization.CultureInfo.InvariantCulture);

    private BackupMountTreeProvider Mount(params MountSelection.Source[] sources)
        => new(_engine, _backup, _destination, "default", _cacheDir, new MountSelection(sources));

    private int TreeReads => _engine.Calls.Count(c => c.StartsWith("ListSnapshotTree(", StringComparison.Ordinal));
    private int OtherEngineReads => _engine.Calls.Count(c => c.StartsWith("ListSnapshots(", StringComparison.Ordinal)
        || c.StartsWith("ListSnapshotDirectory(", StringComparison.Ordinal) || c.StartsWith("ListSnapshotFiles(", StringComparison.Ordinal));

    private static async Task<string> Read(ISnapshotTreeProvider provider, string path)
    {
        await using var stream = await provider.OpenAsync(path, CancellationToken.None);
        return await new StreamReader(stream).ReadToEndAsync();
    }

    /// <summary>Lists every folder and stats every entry, as Explorer, the
    /// thumbnailer and the search indexer would.</summary>
    private static async Task<int> WalkEverything(ISnapshotTreeProvider provider, string dir = "")
    {
        var count = 0;
        foreach (var entry in (await provider.ListAsync(dir, CancellationToken.None))!)
        {
            count++;
            Assert.NotNull(await provider.StatAsync(entry.RelativePath, CancellationToken.None));
            if (entry.IsDirectory) count += await WalkEverything(provider, entry.RelativePath);
        }
        return count;
    }

    [Fact]
    public async Task OneOlderRevision_ItsFilesAreAtTheDriveRoot_AndOnlyItsTreeIsRead()
    {
        using var provider = Mount(new MountSelection.Source(_alpha, new[] { Alpha(2) }));
        Assert.Equal(1, await provider.PrepareAsync(null, CancellationToken.None));
        Assert.Equal(1, TreeReads);

        var root = (await provider.ListAsync("", CancellationToken.None))!;
        Assert.Equal(new[] { "Documents", "readme.txt" }, root.Select(e => e.RelativePath).OrderBy(p => p, StringComparer.Ordinal));
        Assert.Equal("alpha-root-r2", await Read(provider, "readme.txt"));
        Assert.Equal("alpha-notes", await Read(provider, "Documents/notes.txt"));
        Assert.True((await provider.StatAsync("", CancellationToken.None))!.IsDirectory);
        Assert.True((await provider.StatAsync("Documents", CancellationToken.None))!.IsDirectory);

        // Browsing everything afterwards reads nothing more from the engine.
        Assert.Equal(3, await WalkEverything(provider));
        Assert.Null(await provider.ListAsync("All revisions", CancellationToken.None));
        Assert.Null(await provider.StatAsync("Latest revision", CancellationToken.None));
        Assert.Null(await provider.StatAsync("desktop.ini", CancellationToken.None));
        Assert.Equal(1, TreeReads);
        Assert.Equal(0, OtherEngineReads);
    }

    [Fact]
    public async Task SeveralRevisions_OneFolderEach_NamedByDateAndTime_EachServingItsOwnBytes()
    {
        using var provider = Mount(new MountSelection.Source(_alpha, new[] { Alpha(3), Alpha(1) }));
        Assert.Equal(2, await provider.PrepareAsync(null, CancellationToken.None));
        Assert.Equal(2, TreeReads);

        var root = (await provider.ListAsync("", CancellationToken.None))!;
        Assert.Equal(new[] { Folder(R3), Folder(R1) }, root.Select(e => e.RelativePath));
        Assert.All(root, e => Assert.True(e.IsDirectory));
        Assert.Equal(R1, root[1].LastWriteUtc);
        Assert.True((await provider.StatAsync("", CancellationToken.None))!.IsDirectory);
        Assert.True((await provider.StatAsync(Folder(R1), CancellationToken.None))!.IsDirectory);
        Assert.Equal("alpha-root-r3", await Read(provider, Folder(R3) + "/readme.txt"));
        Assert.Equal("alpha-root", await Read(provider, Folder(R1) + "/readme.txt"));
        var inside = (await provider.ListAsync(Folder(R1), CancellationToken.None))!;
        Assert.Contains(inside, e => e.RelativePath == Folder(R1) + "/Documents" && e.IsDirectory);

        // The revision that was not chosen is not there, and nothing reads it.
        Assert.Null(await provider.StatAsync(Folder(R2), CancellationToken.None));
        Assert.Null(await provider.ListAsync(Folder(R2), CancellationToken.None));
        Assert.Equal(2 + 2 * 3, await WalkEverything(provider));
        Assert.Equal(2, TreeReads);
        Assert.Equal(0, OtherEngineReads);
        Assert.DoesNotContain(_engine.Calls, c => c.Contains(",r2)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SeveralSources_AFolderEach_ThenTheSameRuleInside()
    {
        using var provider = Mount(
            new MountSelection.Source(_alpha, new[] { Alpha(3), Alpha(2) }),
            new MountSelection.Source(_beta, _betaRevisions));
        Assert.Equal(3, await provider.PrepareAsync(null, CancellationToken.None));

        var root = (await provider.ListAsync("", CancellationToken.None))!;
        Assert.Equal(new[] { "alpha", "beta" }, root.Select(e => e.RelativePath));
        Assert.Equal(new[] { "alpha/" + Folder(R3), "alpha/" + Folder(R2) },
            (await provider.ListAsync("alpha", CancellationToken.None))!.Select(e => e.RelativePath));
        // beta has one chosen revision: its files sit right in its folder.
        Assert.Equal(new[] { "beta/file.bin" }, (await provider.ListAsync("beta", CancellationToken.None))!.Select(e => e.RelativePath));
        Assert.Equal("beta-r1", await Read(provider, "beta/file.bin"));
        Assert.Equal("alpha-root-r2", await Read(provider, "alpha/" + Folder(R2) + "/readme.txt"));
        Assert.True((await provider.StatAsync("alpha", CancellationToken.None))!.IsDirectory);
        Assert.True((await provider.StatAsync("alpha/" + Folder(R3), CancellationToken.None))!.IsDirectory);
        Assert.Null(await provider.StatAsync("alpha/" + Folder(R1), CancellationToken.None));
        Assert.Null(await provider.StatAsync("gamma", CancellationToken.None));
        Assert.Equal(3, TreeReads);
        Assert.Equal(0, OtherEngineReads);
    }

    [Fact]
    public async Task SeveralSources_OneRevisionEach_FilesInEachSourceFolder()
    {
        using var provider = Mount(
            new MountSelection.Source(_alpha, new[] { Alpha(3) }),
            new MountSelection.Source(_beta, _betaRevisions));
        await provider.PrepareAsync(null, CancellationToken.None);
        Assert.Equal(new[] { "alpha", "beta" }, (await provider.ListAsync("", CancellationToken.None))!.Select(e => e.RelativePath));
        Assert.Contains((await provider.ListAsync("alpha", CancellationToken.None))!, e => e.RelativePath == "alpha/readme.txt");
        Assert.Equal("alpha-root-r3", await Read(provider, "alpha/readme.txt"));
    }

    [Fact]
    public async Task WindowsUppercasePaths_ListAndReadTheCorrectRevision()
    {
        using var provider = Mount(
            new MountSelection.Source(_alpha, new[] { Alpha(3), Alpha(1) }),
            new MountSelection.Source(_beta, _betaRevisions));
        await provider.PrepareAsync(null, CancellationToken.None);
        var r1 = "ALPHA/" + Folder(R1).ToUpperInvariant();
        Assert.True((await provider.StatAsync(r1, CancellationToken.None))!.IsDirectory);
        var listed = (await provider.ListAsync(r1, CancellationToken.None))!;
        Assert.All(listed, e => Assert.StartsWith(r1 + "/", e.RelativePath, StringComparison.Ordinal));
        Assert.Equal("alpha-root", await Read(provider, r1 + "/README.TXT"));
        Assert.Equal("beta-r1", await Read(provider, "BETA/FILE.BIN"));
    }

    [Fact]
    public async Task Prepare_SaysWhichRevisionItIsReading()
    {
        using var provider = Mount(new MountSelection.Source(_alpha, new[] { Alpha(3), Alpha(2), Alpha(1) }));
        var heard = new List<MountReading>();
        await provider.PrepareAsync(new Inline(heard.Add), CancellationToken.None);
        Assert.Equal(new[] { 1, 2, 3 }, heard.Select(r => r.Index).Distinct());
        Assert.All(heard, r => Assert.Equal(3, r.Count));
        Assert.Equal(MountSelection.When(R3), heard.First().Revision);
        Assert.Equal(MountSelection.When(R1), heard.Last().Revision);
        // Files only (readme.txt, Documents/notes.txt), not the Documents folder.
        Assert.Equal(2, heard.Last().Files);
        Assert.Equal("Reading the backup's folders, revision 2 of 3 (6 Nov 2026 x): 1,200 files so far".Replace("6 Nov 2026 x", MountSelection.When(R2)),
            MountedSnapshotRegistry.ReadingText(new MountReading(1200, 2, 3, MountSelection.When(R2))));
        Assert.Equal("Reading the backup's folders, revision 2 of 3 (6 Nov 2026 x): 41%, 1,200 files so far".Replace("6 Nov 2026 x", MountSelection.When(R2)),
            MountedSnapshotRegistry.ReadingText(new MountReading(1200, 2, 3, MountSelection.When(R2), 41)));
        Assert.Equal("Reading the backup's folders: 1,200 files so far", MountedSnapshotRegistry.ReadingText(1200));
        Assert.Equal("Reading the backup's folders: 0%", MountedSnapshotRegistry.ReadingText(new MountReading(0, Percent: 0)));
        Assert.Equal("Reading the backup's folders", MountedSnapshotRegistry.ReadingText(0));
    }

    /// <summary>U166: the revisions' summaries give their file counts, so the
    /// mount's bar runs once from 0 to 100% over every chosen revision, never
    /// backwards, and reaches 100% only when the last tree has been read.</summary>
    [Fact]
    public async Task Prepare_ReportsAPercentageOverTheWholeMount_FromTheRevisionsFileCounts()
    {
        using var provider = Mount(new MountSelection.Source(_alpha, new[] { Alpha(3), Alpha(2), Alpha(1) }));
        var heard = new List<MountReading>();
        await provider.PrepareAsync(new Inline(heard.Add), CancellationToken.None);
        Assert.All(heard, r => Assert.NotNull(r.Percent));
        var percents = heard.Select(r => r.Percent!.Value).ToList();
        Assert.Equal(percents.OrderBy(p => p), percents);           // never backwards
        Assert.Equal(100, percents.Last());
        Assert.All(heard.Where(r => r.Index < 3), r => Assert.True(r.Percent <= 99));
        // Each of the three equal revisions done: a third, then two thirds.
        Assert.Equal(33, heard.Last(r => r.Index == 1).Percent);
        Assert.Equal(66, heard.Last(r => r.Index == 2).Percent);

        // Revisions without a summary (older snapshots) and no other revision
        // of the same source to go by: the bar keeps moving, no percentage.
        using var unknown = Mount(new MountSelection.Source(_alpha, new[] { Alpha(3) with { FileCount = 0 } }));
        heard.Clear();
        await unknown.PrepareAsync(new Inline(heard.Add), CancellationToken.None);
        Assert.NotEmpty(heard);
        Assert.All(heard, r => Assert.Null(r.Percent));
        Assert.Equal("Reading the backup's folders: 2 files so far", MountedSnapshotRegistry.ReadingText(heard.Last()));
    }

    [Fact]
    public async Task Prepare_CanBeCancelled_AndNothingIsChosenMeansNothingToMount()
    {
        _engine.ListSnapshotTreeGate = new ManualResetEventSlim(false);
        try
        {
            // Cancel on the card cancels the mount, which disposes the
            // provider: the listing in progress stops.
            using var provider = Mount(new MountSelection.Source(_alpha, new[] { Alpha(1) }));
            using var cancel = new CancellationTokenSource();
            var preparing = Task.Run(() => provider.PrepareAsync(null, cancel.Token));
            await Task.Delay(150);
            Assert.False(preparing.IsCompleted);
            cancel.Cancel();
            provider.Dispose();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing);
        }
        finally { _engine.ListSnapshotTreeGate?.Set(); _engine.ListSnapshotTreeGate = null; }

        using var empty = Mount(new MountSelection.Source(_alpha, Array.Empty<RevisionSummary>()));
        Assert.Equal(0, await empty.PrepareAsync(null, CancellationToken.None));
        Assert.Empty((await empty.ListAsync("", CancellationToken.None))!);
    }

    [Fact]
    public async Task FreshMount_IgnoresOldOrdinalCache_AndAnotherDestinationsBytes()
    {
        var legacy = Path.Combine(_cacheDir, "alpha-r3");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "readme.txt"), "WRONG OLD SNAPSHOT");
        using (var first = Mount(new MountSelection.Source(_alpha, new[] { Alpha(3) })))
            Assert.Equal("alpha-root-r3", await Read(first, "readme.txt"));

        var other = new Destination { Id = "another-destination", Name = "Other", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = Path.Combine(_tmpRoot, "other-storage") };
        Directory.CreateDirectory(other.PathOrSubpath);
        for (var version = 1; version <= 3; version++)
        {
            File.WriteAllText(Path.Combine(_alpha, "readme.txt"), "OTHER DESTINATION " + version);
            await _engine.BackupPruneCheckAsync(_backup, _alpha, other, "default", CancellationToken.None);
        }
        var otherLatest = (await _engine.ListSnapshotsAsync(_backup, _alpha, other, "default", CancellationToken.None))[0];
        using var second = new BackupMountTreeProvider(_engine, _backup, other, "default", _cacheDir,
            new MountSelection(new[] { new MountSelection.Source(_alpha, new[] { otherLatest }) }));
        Assert.Equal("OTHER DESTINATION 3", await Read(second, "readme.txt"));
        Assert.Equal("WRONG OLD SNAPSHOT", File.ReadAllText(Path.Combine(legacy, "readme.txt")));
    }

    [Fact]
    public void RevisionFolderNames_AddSecondsThenACounterWhenTimesCollide()
    {
        var a = new RevisionSummary(3, new DateTime(2026, 11, 7, 14, 30, 5, DateTimeKind.Utc), 0, 0);
        var b = new RevisionSummary(2, new DateTime(2026, 11, 7, 14, 30, 41, DateTimeKind.Utc), 0, 0);
        var c = new RevisionSummary(1, new DateTime(2026, 11, 7, 14, 30, 41, DateTimeKind.Utc), 0, 0);
        var d = new RevisionSummary(4, new DateTime(2026, 11, 8, 8, 0, 0, DateTimeKind.Utc), 0, 0);
        var names = BackupMountTreeProvider.RevisionFolderNames(new[] { d, a, b, c });
        string Local(DateTime utc, string format) => utc.ToLocalTime().ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(new[]
        {
            Local(d.CreatedUtc, "yyyy-MM-dd HH-mm"),
            Local(a.CreatedUtc, "yyyy-MM-dd HH-mm-ss"),
            Local(b.CreatedUtc, "yyyy-MM-dd HH-mm-ss"),
            Local(c.CreatedUtc, "yyyy-MM-dd HH-mm-ss") + " (2)",
        }, names);
        Assert.All(names, n => Assert.DoesNotContain(':', n));
    }

    [Fact]
    public void Describe_NamesTheRevisionOrCountsThem()
    {
        Assert.Equal(MountSelection.When(R2), new MountSelection(new[] { new MountSelection.Source(_alpha, new[] { Alpha(2) }) }).Describe());
        Assert.Equal(MountSelection.When(R3), new MountSelection(new[]
        {
            new MountSelection.Source(_alpha, new[] { Alpha(3) }), new MountSelection.Source(_beta, _betaRevisions),
        }).Describe());
        Assert.Equal("2 revisions", new MountSelection(new[] { new MountSelection.Source(_alpha, new[] { Alpha(3), Alpha(1) }) }).Describe());
    }

    [Fact]
    public void BuildSourceFolderMap_DisambiguatesCollisions()
    {
        // Two sources whose leaf names collide ("foo") — the second
        // gets a disambiguator. Predictable so the user doesn't see
        // a missing source in Z:\.
        var map = BackupMountTreeProvider.BuildSourceFolderMap(
            new[] { @"C:\projects\foo", @"D:\backups\foo" });
        Assert.Equal(2, map.Count);
        Assert.Contains("foo", map.Keys);
        Assert.Contains(map.Keys, k => k != "foo" && k.StartsWith("foo"));
    }

    private sealed class Inline(Action<MountReading> report) : IProgress<MountReading>
    {
        public void Report(MountReading value) => report(value);
    }
}
