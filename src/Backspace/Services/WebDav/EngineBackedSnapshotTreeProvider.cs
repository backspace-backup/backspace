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
/// <see cref="ISnapshotTreeProvider"/> backed by a live
/// <see cref="IBackupEngine"/>. Resolves a snapshot's file tree once
/// (via <see cref="IBackupEngine.ListSnapshotFilesAsync"/>) and lazily
/// materialises individual files on GET via
/// <see cref="IBackupEngine.RestoreFilesAsync"/>. The materialised
/// files live in a local cache directory that the provider owns;
/// downloading a 1 GB snapshot to browse a single file no longer
/// costs 1 GB of disk.
///
/// <para>
/// Concurrency: PROPFIND traffic from Explorer is read-only against
/// the in-memory listing cache (safe for any concurrency). GET on a
/// not-yet-materialised file serialises through a per-path
/// <see cref="SemaphoreSlim"/> so two concurrent GETs for the same
/// file don't both spawn duplicate engine.RestoreFiles calls. Once
/// the file is in cache, subsequent GETs short-circuit to a plain
/// <see cref="FileStream"/>.
/// </para>
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
    private readonly string _bulkCacheDir;
    private string? _publishedBulkCacheDir;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perFileGates = new();
    // Per-directory listing cache. Key = source-relative dir path
    // (forward slashes, no leading slash; "" = root). Value = the
    // children of that dir, or null if the engine confirmed the dir
    // doesn't exist in this snapshot. Populated lazily — one RPC per
    // directory Explorer actually opens, not the whole tree upfront.
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
        _bulkCacheDir = Path.Combine(cacheDir, "bulk-complete");
        Directory.CreateDirectory(_cacheDir);
        if (Directory.Exists(_bulkCacheDir)) _publishedBulkCacheDir = _bulkCacheDir;
    }

    public async Task<IReadOnlyList<WebDavEntry>?> ListAsync(string relativeDir, CancellationToken ct)
    {
        var key = Normalize(relativeDir);
        return await ListDirAsync(key, ct).ConfigureAwait(false);
    }

    public async Task<WebDavEntry?> StatAsync(string relativePath, CancellationToken ct)
    {
        var key = Normalize(relativePath);
        // ROOT short-circuit: we always know "" is a directory. Don't
        // wait on a gRPC call for this — Explorer probes the root
        // before anything else, and blocking the WinFSP dispatcher
        // thread would push us toward the kernel's file-system
        // timeout.
        if (key.Length == 0)
            return new WebDavEntry("", IsDirectory: true, SizeBytes: 0, LastWriteUtc: DateTime.UtcNow);

        // For a non-root entry, look at the parent's listing — that's
        // where Explorer's enumeration already cached the child's
        // type/size, and re-using it avoids a second per-stat RPC.
        var lastSlash = key.LastIndexOf('/');
        var parent = lastSlash < 0 ? "" : key.Substring(0, lastSlash);
        var leaf = lastSlash < 0 ? key : key.Substring(lastSlash + 1);
        var siblings = await ListDirAsync(parent, ct).ConfigureAwait(false);
        if (siblings is null) return null;
        foreach (var s in siblings)
        {
            var sLeaf = s.RelativePath;
            var ss = sLeaf.LastIndexOf('/');
            if (ss >= 0) sLeaf = sLeaf.Substring(ss + 1);
            if (string.Equals(sLeaf, leaf, StringComparison.OrdinalIgnoreCase))
                return s;
        }
        return null;
    }

    public async Task<Stream> OpenAsync(string relativePath, CancellationToken ct)
    {
        var key = Normalize(relativePath);
        var entry = await StatAsync(key, ct).ConfigureAwait(false);
        if (entry is null || entry.IsDirectory)
            throw new FileNotFoundException("Not a regular file in this snapshot.", relativePath);

        var cachedPath = Path.Combine(_cacheDir, key.Replace('/', Path.DirectorySeparatorChar));

        // Read-count tripwire. Once Backspace has seen
        // <see cref="BulkRestoreReadThreshold"/> opens against the
        // same (source, revision), we conclude the user isn't
        // just sampling a single file — they're traversing or
        // copying — and switch to the bulk restore path (one restic
        // invocation for the whole subtree, ~seconds even for
        // thousands of files; vs. ~500 ms per file when we
        // restore one at a time, which is what made an Explorer
        // "Copy" of a 2500-file revision take ~2 hours).
        //
        // Below the threshold we stay on the per-file path. The
        // important property: opening ONE file out of a 100 GB
        // snapshot must not materialise 100 GB to disk. The cache
        // dir is on a known path
        // (BackrestDataDir/mount-cache/&lt;backup&gt;/&lt;src&gt;-r&lt;rev&gt;)
        // — anything we put there is visible to the user, so we
        // only put what they've actively opened.
        var reads = Interlocked.Increment(ref _readCount);
        if (TryGetCachedPath(key) is { } alreadyCached)
            return OpenCached(alreadyCached);
        if (reads >= BulkRestoreReadThreshold)
        {
            try { await EnsureBulkRestoredAsync(ct).WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _lifetime.IsCancellationRequested) { throw; }
            catch { /* fall through to per-file restore */ }
            if (TryGetCachedPath(key) is { } bulkCached)
                return OpenCached(bulkCached);
        }

        // Per-file gate so two concurrent GETs don't both trigger a
        // restore. The dictionary keeps the gate alive for the
        // lifetime of the provider; gate instances are tiny so the
        // memory cost is negligible vs the alternative (a per-call
        // gate that races with eviction).
        var gate = _perFileGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (TryGetCachedPath(key) is { } racedCached)
                return OpenCached(racedCached);
            if (!File.Exists(cachedPath))
            {
                var result = await WithReadLeaseAsync(token => _engine.RestoreFilesAsync(
                    _backup, _sourcePath, _destination, _storageName,
                    _revision, new[] { "/" + key.TrimStart('/') }, _cacheDir,
                    onProgress: null,
                    token,
                    expectedLeaves: new[] { key }, expectedSnapshotId: _snapshotId), ct).ConfigureAwait(false);

                if (result.Status is RestoreTerminalStatus.Error or RestoreTerminalStatus.Cancelled)
                    throw new IOException(result.ErrorMessage ?? "Engine reported restore failure.");

                if (!File.Exists(cachedPath))
                    throw new FileNotFoundException(
                        $"Engine did not materialise {relativePath} into the cache.", cachedPath);
            }
        }
        finally
        {
            gate.Release();
        }
        return OpenCached(cachedPath);
    }

    /// <summary>Background-walk the whole snapshot tree, populating
    /// <see cref="_dirCache"/> for every directory we discover.
    ///
    /// <para>Two paths, chosen at runtime:</para>
    /// <list type="bullet">
    /// <item><description><b>Bulk</b> — one restic invocation
    /// returning the full recursive tree (snapshot has ~2500 entries
    /// → ~1 s). Available for local destinations; the engine returns
    /// null for cloud and we fall back.</description></item>
    /// <item><description><b>Parallel walk</b> — 16-way concurrent
    /// per-directory RPCs. Used for cloud destinations where the
    /// bulk path would have to replicate Backrest's rclone setup.
    /// Still much faster than serial but each RPC is ~500 ms
    /// (Backrest fork+execs restic per call), so even with 16
    /// workers a deep tree takes tens of seconds.</description></item>
    /// </list>
    ///
    /// <para>Either path is best-effort and idempotent. TryAdd-style
    /// caching means concurrent lazy fetches don't conflict with the
    /// prefetch.</para></summary>
    public async Task PrefetchAllAsync(CancellationToken ct = default)
    {
        // Bulk path: one restic invocation, full recursive listing.
        // ~90× faster than the per-dir RPC walk for the user-reported
        // case (benchmarked on a real ~2500-entry snapshot: 962 ms vs
        // 86 s for the same tree).
        IReadOnlyList<RevisionTreeNode>? bulk;
        try
        {
            bulk = await WithReadLeaseAsync(token => _engine.ListSnapshotTreeAsync(
                _backup, _sourcePath, _destination, _storageName, _revision, token, _snapshotId), ct)
                .ConfigureAwait(false);
        }
        catch
        {
            bulk = null;
        }
        if (bulk is not null)
        {
            PopulateCacheFromBulk(bulk);
            return;
        }

        // Fallback: parallel per-dir walk.
        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = 16,
        };
        await WalkAsync("", options).ConfigureAwait(false);
    }

    /// <summary>Turn the engine's flat recursive node list into per-
    /// directory listings and atomically populate
    /// <see cref="_dirCache"/>. We use the same path-normalisation
    /// rules as the lazy path so a concurrent lazy fetch for a
    /// directory ends up with the SAME key — race-resolution then
    /// just falls out of <see cref="ConcurrentDictionary.TryAdd"/>
    /// (lazy wins if it raced ahead; bulk wins for cold keys; either
    /// way the cached value is the right set of children).</summary>
    private void PopulateCacheFromBulk(IReadOnlyList<RevisionTreeNode> nodes)
    {
        // Group children by parent dir. The source root has key "".
        var grouped = new Dictionary<string, List<WebDavEntry>>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = new(),
        };
        foreach (var node in nodes)
        {
            var path = Normalize(node.Path);
            if (path.Length == 0) continue; // the source-root entry itself
            var slash = path.LastIndexOf('/');
            var parent = slash < 0 ? "" : path.Substring(0, slash);
            if (!grouped.TryGetValue(parent, out var bucket))
            {
                bucket = new List<WebDavEntry>();
                grouped[parent] = bucket;
            }
            bucket.Add(new WebDavEntry(
                RelativePath: path,
                IsDirectory: node.IsDirectory,
                SizeBytes: node.SizeBytes,
                LastWriteUtc: node.ModifiedUtc));
            // Ensure every directory has a (possibly empty) bucket so
            // a later ListAsync on an empty directory returns "exists,
            // no children" instead of null ("doesn't exist").
            if (node.IsDirectory && !grouped.ContainsKey(path))
                grouped[path] = new List<WebDavEntry>();
        }

        foreach (var (dir, children) in grouped)
        {
            var completed = Task.FromResult<IReadOnlyList<WebDavEntry>?>(children);
            _dirCache.TryAdd(dir, new DirectoryFetch(completed, CancellationToken.None));
        }
    }

    private async Task WalkAsync(string dir, ParallelOptions options)
    {
        var children = await ListDirAsync(dir, options.CancellationToken).ConfigureAwait(false);
        if (children is null) return;
        var subdirs = children.Where(c => c.IsDirectory).Select(c => c.RelativePath).ToList();
        if (subdirs.Count == 0) return;
        await Parallel.ForEachAsync(subdirs, options, async (subdir, ct) =>
        {
            await WalkAsync(subdir, options).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    // Singleton "bulk restore in flight or done" task. Created the
    // first time OpenAsync sees enough reads to upgrade off the
    // per-file path. Reused by every later caller so we never run
    // two bulk restores per provider.
    private Task<bool>? _bulkRestoreTask;
    private readonly object _bulkRestoreLock = new();
    private long _readCount;

    /// <summary>How many OpenAsync calls we tolerate on the per-file
    /// path before promoting to a bulk subtree restore. Picked so
    /// that an Explorer "Copy whole revision" flips after the
    /// second-or-third file (Explorer issues opens almost
    /// instantaneously when it's enumerating a copy), but a
    /// single "user double-clicked one file from a giant snapshot"
    /// keeps the cache footprint small. Internal so tests can pin
    /// the threshold without exposing a runtime knob.</summary>
    internal const int BulkRestoreReadThreshold = 2;

    private Task<bool> EnsureBulkRestoredAsync(CancellationToken ct)
    {
        if (_bulkRestoreTask is { } existing) return existing;
        lock (_bulkRestoreLock)
        {
            if (_bulkRestoreTask is { } raced) return raced;
            // Detach from the caller's CT so a transient Explorer
            // open-cancel doesn't kill the bulk restore everyone else
            // is now sharing. The restore costs nothing once the
            // files are on disk; the next opener gets them for free.
            _bulkRestoreTask = Task.Run(async () =>
            {
                // No reader can see this directory until the engine confirms
                // complete success. Failed/preallocated files never masquerade
                // as usable files merely because File.Exists returns true.
                var privateCache = Path.Combine(Path.GetDirectoryName(_cacheDir)!, ".bulk-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var complete = await WithReadLeaseAsync(token => _engine.RestoreSnapshotSubtreeAsync(
                        _backup, _sourcePath, _destination, _storageName, _revision, privateCache,
                        token, _snapshotId), _lifetime.Token).ConfigureAwait(false);
                    if (!complete || _lifetime.IsCancellationRequested) return false;
                    // Directory rename publishes the complete cache atomically,
                    // including for later mounts of this exact same snapshot.
                    try { Directory.Move(privateCache, _bulkCacheDir); }
                    catch (IOException) when (Directory.Exists(_bulkCacheDir)) { }
                    Volatile.Write(ref _publishedBulkCacheDir, _bulkCacheDir);
                    return true;
                }
                finally
                {
                    // An unconfirmed engine stop retains staging and blocks
                    // maintenance until restart. Never remove its live output.
                    if (!ServiceLocator.Maintenance.IsBlocked)
                        try { if (Directory.Exists(privateCache)) Directory.Delete(privateCache, recursive: true); }
                        catch { /* unpublished scratch output is safe to retain */ }
                }
            });
            return _bulkRestoreTask;
        }
    }

    private string? TryGetCachedPath(string key)
    {
        var relative = key.Replace('/', Path.DirectorySeparatorChar);
        var perFile = Path.Combine(_cacheDir, relative);
        if (File.Exists(perFile)) return perFile;
        var bulkRoot = Volatile.Read(ref _publishedBulkCacheDir);
        if (bulkRoot is null) return null;
        var bulkFile = Path.Combine(bulkRoot, relative);
        return File.Exists(bulkFile) ? bulkFile : null;
    }

    public void Dispose()
    {
        // In-flight RPCs must acknowledge cancellation before their read
        // lease ends. Do not dispose a semaphore another opener will release.
        _lifetime.Cancel();
    }

    // ---- internals ----

    /// <summary>Lazy per-directory listing. Coalesces concurrent
    /// requests for the same dir via <see cref="ConcurrentDictionary.GetOrAdd"/>
    /// — the FIRST caller for a given key starts the engine RPC; later
    /// callers await the same Task. Subsequent calls hit the in-memory
    /// cache.</summary>
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
        var result = new List<WebDavEntry>(children.Count);
        foreach (var c in children)
        {
            result.Add(new WebDavEntry(
                RelativePath: prefix + c.Name,
                IsDirectory: c.IsDirectory,
                SizeBytes: c.SizeBytes,
                LastWriteUtc: c.ModifiedUtc));
        }
        return result;
    }

    private async Task<T> WithReadLeaseAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        using var lease = await ServiceLocator.Maintenance.AcquireReadAsync(linked.Token, linked.Cancel).ConfigureAwait(false);
        return await operation(linked.Token).ConfigureAwait(false);
    }

    private static string Normalize(string path)
        => string.IsNullOrEmpty(path) ? "" : path.Replace('\\', '/').Trim('/');

    private static FileStream OpenCached(string path)
    {
        // A restored link points at today's local filesystem, not the chosen
        // snapshot's bytes. Never follow it while presenting a read-only backup.
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("This snapshot contains a linked file or folder. Backspace cannot open links from a mounted backup because they may point outside the snapshot. Use Restore to recover the link itself.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
    }
}

