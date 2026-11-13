using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;

namespace Backspace.Services.WebDav;

/// <summary>
/// One snapshot of one source, as the mounted drive shows it (U158).
///
/// <para><b>Folders.</b> A snapshot never changes, so its whole tree is read
/// once, with one engine listing (<see cref="IBackupEngine.ListSnapshotTreeAsync"/>,
/// a single restic process), and kept in memory as a <see cref="SnapshotTree"/>.
/// Every later listing, look-up and size sweep is answered from memory. The
/// mount reads the tree of every revision the person chose before the drive
/// appears, and no other (U162). Asking the
/// engine one folder at a time (a restic process per folder, plus a cloud
/// connection check for each on a cloud destination) is only the fallback for
/// an engine that cannot list a whole tree.</para>
///
/// <para><b>Files.</b> A file's bytes are downloaded into a private cache the
/// first time it is read and read from there afterwards. Opening returns at
/// once; while the download runs a read waits only for the bytes it asks for
/// (U167, <see cref="ProgressiveFile"/>). Only files that are
/// opened are fetched: never their folder's other files, and never the whole
/// snapshot (the owner, 7 November 2026: opening one file from a 190 GB backup
/// must not download anything else).</para>
/// </summary>
public sealed class EngineBackedSnapshotTreeProvider : ISnapshotTreeProvider, IDisposable
{
    private readonly IBackupEngine _engine;
    private readonly Backup _backup;
    private readonly string _sourcePath;
    private readonly Destination _destination;
    private readonly string _storageName;
    private readonly int _revision;
    private readonly string? _snapshotId;
    private readonly string _cacheDir;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _partialDir;
    private readonly object _fetchGate = new();
    private readonly Dictionary<string, ProgressiveFile> _fetches = new(StringComparer.Ordinal);

    private readonly object _treeGate = new();
    private Task<SnapshotTree?>? _treeTask;
    private SnapshotTree? _tree;
    private bool _treeUnavailable;
    private int _treeFilesRead;
    private readonly ConcurrentDictionary<SnapshotTree.Node, IReadOnlyList<WebDavEntry>> _listings = new();

    // Folder batches: the folder's path → the restore fetching its files.

    // Fallback only (an engine without whole-tree listings).
    private sealed record DirectoryFetch(Task<IReadOnlyList<WebDavEntry>?> Task, CancellationToken OwnerToken);
    private readonly ConcurrentDictionary<string, DirectoryFetch> _dirCache = new();
    private readonly object _dirCacheGate = new();

    public EngineBackedSnapshotTreeProvider(
        IBackupEngine engine, Backup backup, string sourcePath, Destination destination,
        string storageName, int revision, string cacheDir, string? snapshotId = null)
    {
        _engine = engine;
        _backup = backup;
        _sourcePath = sourcePath;
        _destination = destination;
        _storageName = storageName;
        _revision = revision;
        _snapshotId = snapshotId;
        _cacheDir = Path.Combine(cacheDir, "files");
        Directory.CreateDirectory(_cacheDir);
        _partialDir = Path.Combine(cacheDir, "partial");
        RemoveAbandonedDownloads(_partialDir);
    }

    /// <summary>The snapshot's tree once read; null before, or when the
    /// engine can't list whole trees.</summary>
    public SnapshotTree? Tree => Volatile.Read(ref _tree);

    /// <summary>Files (not folders) read so far by the listing in progress,
    /// for progress (U166). Never goes backwards within one listing.</summary>
    public int TreeFilesRead => Volatile.Read(ref _treeFilesRead);

    /// <summary>Reads the whole tree once; every caller shares the one engine
    /// listing. Null when the engine can't list whole trees (the per-folder
    /// fallback then answers). A cancelled or failed listing is retried by
    /// the next caller.</summary>
    public async Task<SnapshotTree?> LoadTreeAsync(CancellationToken ct)
    {
        Task<SnapshotTree?> task;
        lock (_treeGate)
        {
            if (_treeUnavailable) return null;
            if (_treeTask is null || _treeTask.IsFaulted || _treeTask.IsCanceled)
                _treeTask = ReadTreeAsync();
            task = _treeTask;
        }
        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<SnapshotTree?> ReadTreeAsync()
    {
        // Detached from any one caller: a cancelled Explorer request must not
        // waste a listing others are waiting for. Unmounting cancels it.
        Volatile.Write(ref _treeFilesRead, 0);
        var nodes = await WithReadLeaseAsync(token => _engine.ListSnapshotTreeAsync(
            _backup, _sourcePath, _destination, _storageName, _revision, token, _snapshotId,
            new Progress<int>(count =>
            {
                for (var seen = Volatile.Read(ref _treeFilesRead); count > seen;)
                {
                    var was = Interlocked.CompareExchange(ref _treeFilesRead, count, seen);
                    if (was == seen) break;
                    seen = was;
                }
            })), _lifetime.Token).ConfigureAwait(false);
        if (nodes is null)
        {
            lock (_treeGate) _treeUnavailable = true;
            return null;
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var tree = await Task.Run(() => SnapshotTree.Build(nodes)).ConfigureAwait(false);
        AppLogger.For<EngineBackedSnapshotTreeProvider>().Information(
            "Mounted snapshot tree: {Files} files, {Folders} folders, built in {Ms} ms", tree.FileCount, tree.DirectoryCount, clock.ElapsedMilliseconds);
        Volatile.Write(ref _tree, tree);
        return tree;
    }

    public async Task<IReadOnlyList<WebDavEntry>?> ListAsync(string relativeDir, CancellationToken ct)
    {
        var key = Normalize(relativeDir);
        var tree = Tree ?? await LoadTreeAsync(ct).ConfigureAwait(false);
        if (tree is null) return await ListDirAsync(key, ct).ConfigureAwait(false);
        var node = tree.Find(key);
        return node is { IsDirectory: true } ? Listing(node, key) : null;
    }

    public async Task<WebDavEntry?> StatAsync(string relativePath, CancellationToken ct)
    {
        var key = Normalize(relativePath);
        // Explorer probes the root before anything else; never wait for it.
        if (key.Length == 0)
            return new WebDavEntry("", IsDirectory: true, SizeBytes: 0, LastWriteUtc: Tree?.Root.ModifiedUtc is { } t && t != DateTime.MinValue ? t : DateTime.UtcNow);
        var tree = Tree ?? await LoadTreeAsync(ct).ConfigureAwait(false);
        if (tree is not null)
            // The snapshot's own spelling of the name: the file cache is keyed by it.
            return tree.Find(key) is { } node ? new WebDavEntry(node.Path, node.IsDirectory, node.SizeBytes, node.ModifiedUtc) : null;

        var lastSlash = key.LastIndexOf('/');
        var parent = lastSlash < 0 ? "" : key[..lastSlash];
        var leaf = lastSlash < 0 ? key : key[(lastSlash + 1)..];
        var siblings = await ListDirAsync(parent, ct).ConfigureAwait(false);
        return siblings?.FirstOrDefault(s => string.Equals(LeafOf(s.RelativePath), leaf, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<Stream> OpenAsync(string relativePath, CancellationToken ct)
    {
        var key = Normalize(relativePath);
        var entry = await StatAsync(key, ct).ConfigureAwait(false);
        if (entry is null || entry.IsDirectory)
            throw new FileNotFoundException("Not a regular file in this snapshot.", relativePath);
        key = entry.RelativePath; // the snapshot's own spelling of the name
        if (entry.SizeBytes == 0) return new MemoryStream(Array.Empty<byte>(), writable: false);
        return OpenFetched(key, entry.SizeBytes);
    }

    /// <summary>How long a download continues once nobody has the file open:
    /// a program that closes and reopens it (Explorer does) keeps it.</summary>
    internal TimeSpan FetchIdleGrace { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The cached file, or a stream over its download (U167): returns
    /// at once; a read waits only for the bytes it asks for. Everyone opening
    /// the file meanwhile shares the one download.</summary>
    private Stream OpenFetched(string key, long length)
    {
        lock (_fetchGate)
        {
            if (_fetches.TryGetValue(key, out var running))
            {
                if (running.TryOpenReader() is { } reader) return reader;
                _fetches.Remove(key);
            }
            if (TryGetCachedPath(key) is { } cached) return OpenCached(cached);
            var file = ProgressiveFile.Start(_partialDir, CachePath(key), length,
                (output, token) => FetchAsync(key, output, token), _lifetime.Token, FetchIdleGrace, ended =>
                {
                    lock (_fetchGate)
                        if (_fetches.TryGetValue(key, out var current) && ReferenceEquals(current, ended)) _fetches.Remove(key);
                });
            _fetches[key] = file;
            return file.TryOpenReader() ?? throw new IOException("The download of this file ended before it began.");
        }
    }

    /// <summary>Writes the file's bytes to <paramref name="output"/>: straight
    /// from the destination as they arrive (restic dump), or, for an engine
    /// that cannot, or a snapshot without its whole tree in memory (which
    /// could hold links), restored first into a private folder.</summary>
    private async Task FetchAsync(string key, Stream output, CancellationToken ct)
    {
        if (Tree is not null && await WithReadLeaseAsync(token => _engine.DumpSnapshotFileAsync(
                _backup, _sourcePath, _destination, _storageName, _revision, key, output, token, _snapshotId), ct).ConfigureAwait(false))
            return;
        var staging = Path.Combine(_partialDir, Guid.NewGuid().ToString("N"));
        try
        {
            var result = await WithReadLeaseAsync(token => _engine.RestoreFilesAsync(
                _backup, _sourcePath, _destination, _storageName,
                _revision, new[] { "/" + key }, staging,
                onProgress: null, token,
                expectedLeaves: new[] { key }, expectedSnapshotId: _snapshotId), ct).ConfigureAwait(false);
            if (result.Status is RestoreTerminalStatus.Error or RestoreTerminalStatus.Cancelled)
                throw new IOException(result.ErrorMessage ?? "Engine reported restore failure.");
            var restored = Path.Combine(staging, key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(restored))
                throw new FileNotFoundException($"Engine did not materialise {key} into the cache.", key);
            await using var input = OpenCached(restored);
            await input.CopyToAsync(output, ct).ConfigureAwait(false);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    /// <summary>Removes downloads a previous session left unfinished. A file
    /// still open (by a mount of the same snapshot) is left alone.</summary>
    private static void RemoveAbandonedDownloads(string partialDir)
    {
        if (!Directory.Exists(partialDir)) return;
        foreach (var part in Directory.EnumerateFiles(partialDir, "*.part"))
            try { using var _ = new FileStream(part, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        // In-flight engine calls acknowledge cancellation before their read
        // lease ends. Do not dispose a semaphore another opener will release.
        _lifetime.Cancel();
    }

    // ---- tree answers ----

    private IReadOnlyList<WebDavEntry> Listing(SnapshotTree.Node node, string key)
        => _listings.GetOrAdd(node, n =>
        {
            var prefix = key.Length == 0 ? "" : key + "/";
            var children = n.Children;
            var entries = new WebDavEntry[children.Count];
            for (var i = 0; i < entries.Length; i++)
            {
                var child = children[i];
                entries[i] = new WebDavEntry(prefix + child.Name, child.IsDirectory, child.SizeBytes, child.ModifiedUtc);
            }
            return entries;
        });

    // ---- fallback: one folder at a time ----

    private async Task<IReadOnlyList<WebDavEntry>?> ListDirAsync(string normalizedKey, CancellationToken ct)
    {
        DirectoryFetch fetch;
        lock (_dirCacheGate)
        {
            if (_dirCache.TryGetValue(normalizedKey, out var existing)
                && (existing.OwnerToken.IsCancellationRequested || existing.Task.IsCanceled || existing.Task.IsFaulted))
                _dirCache.TryRemove(normalizedKey, out _);
            fetch = _dirCache.GetOrAdd(normalizedKey, k => new DirectoryFetch(FetchDirAsync(k, ct), ct));
        }
        try { return await fetch.Task.WaitAsync(ct).ConfigureAwait(false); }
        catch
        {
            lock (_dirCacheGate)
                if ((fetch.OwnerToken.IsCancellationRequested || fetch.Task.IsCanceled || fetch.Task.IsFaulted)
                    && _dirCache.TryGetValue(normalizedKey, out var current) && ReferenceEquals(current, fetch))
                    _dirCache.TryRemove(normalizedKey, out _);
            throw;
        }
    }

    private async Task<IReadOnlyList<WebDavEntry>?> FetchDirAsync(string normalizedKey, CancellationToken ct)
    {
        var children = await WithReadLeaseAsync(token => _engine.ListSnapshotDirectoryAsync(
            _backup, _sourcePath, _destination, _storageName, _revision, normalizedKey, token, _snapshotId), ct)
            .ConfigureAwait(false);
        if (children is null) return null;
        var prefix = normalizedKey.Length == 0 ? "" : normalizedKey + "/";
        return children.Select(c => new WebDavEntry(prefix + c.Name, c.IsDirectory, c.SizeBytes, c.ModifiedUtc)).ToList();
    }

    // ---- helpers ----

    private async Task<T> WithReadLeaseAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        using var lease = await ServiceLocator.Maintenance.AcquireReadAsync(linked.Token, linked.Cancel).ConfigureAwait(false);
        return await operation(linked.Token).ConfigureAwait(false);
    }

    private string CachePath(string key) => Path.Combine(_cacheDir, key.Replace('/', Path.DirectorySeparatorChar));

    private string? TryGetCachedPath(string key)
    {
        var path = CachePath(key);
        return File.Exists(path) ? path : null;
    }

    private static string LeafOf(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx >= 0 ? path[(idx + 1)..] : path;
    }

    private static string Normalize(string path)
        => string.IsNullOrEmpty(path) ? "" : path.Replace('\\', '/').Trim('/');

    private static FileStream OpenCached(string path)
    {
        // A restored link points at today's local filesystem, not the chosen
        // snapshot's bytes. Never follow it while presenting a read-only backup.
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0 && !ResticEngine.IsSystemLink(current))
                throw new IOException("This snapshot contains a linked file or folder. Backspace cannot open links from a mounted backup because they may point outside the snapshot. Use Restore to recover the link itself.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: false);
    }
}
