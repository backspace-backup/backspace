using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Backspace.Services;

/// <summary>
/// Walks a directory tree and reports total bytes + file count, async
/// and cancellable. Caches results so a chip displaying the size of
/// <c>C:\Users\me\Documents</c> doesn't re-walk every time the
/// containing view re-renders.
///
/// Used by the BackupEditor's source chips and the onboarding wizard
/// to give the user an at-a-glance estimate of how big a backup will
/// be — purely informational, never blocks any flow.
///
/// Defensive choices for Windows quirks:
///   - Skip reparse points (junctions, symlinks) — we'd otherwise
///     traverse the whole drive via <c>%SystemDrive%\Documents and
///     Settings → Users</c> and similar legacy junctions.
///   - Catch UnauthorizedAccessException per-directory; users may have
///     read-denied subdirs deep in their tree.
///   - Time-cap each probe at <see cref="MaxScanDuration"/> so we never
///     leave the UI showing "estimating…" forever on huge drives.
/// </summary>
public sealed class SourceSizeProbe
{
    private static ILogger _log => AppLogger.For<SourceSizeProbe>();

    /// <summary>Per-source cap on how long a scan runs before giving
    /// up with a partial result. Generous enough for typical user-
    /// data folders; the result is just an estimate either way.</summary>
    public TimeSpan MaxScanDuration { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>How often the walker fires an in-progress
    /// <see cref="SourceSizeProgress"/> update so the UI can show a
    /// running total instead of a static "Estimating size…" string
    /// that reads as stuck.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    private readonly ConcurrentDictionary<string, SourceSizeResult> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cached result for the path if a probe has already run.
    /// Returns null if no probe has happened yet.</summary>
    public SourceSizeResult? TryGetCached(string path) =>
        _cache.TryGetValue(NormalizeKey(path), out var r) ? r : null;

    /// <summary>
    /// Returns a cached result if available, otherwise runs the walk
    /// and caches the outcome. Same path called concurrently shares
    /// the in-flight Task — no double-walk.
    /// <paramref name="progress"/> receives running-total updates
    /// during the walk; only the FIRST caller for a given path that
    /// supplies one will see callbacks (in-flight task is shared).
    /// </summary>
    public async Task<SourceSizeResult> GetOrComputeAsync(
        string path,
        CancellationToken ct,
        IProgress<SourceSizeProgress>? progress = null)
    {
        var key = NormalizeKey(path);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        // The shared compute uses CancellationToken.None deliberately:
        // earlier we passed the *first* caller's token into Task.Run, so
        // a second caller awaiting the same path would observe a Task
        // cancelled by someone else's CTS even though their own request
        // was still alive. MaxScanDuration is the real bound on runtime;
        // each caller observes its own cancellation via WaitAsync below
        // without tearing down the walk for the others.
        var task = _inflight.GetOrAdd(key, k => Task.Run(async () =>
        {
            try
            {
                var result = await ComputeAsync(path, CancellationToken.None, progress);
                _cache[k] = result;
                return result;
            }
            finally
            {
                _inflight.TryRemove(k, out Task<SourceSizeResult>? _);
            }
        }));

        return await task.WaitAsync(ct);
    }
    private readonly ConcurrentDictionary<string, Task<SourceSizeResult>> _inflight =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Drops the cache entry for <paramref name="path"/>. Call
    /// after a backup runs so the next size probe reflects fresh state.</summary>
    public void Invalidate(string path) => _cache.TryRemove(NormalizeKey(path), out SourceSizeResult _);

    /// <summary>
    /// Overwrites the cached byte-size for <paramref name="path"/> with
    /// a fresh measurement (e.g. parsed from Duplicacy's
    /// <c>Indexed N files (M bytes)</c> stdout line — which Duplicacy
    /// itself just measured during indexing, so it's strictly more
    /// accurate than our walked estimate AND avoids re-walking the
    /// tree). Without this the cache is set on first probe and never
    /// refreshed for the app's lifetime, so a source that grows from
    /// 5 GB to 50 GB stays at 5 GB until the user reopens the
    /// BackupEditor.
    ///
    /// File count is left at its previous cached value if we don't
    /// have a fresh count to apply (the byte-only path doesn't always
    /// carry a file count, and a stale count is preferable to a zero).
    /// </summary>
    public void UpdateCachedBytes(string path, long bytes, long? fileCount = null)
    {
        if (string.IsNullOrWhiteSpace(path) || bytes < 0) return;
        var key = NormalizeKey(path);
        var prev = _cache.TryGetValue(key, out var existing) ? existing : default;
        var files = fileCount ?? (prev.Completed ? prev.FileCount : 0);
        _cache[key] = new SourceSizeResult(path, bytes, files, Completed: true, Truncated: false);
        _log.Debug("Source size updated from runtime measurement: {Path} → {Bytes:N0} bytes",
            path, bytes);
    }

    private async Task<SourceSizeResult> ComputeAsync(
        string path,
        CancellationToken ct,
        IProgress<SourceSizeProgress>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return new SourceSizeResult(path, 0, 0, false, false);

        long bytes = 0;
        long files = 0;
        var unreadable = 0;
        string? unreadableExample = null;
        var root = TreeWalk.Root(path);
        var walk = Task.Run(() => TreeWalk.Walk(root, (entry, _) =>
        {
            // Never backed up (System Volume Information, pagefile.sys…), so never counted.
            if (RecommendedFilters.IsAlwaysExcludedAtRoot(root, entry.FullPath, entry.IsDirectory)) return null;
            if (entry.IsDirectory) return false;
            Interlocked.Add(ref bytes, entry.Length);
            Interlocked.Increment(ref files);
            return null;
        }, DateTime.UtcNow + MaxScanDuration, ct, dir =>
        {
            if (Interlocked.Increment(ref unreadable) == 1) unreadableExample = dir;
        }), ct);

        while (!walk.IsCompleted)
        {
            await Task.WhenAny(walk, Task.Delay(ProgressInterval, ct));
            if (!walk.IsCompleted)
                progress?.Report(new SourceSizeProgress(Interlocked.Read(ref bytes), Interlocked.Read(ref files)));
        }
        var truncated = !await walk;
        var result = new SourceSizeResult(path, bytes, files, true, truncated,
            UnreadableFolders: unreadable, UnreadableExample: unreadableExample);

        // Folders this account can't open (another account's profile) are
        // backed up by the background service, which can read them: ask it
        // to measure with its rights (U106). Without it, say what was missed.
        if (unreadable > 0 && Elevated is { } elevated)
        {
            try
            {
                if (await elevated(path, ct).ConfigureAwait(false) is { Completed: true } measured && measured.Bytes >= bytes)
                    result = measured;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Debug(ex, "The background service could not measure {Path}", path);
            }
        }

        // A whole drive too big to list in time: the space it uses is a
        // close, instant answer (it also counts the page file and other
        // system files that backups leave out).
        if (result.Truncated && TreeWalk.IsDriveRoot(root))
        {
            try
            {
                var drive = new DriveInfo(root);
                var used = drive.TotalSize - drive.TotalFreeSpace;
                if (used > bytes) result = result with { Bytes = used, Approximate = true };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        _log.Debug("Source size for {Path}: {Bytes:N0} bytes, {Files:N0} files, truncated={Truncated}, approximate={Approximate}",
            path, result.Bytes, files, truncated, result.Approximate);
        return result;
    }

    /// <summary>Measures a folder with the background service's rights, or
    /// returns null when there is no such service (set by the app on Windows).</summary>
    internal static Func<string, CancellationToken, Task<SourceSizeResult?>>? Elevated;

    private static string NormalizeKey(string path)
    {
        try { return TreeWalk.Root(path); }
        catch { return path; }
    }
}

/// <param name="Path">Folder probed.</param>
/// <param name="Bytes">Total bytes of regular files (reparse points skipped).</param>
/// <param name="FileCount">Files counted.</param>
/// <param name="Completed">True once the walk finished. False = not yet probed.</param>
/// <param name="Truncated">True if the time cap kicked in before we finished.</param>
/// <param name="Approximate">True when <paramref name="Bytes"/> is the space
/// used on the whole drive, because listing it took too long.</param>
/// <param name="UnreadableFolders">Folders that couldn't be opened, so their
/// contents aren't counted.</param>
/// <param name="UnreadableExample">The first of them, to name in the interface.</param>
/// <param name="MeasuredByService">Measured by the background service, with the
/// same rights as its backups.</param>
public readonly record struct SourceSizeResult(
    string Path,
    long Bytes,
    long FileCount,
    bool Completed,
    bool Truncated,
    bool Approximate = false,
    int UnreadableFolders = 0,
    string? UnreadableExample = null,
    bool MeasuredByService = false)
{
    /// <summary>Folders the backup will leave out because they can't be opened,
    /// or null when there are none (U106). The size doesn't count them, and
    /// rightly: the backup won't hold them either.</summary>
    public string? UnreadableNote
    {
        get
        {
            if (UnreadableFolders == 0) return null;
            var one = UnreadableFolders == 1;
            var what = one ? "1 folder won't be backed up because" : $"{UnreadableFolders:N0} folders won't be backed up because";
            if (MeasuredByService) return $"{what} {(one ? "it" : "they")} can't be opened.";
            var where = UnreadableExample is null ? "" : one ? $": {UnreadableExample}" : $", such as {UnreadableExample}";
            return $"{what} Backspace can't open {(one ? "it" : "them")}{where}. Turn on administrator access in Settings to include {(one ? "it" : "them")}.";
        }
    }
}

/// <summary>Running-total snapshot fired periodically by the walker
/// while a probe is still in flight. Lets the UI replace the static
/// "Estimating size…" placeholder with a growing "12.3 MB · 412
/// files…" indicator so the user can see the walk is making progress.</summary>
public readonly record struct SourceSizeProgress(long Bytes, long FileCount);
