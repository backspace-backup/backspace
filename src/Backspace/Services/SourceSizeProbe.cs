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
/// Used by the BackupEditor's source chips and the backup cards to give
/// the user an at-a-glance estimate of how big a backup will be, counted
/// with that backup's exclusion rules — purely informational, never
/// blocks any flow.
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

    // Sizes a backup measured (restic's own totals), whatever rules counted them.
    private readonly ConcurrentDictionary<string, SourceSizeResult> _measured =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cached result for the path, counted with a backup's
    /// <paramref name="rules"/>, if a probe has already run (or a backup
    /// measured it). Returns null if no probe has happened yet.</summary>
    public SourceSizeResult? TryGetCached(string path, SourceSizeRules? rules = null) =>
        _cache.TryGetValue(CacheKey(path, rules), out var r) ? r
        : _measured.TryGetValue(NormalizeKey(path), out var m) ? m : null;

    /// <summary>
    /// Returns a cached result if available, otherwise runs the walk
    /// and caches the outcome. Same path and rules called concurrently
    /// share the in-flight Task — no double-walk.
    /// <paramref name="progress"/> receives running-total updates
    /// during the walk; only the FIRST caller for a given path that
    /// supplies one will see callbacks (in-flight task is shared).
    /// </summary>
    public async Task<SourceSizeResult> GetOrComputeAsync(
        string path,
        CancellationToken ct,
        IProgress<SourceSizeProgress>? progress = null,
        SourceSizeRules? rules = null)
    {
        rules ??= SourceSizeRules.None;
        if (TryGetCached(path, rules) is { } cached)
            return cached;

        // The shared compute uses CancellationToken.None deliberately:
        // earlier we passed the *first* caller's token into Task.Run, so
        // a second caller awaiting the same path would observe a Task
        // cancelled by someone else's CTS even though their own request
        // was still alive. MaxScanDuration is the real bound on runtime;
        // each caller observes its own cancellation via WaitAsync below
        // without tearing down the walk for the others.
        var task = _inflight.GetOrAdd(CacheKey(path, rules), k => Task.Run(async () =>
        {
            try
            {
                var result = await ComputeAsync(path, rules, CancellationToken.None, progress);
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

    /// <summary>Drops the cache entries for <paramref name="path"/>. Call
    /// after a backup runs so the next size probe reflects fresh state.</summary>
    public void Invalidate(string path)
    {
        var prefix = NormalizeKey(path) + "|";
        foreach (var key in _cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            _cache.TryRemove(key, out SourceSizeResult _);
        _measured.TryRemove(NormalizeKey(path), out SourceSizeResult _);
    }

    /// <summary>
    /// Records a byte-size the backup itself measured for
    /// <paramref name="path"/> (restic's total at the end of a backup,
    /// which applied the backup's rules) — strictly more accurate than
    /// our walked estimate AND avoids re-walking the tree. Without this
    /// the cache is set on first probe and never refreshed for the app's
    /// lifetime, so a source that grows from 5 GB to 50 GB stays at 5 GB
    /// until the user reopens the BackupEditor.
    ///
    /// File count is left at its previous value if we don't have a fresh
    /// count to apply (a stale count is preferable to a zero).
    /// </summary>
    public void UpdateCachedBytes(string path, long bytes, long? fileCount = null)
    {
        if (string.IsNullOrWhiteSpace(path) || bytes < 0) return;
        var key = NormalizeKey(path);
        var prev = _measured.TryGetValue(key, out var existing) ? existing : default;
        var files = fileCount ?? (prev.Completed ? prev.FileCount : 0);
        _measured[key] = new SourceSizeResult(path, bytes, files, Completed: true, Truncated: false);
        _log.Debug("Source size updated from runtime measurement: {Path} → {Bytes:N0} bytes",
            path, bytes);
    }

    private async Task<SourceSizeResult> ComputeAsync(
        string path,
        SourceSizeRules rules,
        CancellationToken ct,
        IProgress<SourceSizeProgress>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return new SourceSizeResult(path, 0, 0, false, false);

        // With the background service running, it counts, with the rights of
        // its backups, and keeps the count for them: the same folder isn't
        // counted twice (U129, 4 November 2026).
        if (Elevated is { } service)
        {
            try
            {
                if (await service(path, rules, ct).ConfigureAwait(false) is { Completed: true } counted) return counted;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Debug(ex, "The background service could not measure {Path}; counting here", path);
            }
        }

        var count = new Count();
        var root = TreeWalk.Root(path);
        var walk = Task.Run(() => CountIncluded(root, rules, DateTime.UtcNow + MaxScanDuration, ct, count), ct);

        while (!walk.IsCompleted)
        {
            await Task.WhenAny(walk, Task.Delay(ProgressInterval, ct));
            if (!walk.IsCompleted)
                progress?.Report(new SourceSizeProgress(Interlocked.Read(ref count.Bytes), Interlocked.Read(ref count.Files)));
        }
        var truncated = !await walk;
        var bytes = count.Bytes;
        var result = new SourceSizeResult(path, bytes, count.Files, true, truncated,
            UnreadableFolders: count.Unreadable, UnreadableExample: count.UnreadableExample, CloudOnlyFiles: count.CloudOnly);

        // A whole drive too big to list in time: the space it uses is a
        // close, instant answer (it also counts the page file and other
        // system files that backups leave out). Not with exclusion rules:
        // they can leave out most of a drive, so the space used says
        // nothing about the backup (U123).
        if (result.Truncated && TreeWalk.IsDriveRoot(root) && !rules.HasRules)
        {
            try
            {
                var drive = new DriveInfo(root);
                var used = drive.TotalSize - drive.TotalFreeSpace;
                if (used > bytes) result = result with { Bytes = used, Approximate = true };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        _log.Debug("Source size for {Path}: {Bytes:N0} bytes, {Files:N0} files, truncated={Truncated}, approximate={Approximate}, rules={Rules}",
            path, result.Bytes, result.FileCount, truncated, result.Approximate, rules.HasRules);
        return result;
    }

    /// <summary>Measures a folder with the background service's rights, or
    /// returns null when there is no such service (set by the app on Windows).</summary>
    internal static Func<string, SourceSizeRules, CancellationToken, Task<SourceSizeResult?>>? Elevated;

    /// <summary>Running totals of <see cref="CountIncluded"/>.</summary>
    internal sealed class Count
    {
        public long Bytes, Files, CloudOnly;
        public int Unreadable;
        public string? UnreadableExample;
    }

    /// <summary>
    /// Counts what a backup of <paramref name="root"/> holds: files its
    /// exclusion rules leave out (a Dropbox folder, caches) are not counted,
    /// nor are files kept only in the cloud. Without a backup's rules, the
    /// Essentials a new backup gets apply (pagefile.sys…).
    /// Excluded folders are not even read (U123, 4 November 2026: C:\ showed
    /// 791 GB because its own rules were ignored). Shared by the app and the
    /// background service.
    /// </summary>
    /// <returns>False when <paramref name="deadline"/> passed before the end.</returns>
    internal static bool CountIncluded(string root, SourceSizeRules rules, DateTime deadline, CancellationToken ct, Count count)
    {
        var compiled = FilterTranslator.CompileRules(rules.Text);
        bool Excluded(string fullPath, bool directory)
            => compiled.Count > 0 && FilterTranslator.Evaluate(compiled, FilterTranslator.ResticPath(fullPath) + (directory ? "/" : "")).Excluded;
        return TreeWalk.Walk(root, (entry, _) =>
        {
            if (Excluded(entry.FullPath, entry.IsDirectory)) return null;
            if (entry.IsDirectory) return false;
            if (rules.SkipCloudOnly && TreeWalk.IsCloudOnly(entry.Attributes))
            {
                Interlocked.Increment(ref count.CloudOnly);
                return null;
            }
            Interlocked.Add(ref count.Bytes, entry.Length);
            Interlocked.Increment(ref count.Files);
            return null;
        }, deadline, ct, dir =>
        {
            if (Interlocked.Increment(ref count.Unreadable) == 1) count.UnreadableExample = dir;
        }, (entry, _) =>
        {
            // Placeholders of files kept only in the cloud are links: never
            // read, so never counted, whatever the backup does with them.
            if (!entry.IsDirectory && TreeWalk.IsCloudOnly(entry.Attributes) && !Excluded(entry.FullPath, false))
                Interlocked.Increment(ref count.CloudOnly);
        });
    }

    private static string CacheKey(string path, SourceSizeRules? rules) => NormalizeKey(path) + "|" + (rules ?? SourceSizeRules.None).Key;

    private static string NormalizeKey(string path)
    {
        try { return TreeWalk.Root(path); }
        catch { return path; }
    }
}

/// <summary>What a backup leaves out of its folders when counting their size
/// (U123): its exclusion rules and, when it skips them, files kept only in
/// the cloud.</summary>
public sealed record SourceSizeRules(string? FiltersText, bool SkipCloudOnly)
{
    /// <summary>No backup yet: a new backup's Essentials (pagefile.sys…)
    /// apply, and cloud placeholders are not counted.</summary>
    public static SourceSizeRules None { get; } = new(null, false);

    public static SourceSizeRules For(Models.Backup backup) => new(backup.FiltersText ?? "", backup.ExcludeCloudFiles);

    internal bool HasRules => !string.IsNullOrWhiteSpace(FiltersText);

    /// <summary>The rules that apply: the backup's Filters text, as restic
    /// reads it, or the Essentials without a backup.</summary>
    internal string Text => FiltersText ?? RecommendedFilters.EssentialsText();

    // Identifies the rules in the cache without keeping long rule texts as keys.
    internal string Key { get; } = (SkipCloudOnly ? "c" : "a") + (FiltersText is null ? ""
        : "t" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(FiltersText.Replace("\r\n", "\n")))));
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
/// <param name="CloudOnlyFiles">Files kept only in the cloud, not counted.</param>
public readonly record struct SourceSizeResult(
    string Path,
    long Bytes,
    long FileCount,
    bool Completed,
    bool Truncated,
    bool Approximate = false,
    int UnreadableFolders = 0,
    string? UnreadableExample = null,
    bool MeasuredByService = false,
    long CloudOnlyFiles = 0)
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

    /// <summary>Files kept only in the cloud, left out of the size, or null (U123).</summary>
    public string? CloudOnlyNote => CloudOnlyFiles switch
    {
        0 => null,
        1 => "1 online-only file isn't counted: it is kept only in the cloud.",
        _ => $"{CloudOnlyFiles:N0} online-only files aren't counted: they are kept only in the cloud.",
    };
}

/// <summary>Running-total snapshot fired periodically by the walker
/// while a probe is still in flight. Lets the UI replace the static
/// "Estimating size…" placeholder with a growing "12.3 MB · 412
/// files…" indicator so the user can see the walk is making progress.</summary>
public readonly record struct SourceSizeProgress(long Bytes, long FileCount);
