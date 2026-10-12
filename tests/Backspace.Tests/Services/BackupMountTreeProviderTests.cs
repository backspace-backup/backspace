using System;
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
/// Mount-tree composition test for the two-branch layout:
/// <c>Z:\Latest revision\&lt;src&gt;\&lt;files&gt;</c> and
/// <c>Z:\All revisions\&lt;src&gt; (N revisions)\&lt;rev&gt;\&lt;files&gt;</c>.
/// Asserts that:
///   * Root lists exactly the two virtual branches.
///   * Latest jumps straight from source into files of the newest snapshot.
///   * All preserves the historical revision-picker layer.
///   * Files served under either branch route through the SAME backing
///     <see cref="EngineBackedSnapshotTreeProvider"/> cache.
///
/// Runs against the in-memory engine so the test is hermetic; the
/// shape it covers is exactly what production WinFSP mounts go
/// through (WinFspSnapshotFs calls ISnapshotTreeProvider with
/// forward-slash paths rooted at the drive).
/// </summary>
public class BackupMountTreeProviderTests : IDisposable
{
    private readonly string _tmpRoot;
    private readonly InMemoryBackupEngine _engine;
    private readonly Backup _backup;
    private readonly Destination _destination;
    private readonly string _cacheDir;
    private readonly Func<SecretsStore, IBackupEngine>? _previousOverride;
    private readonly IBackupEngine _previousActive;

    public BackupMountTreeProviderTests()
    {
        _tmpRoot = Path.Combine(Path.GetTempPath(), "backspace-mounttree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpRoot);

        var sourceA = Path.Combine(_tmpRoot, "alpha");
        Directory.CreateDirectory(Path.Combine(sourceA, "Documents"));
        File.WriteAllText(Path.Combine(sourceA, "readme.txt"), "alpha-root");
        File.WriteAllText(Path.Combine(sourceA, "Documents", "notes.txt"), "alpha-notes");

        var sourceB = Path.Combine(_tmpRoot, "beta");
        Directory.CreateDirectory(sourceB);
        File.WriteAllText(Path.Combine(sourceB, "file.bin"), "beta-r1");

        var storage = Path.Combine(_tmpRoot, "storage");
        Directory.CreateDirectory(storage);

        _engine = new InMemoryBackupEngine();
        _backup = new Backup { Id = "bk-mounttree", Name = "MountTree" };
        _backup.SourcePaths.Add(sourceA);
        _backup.SourcePaths.Add(sourceB);
        _destination = new Destination { Id = "dst-mt", Name = "Local", Kind = DestinationKind.LocalFolder, PathOrSubpath = storage };
        _backup.Targets.Add(new BackupTarget { DestinationId = _destination.Id, StorageName = "default" });
        _cacheDir = Path.Combine(_tmpRoot, "cache");

        ServiceLocator.Config.Update(cfg => cfg.Destinations.Add(_destination));

        // Three revisions on sourceA, one revision on sourceB.
        _engine.BackupPruneCheckAsync(_backup, sourceA, _destination, "default", CancellationToken.None).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(sourceA, "readme.txt"), "alpha-root-r2");
        _engine.BackupPruneCheckAsync(_backup, sourceA, _destination, "default", CancellationToken.None).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(sourceA, "readme.txt"), "alpha-root-r3");
        _engine.BackupPruneCheckAsync(_backup, sourceA, _destination, "default", CancellationToken.None).GetAwaiter().GetResult();
        _engine.BackupPruneCheckAsync(_backup, sourceB, _destination, "default", CancellationToken.None).GetAwaiter().GetResult();

        _previousOverride = ServiceLocator.EngineOverride;
        _previousActive = ServiceLocator.Engine;
        var prop = typeof(ServiceLocator).GetProperty(
            nameof(ServiceLocator.Engine),
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        prop?.SetMethod?.Invoke(null, new object[] { _engine });
        ServiceLocator.RebuildEngineDependents();
    }

    public void Dispose()
    {
        var prop = typeof(ServiceLocator).GetProperty(
            nameof(ServiceLocator.Engine),
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        prop?.SetMethod?.Invoke(null, new object[] { _previousActive });
        ServiceLocator.RebuildEngineDependents();
        ServiceLocator.EngineOverride = _previousOverride;

        ServiceLocator.Config.Update(cfg => cfg.Destinations.RemoveAll(d => d.Id == _destination.Id));
        try { Directory.Delete(_tmpRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task CancelledInitialRevisionListing_CanBeRetriedWithoutRemounting()
    {
        using var provider = new BackupMountTreeProvider(_engine, _backup, _destination, "default", _cacheDir);
        using (var maintenance = await ServiceLocator.Maintenance.AcquireAsync("cancelled mount listing"))
        {
            using var cancel = new CancellationTokenSource();
            var listing = provider.ListAsync("All revisions", cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listing);
        }
        var retried = await provider.ListAsync("All revisions", CancellationToken.None);
        Assert.NotNull(retried);
        Assert.Equal(2, retried!.Count);
    }

    [Fact]
    public async Task FreshMount_IgnoresOldOrdinalCache_AndAnotherDestinationsBytes()
    {
        var legacy = Path.Combine(_cacheDir, "alpha-r3");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "readme.txt"), "WRONG OLD SNAPSHOT");
        using (var first = new BackupMountTreeProvider(_engine, _backup, _destination, "default", _cacheDir))
        await using (var stream = await first.OpenAsync("Latest revision/alpha/readme.txt", CancellationToken.None))
            Assert.Equal("alpha-root-r3", await new StreamReader(stream).ReadToEndAsync());

        var other = new Destination { Id = "another-destination", Name = "Other", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = Path.Combine(_tmpRoot, "other-storage") };
        Directory.CreateDirectory(other.PathOrSubpath);
        for (var version = 1; version <= 3; version++)
        {
            File.WriteAllText(Path.Combine(_backup.SourcePaths[0], "readme.txt"), "OTHER DESTINATION " + version);
            await _engine.BackupPruneCheckAsync(_backup, _backup.SourcePaths[0], other, "default", CancellationToken.None);
        }
        using var second = new BackupMountTreeProvider(_engine, _backup, other, "default", _cacheDir);
        await using var otherStream = await second.OpenAsync("Latest revision/alpha/readme.txt", CancellationToken.None);
        Assert.Equal("OTHER DESTINATION 3", await new StreamReader(otherStream).ReadToEndAsync());
        Assert.Equal("WRONG OLD SNAPSHOT", File.ReadAllText(Path.Combine(legacy, "readme.txt")));
    }

    [Fact]
    public async Task Root_ListsTwoBranches()
    {
        using var provider = new BackupMountTreeProvider(_engine, _backup, _destination, "default", _cacheDir);
        var children = await provider.ListAsync("", CancellationToken.None);
        Assert.NotNull(children);
        Assert.Equal(2, children!.Count);
        Assert.Contains(children, e => e.RelativePath == "Latest revision" && e.IsDirectory);
        Assert.Contains(children, e => e.RelativePath == "All revisions" && e.IsDirectory);
    }

    [Fact]
    public async Task LatestBranch_ListsBareSourceFolders()
    {
        using var provider = new BackupMountTreeProvider(_engine, _backup, _destination, "default", _cacheDir);
        var children = await provider.ListAsync("Latest revision", CancellationToken.None);
        Assert.NotNull(children);
        Assert.Contains(children!, e => e.RelativePath == "Latest revision/alpha" && e.IsDirectory);
        Assert.Contains(children!, e => e.RelativePath == "Latest revision/beta" && e.IsDirectory);
    }

    [Fact]
    public async Task AllBranch_ListsSourcesWithRevisionCount()
    {
        using var provider = new BackupMountTreeProvider(_engine, _backup, _destination, "default", _cacheDir);
        var children = await provider.ListAsync("All revisions", CancellationToken.None);
        Assert.NotNull(children);
        Assert.Contains(children!, e => e.RelativePath == "All revisions/alpha (3 revisions)" && e.IsDirectory);
        Assert.Contains(children!, e => e.RelativePath == "All revisions/beta (1 revision)" && e.IsDirectory);
    }

    [Fact]
    public async Task LatestBranch_SourceFolder_ShowsLatestRevisionFiles()
    {
        using var provider = new BackupMountTreeProvider(_engine, _backup, _destination, "default", _cacheDir);
        // Opening Latest revision/alpha should jump straight to the
        // newest snapshot's files — no revision-picker layer.
        var children = await provider.ListAsync("Latest revision/alpha", CancellationToken.None);
        Assert.NotNull(children);
        Assert.Contains(children!, e => e.RelativePath == "Latest revision/alpha/readme.txt" && !e.IsDirectory);
        Assert.Contains(children!, e => e.RelativePath == "Latest revision/alpha/Documents" && e.IsDirectory);
    }

    [Fact]
    public async Task AllBranch_SourceFolder_ListsRevisionsWithDatedNames()
    {
        using var provider = new BackupMountTreeProvider(_engine, _backup, _destination, "default", _cacheDir);
        var revs = await provider.ListAsync("All revisions/alpha (3 revisions)", CancellationToken.None);
        Assert.NotNull(revs);
        Assert.Equal(3, revs!.Count);
        Assert.All(revs, e => Assert.Matches(@"^All revisions/alpha \(3 revisions\)/r\d+ - \d{4}-\d{2}-\d{2} \d{2}-\d{2}$", e.RelativePath));
    }

    [Fact]
    public async Task AllBranch_RevisionFolder_DelegatesToEngineProviderForFiles()
    {
        using var provider = new BackupMountTreeProvider(_engine, _backup, _destination, "default", _cacheDir);
        var revs = (await provider.ListAsync("All revisions/alpha (3 revisions)", CancellationToken.None))!;
        var r1Folder = revs.Single(r => r.RelativePath.Contains("/r1 - "));
        var files = await provider.ListAsync(r1Folder.RelativePath, CancellationToken.None);
        Assert.NotNull(files);
        Assert.Contains(files!, e => e.RelativePath == r1Folder.RelativePath + "/readme.txt" && !e.IsDirectory);
        Assert.Contains(files!, e => e.RelativePath == r1Folder.RelativePath + "/Documents" && e.IsDirectory);
    }

    [Fact]
    public async Task Stat_ResolvesEveryLevel_LatestBranch()
    {
        using var provider = new BackupMountTreeProvider(_engine, _backup, _destination, "default", _cacheDir);
        Assert.True((await provider.StatAsync("", CancellationToken.None))!.IsDirectory);
        Assert.True((await provider.StatAsync("Latest revision", CancellationToken.None))!.IsDirectory);
        Assert.True((await provider.StatAsync("Latest revision/alpha", CancellationToken.None))!.IsDirectory);
        var file = await provider.StatAsync("Latest revision/alpha/readme.txt", CancellationToken.None);
        Assert.NotNull(file);
        Assert.False(file!.IsDirectory);
    }

    [Fact]
    public async Task Stat_ResolvesEveryLevel_AllBranch()
    {
        using var provider = new BackupMountTreeProvider(_engine, _backup, _destination, "default", _cacheDir);
        Assert.True((await provider.StatAsync("All revisions", CancellationToken.None))!.IsDirectory);
        Assert.True((await provider.StatAsync("All revisions/alpha (3 revisions)", CancellationToken.None))!.IsDirectory);
        Assert.True((await provider.StatAsync("All revisions/alpha (3 revisions)/r2", CancellationToken.None))!.IsDirectory);
        var file = await provider.StatAsync("All revisions/alpha (3 revisions)/r2/readme.txt", CancellationToken.None);
        Assert.NotNull(file);
        Assert.False(file!.IsDirectory);
    }

    [Fact]
    public async Task Stat_UnknownPaths_ReturnNull()
    {
        using var provider = new BackupMountTreeProvider(_engine, _backup, _destination, "default", _cacheDir);
        Assert.Null(await provider.StatAsync("nosuch", CancellationToken.None));
        Assert.Null(await provider.StatAsync("Latest revision/nosuch", CancellationToken.None));
        Assert.Null(await provider.StatAsync("All revisions/alpha (3 revisions)/r99", CancellationToken.None));
        Assert.Null(await provider.StatAsync("All revisions/alpha (3 revisions)/r1/no-such-file.txt", CancellationToken.None));
    }

    [Fact]
    public async Task Open_LatestServesNewestContent()
    {
        using var provider = new BackupMountTreeProvider(_engine, _backup, _destination, "default", _cacheDir);
        await using var latest = await provider.OpenAsync("Latest revision/alpha/readme.txt", CancellationToken.None);
        Assert.Equal("alpha-root-r3", await new StreamReader(latest).ReadToEndAsync());
    }

    [Fact]
    public async Task Open_AllBranch_DifferentRevisionsStreamDifferentContent()
    {
        // The key invariant the user-reported regression was about:
        // each revision folder must serve the bytes captured in THAT
        // revision, not always the latest. If routing dropped the
        // revision on the floor, every snapshot would render with the
        // latest content.
        using var provider = new BackupMountTreeProvider(_engine, _backup, _destination, "default", _cacheDir);

        await using var r1 = await provider.OpenAsync("All revisions/alpha (3 revisions)/r1/readme.txt", CancellationToken.None);
        Assert.Equal("alpha-root", await new StreamReader(r1).ReadToEndAsync());

        await using var r3 = await provider.OpenAsync("All revisions/alpha (3 revisions)/r3/readme.txt", CancellationToken.None);
        Assert.Equal("alpha-root-r3", await new StreamReader(r3).ReadToEndAsync());
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

    [Fact]
    public void ParsePath_RootAndBranchLevels()
    {
        Assert.Null(BackupMountTreeProvider.ParsePath("").Branch);
        Assert.Equal("Latest revision", BackupMountTreeProvider.ParsePath("Latest revision").Branch);
        Assert.Equal("All revisions", BackupMountTreeProvider.ParsePath("All revisions").Branch);
        // Unknown top-level segment → null branch.
        Assert.Null(BackupMountTreeProvider.ParsePath("not a branch").Branch);
    }

    [Fact]
    public void ParsePath_LatestBranchPaths()
    {
        var p = BackupMountTreeProvider.ParsePath("Latest revision/alpha");
        Assert.Equal("Latest revision", p.Branch);
        Assert.Equal("alpha", p.Src);
        Assert.Null(p.Rev);
        Assert.Equal("", p.Remainder);

        p = BackupMountTreeProvider.ParsePath("Latest revision/alpha/Docs/file.txt");
        Assert.Equal("alpha", p.Src);
        Assert.Equal("Docs/file.txt", p.Remainder);
    }

    [Fact]
    public void ParsePath_AllBranchPaths()
    {
        var p = BackupMountTreeProvider.ParsePath("All revisions/alpha (3 revisions)/r4");
        Assert.Equal("All revisions", p.Branch);
        Assert.Equal("alpha", p.Src);
        Assert.Equal("alpha (3 revisions)", p.SrcSegment);
        Assert.Equal(4, p.Rev);
        Assert.Equal("", p.Remainder);

        // Dated revision form
        p = BackupMountTreeProvider.ParsePath("All revisions/alpha (3 revisions)/r4 - 2026-06-12 07-49/Docs/x.txt");
        Assert.Equal(4, p.Rev);
        Assert.Equal("r4 - 2026-06-12 07-49", p.RevSegment);
        Assert.Equal("Docs/x.txt", p.Remainder);
    }

    [Fact]
    public void StripCountSuffix_OnlyStripsRevisionAnnotation()
    {
        Assert.Equal("alpha", BackupMountTreeProvider.StripCountSuffix("alpha (3 revisions)"));
        Assert.Equal("alpha", BackupMountTreeProvider.StripCountSuffix("alpha (1 revision)"));
        // Don't strip a disambiguator suffix that LOOKS like parens.
        Assert.Equal("ntws (C)", BackupMountTreeProvider.StripCountSuffix("ntws (C)"));
        // Pass through bare names unchanged.
        Assert.Equal("plain", BackupMountTreeProvider.StripCountSuffix("plain"));
    }
}
