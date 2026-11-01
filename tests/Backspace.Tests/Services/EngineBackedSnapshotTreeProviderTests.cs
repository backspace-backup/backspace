using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.WebDav;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Round-trips an engine-backed snapshot tree through the
/// <see cref="ISnapshotTreeProvider"/> surface. Uses the in-memory
/// engine for determinism — the engine writes the snapshot to a
/// known on-disk layout, the provider reads back through
/// <see cref="IBackupEngine.ListSnapshotFilesAsync"/> +
/// <see cref="IBackupEngine.RestoreFilesAsync"/>, and the test
/// asserts both PROPFIND-shaped enumeration and GET-shaped content
/// retrieval reach the same bytes.
/// </summary>
public class EngineBackedSnapshotTreeProviderTests : IDisposable
{
    private readonly string _tmpRoot;
    private readonly InMemoryBackupEngine _engine;
    private readonly Backup _backup;
    private readonly Destination _destination;
    private readonly string _cacheDir;

    public EngineBackedSnapshotTreeProviderTests()
    {
        _tmpRoot = Path.Combine(Path.GetTempPath(), "backspace-engineprov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpRoot);

        var source = Path.Combine(_tmpRoot, "source");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(source, "photos", "2026"));
        File.WriteAllText(Path.Combine(source, "readme.txt"),                       "alpha");
        File.WriteAllText(Path.Combine(source, "photos", "2026", "a.jpg"),          "beta");
        File.WriteAllText(Path.Combine(source, "photos", "2026", "b.jpg"),          "gamma");

        var storage = Path.Combine(_tmpRoot, "storage");
        Directory.CreateDirectory(storage);

        _engine = new InMemoryBackupEngine();
        _backup = new Backup { Id = "bk-prov", Name = "Provider", SourcePaths = { source } };
        _destination = new Destination { Id = "dst-prov", Name = "Local", Kind = DestinationKind.LocalFolder, PathOrSubpath = storage };
        _cacheDir = Path.Combine(_tmpRoot, "cache");

        // Seed: one backup so the engine has a snapshot to expose.
        _engine.BackupPruneCheckAsync(_backup, source, _destination, "default", CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpRoot, recursive: true); } catch { /* AV / locked file — best-effort */ }
    }

    [Fact]
    public async Task UncachedMountedRead_WaitsForExclusiveMaintenance_AndHonoursCancellation()
    {
        using var provider = new EngineBackedSnapshotTreeProvider(_engine, _backup, _backup.SourcePaths[0], _destination, "default", 1, _cacheDir);
        using var maintenance = await ServiceLocator.Maintenance.AcquireAsync("mount regression test");
        using var cancel = new CancellationTokenSource();
        var listing = provider.ListAsync("", cancel.Token);
        await Task.Delay(150);
        Assert.False(listing.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listing);
        Assert.False(ServiceLocator.Maintenance.HasReaders);
        maintenance.Dispose();
        Assert.NotEmpty((await provider.ListAsync("", CancellationToken.None))!);
    }

    [Fact]
    public async Task ListAsync_Root_EnumeratesTopLevelChildren()
    {
        using var provider = new EngineBackedSnapshotTreeProvider(
            _engine, _backup, _backup.SourcePaths[0], _destination, "default", revision: 1, cacheDir: _cacheDir);

        var children = await provider.ListAsync("", CancellationToken.None);
        Assert.NotNull(children);
        Assert.Contains(children!, e => e.RelativePath == "readme.txt" && !e.IsDirectory);
        Assert.Contains(children!, e => e.RelativePath == "photos" && e.IsDirectory);
    }

    [Fact]
    public async Task ListAsync_NestedDirectory_EnumeratesItsChildren()
    {
        using var provider = new EngineBackedSnapshotTreeProvider(
            _engine, _backup, _backup.SourcePaths[0], _destination, "default", revision: 1, cacheDir: _cacheDir);

        var children = await provider.ListAsync("photos/2026", CancellationToken.None);
        Assert.NotNull(children);
        Assert.Equal(2, children!.Count);
        Assert.All(children, e => Assert.False(e.IsDirectory));
        Assert.Contains(children, e => e.RelativePath == "photos/2026/a.jpg");
        Assert.Contains(children, e => e.RelativePath == "photos/2026/b.jpg");
    }

    [Fact]
    public async Task ListAsync_MissingDirectory_ReturnsNull()
    {
        using var provider = new EngineBackedSnapshotTreeProvider(
            _engine, _backup, _backup.SourcePaths[0], _destination, "default", revision: 1, cacheDir: _cacheDir);

        Assert.Null(await provider.ListAsync("no/such/dir", CancellationToken.None));
    }

    [Fact]
    public async Task StatAsync_ResolvesFileAndDirectory()
    {
        using var provider = new EngineBackedSnapshotTreeProvider(
            _engine, _backup, _backup.SourcePaths[0], _destination, "default", revision: 1, cacheDir: _cacheDir);

        var file = await provider.StatAsync("readme.txt", CancellationToken.None);
        Assert.NotNull(file);
        Assert.False(file!.IsDirectory);
        Assert.Equal(5, file.SizeBytes); // "alpha"

        var dir = await provider.StatAsync("photos", CancellationToken.None);
        Assert.NotNull(dir);
        Assert.True(dir!.IsDirectory);
    }

    [Fact]
    public async Task OpenAsync_RestoresAndStreamsContent()
    {
        using var provider = new EngineBackedSnapshotTreeProvider(
            _engine, _backup, _backup.SourcePaths[0], _destination, "default", revision: 1, cacheDir: _cacheDir);

        await using var stream = await provider.OpenAsync("readme.txt", CancellationToken.None);
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();
        Assert.Equal("alpha", content);

        // The materialised cache file exists after the first Open;
        // the second Open serves from cache (no second RestoreFiles
        // call). We verify by counting the engine's Calls log.
        var restoreCallsBefore = _engine.Calls.Count(c => c.StartsWith("RestoreFiles"));
        await using var second = await provider.OpenAsync("readme.txt", CancellationToken.None);
        var restoreCallsAfter = _engine.Calls.Count(c => c.StartsWith("RestoreFiles"));
        Assert.Equal(restoreCallsBefore, restoreCallsAfter);
    }

    [Fact]
    public async Task OpenAsync_OnDirectory_Throws()
    {
        using var provider = new EngineBackedSnapshotTreeProvider(
            _engine, _backup, _backup.SourcePaths[0], _destination, "default", revision: 1, cacheDir: _cacheDir);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => provider.OpenAsync("photos", CancellationToken.None));
    }

    [Fact]
    public async Task OpenAsync_SingleRead_StaysOnPerFilePath_NoBulkRestore()
    {
        // The privacy/disk-space property: a user who opens ONE file
        // from a snapshot must NOT trigger a bulk restore of the
        // whole subtree. Otherwise opening one tiny doc from a 100 GB
        // snapshot would materialise 100 GB to disk.
        using var provider = new EngineBackedSnapshotTreeProvider(
            _engine, _backup, _backup.SourcePaths[0], _destination, "default", revision: 1, cacheDir: _cacheDir);

        var bulkBefore = _engine.Calls.Count(c => c.StartsWith("RestoreSnapshotSubtree"));
        await using (var s1 = await provider.OpenAsync("readme.txt", CancellationToken.None))
            Assert.Equal("alpha", await new StreamReader(s1).ReadToEndAsync());

        Assert.Equal(bulkBefore, _engine.Calls.Count(c => c.StartsWith("RestoreSnapshotSubtree")));
        // Per-file restore happened exactly once for the one file
        // we asked for; nothing else was written to the cache dir.
        Assert.Single(_engine.Calls, c => c.StartsWith("RestoreFiles"));
    }

    [Fact]
    public async Task OpenAsync_RepeatedReads_PromoteToBulkRestoreExactlyOnce()
    {
        // The performance property: once Backspace has seen multiple
        // opens on the same (source, revision) — the user is clearly
        // traversing or copying, not sampling — we MUST switch to a
        // single bulk subtree restore. Without it an Explorer "Copy"
        // of a 2500-file revision spent ~2 hours doing one restic
        // invocation per file. With it the copy completes in seconds.
        using var provider = new EngineBackedSnapshotTreeProvider(
            _engine, _backup, _backup.SourcePaths[0], _destination, "default", revision: 1, cacheDir: _cacheDir);

        var bulkBefore = _engine.Calls.Count(c => c.StartsWith("RestoreSnapshotSubtree"));

        // First read — per-file path.
        await using (var s1 = await provider.OpenAsync("readme.txt", CancellationToken.None))
            Assert.Equal("alpha", await new StreamReader(s1).ReadToEndAsync());
        Assert.Equal(bulkBefore, _engine.Calls.Count(c => c.StartsWith("RestoreSnapshotSubtree")));

        // Second read — promotes to bulk. Even though THIS file is
        // a different one in a deep subdir, bulk picks up both.
        await using (var s2 = await provider.OpenAsync("photos/2026/a.jpg", CancellationToken.None))
            Assert.Equal("beta", await new StreamReader(s2).ReadToEndAsync());
        Assert.Equal(bulkBefore + 1, _engine.Calls.Count(c => c.StartsWith("RestoreSnapshotSubtree")));

        // Third read of yet another file: singleton bulk task → no
        // new bulk call, served from the cache the bulk restore
        // populated.
        await using (var s3 = await provider.OpenAsync("photos/2026/b.jpg", CancellationToken.None))
            Assert.Equal("gamma", await new StreamReader(s3).ReadToEndAsync());
        Assert.Equal(bulkBefore + 1, _engine.Calls.Count(c => c.StartsWith("RestoreSnapshotSubtree")));
    }

    [Fact]
    public async Task CachedJunction_IsNeverFollowedIntoAnExternalSentinel()
    {
        using var provider = new EngineBackedSnapshotTreeProvider(_engine, _backup, _backup.SourcePaths[0],
            _destination, "default", 1, _cacheDir);
        var outside = Path.Combine(_tmpRoot, "outside");
        Directory.CreateDirectory(Path.Combine(outside, "2026"));
        var sentinel = Path.Combine(outside, "2026", "a.jpg");
        File.WriteAllText(sentinel, "LIVE LOCAL FILE, NOT BACKUP DATA");
        var junction = Path.Combine(_cacheDir, "files", "photos");
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J");
        start.ArgumentList.Add(junction); start.ArgumentList.Add(outside);
        using var child = Process.Start(start)!;
        await child.WaitForExitAsync();
        Assert.Equal(0, child.ExitCode);
        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() => provider.OpenAsync("photos/2026/a.jpg", CancellationToken.None));
            Assert.Contains("linked file or folder", error.Message);
            Assert.Equal("LIVE LOCAL FILE, NOT BACKUP DATA", File.ReadAllText(sentinel));
            Assert.DoesNotContain(_engine.Calls, c => c.StartsWith("RestoreFiles"));
        }
        finally { Directory.Delete(junction); }
    }

    [Fact]
    public async Task FailedBulkRestore_NeverServesPreallocatedFiles_AndFallsBackToVerifiedFiles()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proxy = DispatchProxy.Create<IBackupEngine, BulkEngineProxy>();
        var control = (BulkEngineProxy)proxy;
        control.Inner = _engine;
        control.Bulk = async args =>
        {
            var target = (string)args[5]!;
            Directory.CreateDirectory(Path.Combine(target, "photos", "2026"));
            File.WriteAllText(Path.Combine(target, "photos", "2026", "a.jpg"), "INCOMPLETE");
            File.WriteAllText(Path.Combine(target, "photos", "2026", "b.jpg"), "INCOMPLETE");
            started.TrySetResult();
            await finish.Task;
            return false;
        };
        using var provider = new EngineBackedSnapshotTreeProvider(proxy, _backup, _backup.SourcePaths[0],
            _destination, "default", 1, _cacheDir);
        await using (var first = await provider.OpenAsync("readme.txt", CancellationToken.None)) { }
        var second = provider.OpenAsync("photos/2026/a.jpg", CancellationToken.None);
        Task<Stream>? concurrent = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            concurrent = provider.OpenAsync("photos/2026/b.jpg", CancellationToken.None);
            await Task.Delay(100);
            Assert.False(second.IsCompleted);
            Assert.False(concurrent.IsCompleted);
        }
        finally { finish.TrySetResult(); }
        await using var a = await second.WaitAsync(TimeSpan.FromSeconds(5));
        await using var b = await concurrent!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("beta", await new StreamReader(a).ReadToEndAsync());
        Assert.Equal("gamma", await new StreamReader(b).ReadToEndAsync());
    }

    public class BulkEngineProxy : DispatchProxy
    {
        public IBackupEngine Inner { get; set; } = null!;
        public Func<object?[], Task<bool>> Bulk { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == nameof(IBackupEngine.RestoreSnapshotSubtreeAsync)
                ? Bulk(args!) : targetMethod.Invoke(Inner, args);
    }

    [Fact]
    public async Task OpenAsync_OnEngineProvider_StreamsContentLazily()
    {
        // Lazy-restore round-trip: the engine seeds a snapshot, the
        // provider exposes it, and OpenAsync materialises a specific
        // file via the engine on first read. This is the same path
        // WinFspSnapshotFs.Read drives under a real WinFSP mount —
        // exercising it here keeps the provider contract under
        // regression coverage without requiring a kernel driver.
        using var provider = new EngineBackedSnapshotTreeProvider(
            _engine, _backup, _backup.SourcePaths[0], _destination, "default", revision: 1, cacheDir: _cacheDir);

        await using (var top = await provider.OpenAsync("readme.txt", CancellationToken.None))
        {
            using var reader = new StreamReader(top);
            Assert.Equal("alpha", await reader.ReadToEndAsync());
        }

        await using (var nested = await provider.OpenAsync("photos/2026/a.jpg", CancellationToken.None))
        {
            using var reader = new StreamReader(nested);
            Assert.Equal("beta", await reader.ReadToEndAsync());
        }
    }
}
