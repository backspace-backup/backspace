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
/// Composite snapshot tree exposing a backup as a single drive letter,
/// with two browsing modes side-by-side at the root:
/// <code>
///   Z:\
///     Latest revision\          ← shortcut to most recent of each source
///       ntws\
///         &lt;files at latest revision&gt;
///       Shared\
///         &lt;files at latest revision&gt;
///     All revisions\            ← full history
///       ntws (4 revisions)\
///         r4 - 2026-06-12 07-49\&lt;files&gt;
///         r3 - 2026-06-11 21-15\&lt;files&gt;
///         r2 - ...\
///         r1 - ...\
///       Shared (2 revisions)\
///         r2 - ...\
///         r1 - ...\
/// </code>
///
/// The two-branch layout is the user-driven shape: <b>Latest revision</b>
/// is the everyday answer to "give me the current state of my backup"
/// (it's what you reach for 95% of the time); <b>All revisions</b> is
/// the audit / point-in-time view that you only need when you actually
/// have to restore an older state. Putting both at the root means
/// neither click-path requires the user to commit to a mode upfront.
///
/// Implementation strategy: this provider is a pure DISPATCHER. The
/// actual byte-on-disk work — gRPC enumeration, restore-on-demand —
/// stays in <see cref="EngineBackedSnapshotTreeProvider"/>, one
/// instance per (source, revision) pair, materialised lazily on
/// first access and cached for the mount's lifetime. Both
/// <c>Latest revision</c> and <c>All revisions</c> share the same
/// sub-providers, so opening a file under Latest and then opening the
/// same file under All revisions/.../latest reuses one cache.
/// </summary>
public sealed class BackupMountTreeProvider : ISnapshotTreeProvider, IDisposable
{
    /// <summary>First-level virtual folder names. Kept as constants so
    /// the path parser and the listing-builder agree byte-for-byte.</summary>
    internal const string LatestBranch = "Latest revision";
    internal const string AllBranch = "All revisions";

    private readonly IBackupEngine _engine;
    private readonly Backup _backup;
    private readonly Destination _destination;
    private readonly string _storageName;
    private readonly string _cacheRootDir;
    private readonly string _unidentifiedSession = Guid.NewGuid().ToString("N");

    // Source folder name → source path. Built once at construction;
    // disambiguation happens here so the same logical name doesn't
    // appear twice in the root listing.
    private readonly Dictionary<string, string> _sourceByFolder;

    // (source folder, revision number) → engine provider. Shared
    // across both Latest and All branches, so a copy under Latest
    // warms the cache for the equivalent revision under All too.
    private readonly ConcurrentDictionary<(string Src, int Rev), EngineBackedSnapshotTreeProvider> _subProviders = new();

    // Per-source revision listing cache. Backrest's ListSnapshots is
    // cheap on a hot repo but we still don't want every Explorer
    // refresh to round-trip; cache for the mount's lifetime.
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<RevisionSummary>>> _revisionsCache = new();

    public BackupMountTreeProvider(
        IBackupEngine engine, Backup backup, Destination destination,
        string storageName, string cacheRootDir)
    {
        _engine = engine;
        _backup = backup;
        _destination = destination;
        _storageName = storageName;
        // Ignore legacy ordinal-only caches. Destination identity and its
        // configured location both matter when reusing verified cached bytes.
        _cacheRootDir = Path.Combine(cacheRootDir, "v2-" + CacheKey(
            destination.Id, destination.PathOrSubpath, storageName));
        Directory.CreateDirectory(_cacheRootDir);
        _sourceByFolder = BuildSourceFolderMap(backup.SourcePaths);
    }

    public async Task<IReadOnlyList<WebDavEntry>?> ListAsync(string relativeDir, CancellationToken ct)
    {
        var parsed = ParsePath(relativeDir);

        // Level 0 — drive root. Two virtual branches.
        if (parsed.Branch is null)
        {
            return new List<WebDavEntry>
            {
                new(LatestBranch, IsDirectory: true, SizeBytes: 0, LastWriteUtc: DateTime.UtcNow),
                new(AllBranch,    IsDirectory: true, SizeBytes: 0, LastWriteUtc: DateTime.UtcNow),
            };
        }

        // Level 1 — inside a branch. Source folders, naming depends
        // on which branch we're under.
        if (parsed.Src is null)
        {
            if (parsed.Branch == LatestBranch)
            {
                return _sourceByFolder.Keys
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .Select(k => new WebDavEntry(
                        RelativePath: parsed.Branch + "/" + k,
                        IsDirectory: true,
                        SizeBytes: 0,
                        LastWriteUtc: DateTime.UtcNow))
                    .ToList();
            }
            // All revisions: append " (X revisions)" to each source
            // folder name. The revision count is a quick scan over
            // the per-source cache (sequential, but every entry's
            // listing is memoised → at most one RPC per source per
            // mount lifetime).
            var rows = new List<WebDavEntry>();
            foreach (var key in _sourceByFolder.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                var revs = await GetRevisionsAsync(key, ct).ConfigureAwait(false);
                rows.Add(new WebDavEntry(
                    RelativePath: parsed.Branch + "/" + key + " (" + revs.Count + " revision" + (revs.Count == 1 ? "" : "s") + ")",
                    IsDirectory: true,
                    SizeBytes: 0,
                    LastWriteUtc: DateTime.UtcNow));
            }
            return rows;
        }

        // Unknown source.
        if (!_sourceByFolder.ContainsKey(parsed.Src)) return null;

        // Level 2 differs by branch:
        //  - Latest: skip the revision-picker layer; jump straight
        //            into the (src, latestRev) sub-provider.
        //  - All:    list revision folders.
        if (parsed.Branch == LatestBranch)
        {
            return await DelegateListAsync(parsed.Branch, parsed.SrcSegment!, parsed.Src,
                await LatestRevisionAsync(parsed.Src, ct).ConfigureAwait(false),
                revSegment: null,
                parsed.Remainder, ct).ConfigureAwait(false);
        }

        // All branch — Level 2 = revision list.
        if (parsed.Rev is null)
        {
            var revs = await GetRevisionsAsync(parsed.Src, ct).ConfigureAwait(false);
            var prefix = parsed.Branch + "/" + parsed.SrcSegment + "/";
            return revs.Select(r => new WebDavEntry(
                    RelativePath: prefix + RevisionFolderName(r),
                    IsDirectory: true,
                    SizeBytes: r.TotalBytes,
                    LastWriteUtc: r.CreatedUtc))
                .ToList();
        }

        // Level 3+ — inside a (src, rev). Delegate.
        return await DelegateListAsync(parsed.Branch, parsed.SrcSegment!, parsed.Src,
            parsed.Rev.Value,
            parsed.RevSegment,
            parsed.Remainder, ct).ConfigureAwait(false);
    }

    public async Task<WebDavEntry?> StatAsync(string relativePath, CancellationToken ct)
    {
        var key = Normalize(relativePath);
        if (key.Length == 0)
            return new WebDavEntry("", IsDirectory: true, SizeBytes: 0, LastWriteUtc: DateTime.UtcNow);

        var parsed = ParsePath(key);

        // Unknown / malformed branch.
        if (parsed.Branch is null) return null;

        // The branch directories themselves.
        if (parsed.Src is null)
        {
            return new WebDavEntry(parsed.Branch, IsDirectory: true, SizeBytes: 0, LastWriteUtc: DateTime.UtcNow);
        }

        // Unknown source.
        if (!_sourceByFolder.ContainsKey(parsed.Src)) return null;

        // Source directory itself (no deeper path).
        if (parsed.Rev is null && parsed.Remainder.Length == 0 && parsed.Branch == AllBranch)
        {
            return new WebDavEntry(
                RelativePath: parsed.Branch + "/" + parsed.SrcSegment,
                IsDirectory: true,
                SizeBytes: 0,
                LastWriteUtc: DateTime.UtcNow);
        }

        // Latest branch — no revision dir; deeper means files.
        if (parsed.Branch == LatestBranch)
        {
            var latestRev = await LatestRevisionAsync(parsed.Src, ct).ConfigureAwait(false);
            if (parsed.Remainder.Length == 0)
            {
                return new WebDavEntry(
                    RelativePath: parsed.Branch + "/" + parsed.SrcSegment,
                    IsDirectory: true,
                    SizeBytes: 0,
                    LastWriteUtc: DateTime.UtcNow);
            }
            return await DelegateStatAsync(parsed.Branch, parsed.SrcSegment!, parsed.Src,
                latestRev, revSegment: null, parsed.Remainder, ct).ConfigureAwait(false);
        }

        // All branch — revision directory.
        if (parsed.Rev is not null && parsed.Remainder.Length == 0)
        {
            var revs = await GetRevisionsAsync(parsed.Src, ct).ConfigureAwait(false);
            var match = revs.FirstOrDefault(r => r.Number == parsed.Rev.Value);
            if (match.Number == 0 && parsed.Rev.Value != 0) return null;
            return new WebDavEntry(
                RelativePath: parsed.Branch + "/" + parsed.SrcSegment + "/" + parsed.RevSegment,
                IsDirectory: true,
                SizeBytes: match.TotalBytes,
                LastWriteUtc: match.CreatedUtc);
        }

        // All branch — inside a (src, rev).
        if (parsed.Rev is null) return null;
        return await DelegateStatAsync(parsed.Branch, parsed.SrcSegment!, parsed.Src,
            parsed.Rev.Value, parsed.RevSegment, parsed.Remainder, ct).ConfigureAwait(false);
    }

    public async Task<Stream> OpenAsync(string relativePath, CancellationToken ct)
    {
        var parsed = ParsePath(relativePath);
        if (parsed.Branch is null || parsed.Src is null || parsed.Remainder.Length == 0)
            throw new FileNotFoundException(
                "Only files inside a source/revision folder are readable.", relativePath);

        int rev;
        if (parsed.Branch == LatestBranch)
        {
            rev = await LatestRevisionAsync(parsed.Src, ct).ConfigureAwait(false);
        }
        else
        {
            if (parsed.Rev is null)
                throw new FileNotFoundException("Pick a revision folder first.", relativePath);
            rev = parsed.Rev.Value;
        }

        var sub = await GetSubProviderAsync(parsed.Src, rev, ct).ConfigureAwait(false);
        if (sub is null)
            throw new FileNotFoundException("Unknown source × revision.", relativePath);
        return await sub.OpenAsync(parsed.Remainder, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        foreach (var sub in _subProviders.Values)
        {
            try { sub.Dispose(); }
            catch { /* best-effort */ }
        }
        _subProviders.Clear();
    }

    // ---- internals ----

    /// <summary>Delegate a Level 2+ ListAsync to the per-(src, rev)
    /// sub-provider and re-prefix the returned entries so they fit
    /// at OUR mount level. Sub-provider entries are relative to the
    /// SNAPSHOT root; we prepend the full
    /// <c>&lt;branch&gt;/&lt;srcSegment&gt;[/&lt;revSegment&gt;]/</c>
    /// chain so Explorer sees self-consistent paths.</summary>
    private async Task<IReadOnlyList<WebDavEntry>?> DelegateListAsync(
        string branch, string srcSegment, string srcKey, int revision,
        string? revSegment, string remainder, CancellationToken ct)
    {
        var sub = await GetSubProviderAsync(srcKey, revision, ct).ConfigureAwait(false);
        if (sub is null) return null;
        var listed = await sub.ListAsync(remainder, ct).ConfigureAwait(false);
        if (listed is null) return null;
        var prefix = branch + "/" + srcSegment + (revSegment is null ? "/" : "/" + revSegment + "/");
        return listed
            .Select(e => e with { RelativePath = prefix + e.RelativePath })
            .ToList();
    }

    private async Task<WebDavEntry?> DelegateStatAsync(
        string branch, string srcSegment, string srcKey, int revision,
        string? revSegment, string remainder, CancellationToken ct)
    {
        var sub = await GetSubProviderAsync(srcKey, revision, ct).ConfigureAwait(false);
        if (sub is null) return null;
        var entry = await sub.StatAsync(remainder, ct).ConfigureAwait(false);
        if (entry is null) return null;
        var prefix = branch + "/" + srcSegment + (revSegment is null ? "/" : "/" + revSegment + "/");
        return entry with { RelativePath = prefix + entry.RelativePath };
    }

    private async Task<IReadOnlyList<RevisionSummary>> GetRevisionsAsync(string sourceFolder, CancellationToken ct)
    {
        var fetch = _revisionsCache.GetOrAdd(sourceFolder, async key =>
        {
            using var lease = await ServiceLocator.Maintenance.AcquireReadAsync(ct).ConfigureAwait(false);
            return await ServiceLocator.Revisions.ListRevisionsAsync(
                _backup, _sourceByFolder[key], _destination, _storageName, ct).ConfigureAwait(false);
        });
        try { return await fetch.WaitAsync(ct).ConfigureAwait(false); }
        catch
        {
            if (ct.IsCancellationRequested || fetch.IsCanceled || fetch.IsFaulted)
                _revisionsCache.TryRemove(new KeyValuePair<string, Task<IReadOnlyList<RevisionSummary>>>(sourceFolder, fetch));
            throw;
        }
    }

    private async Task<int> LatestRevisionAsync(string sourceFolder, CancellationToken ct)
    {
        var revs = await GetRevisionsAsync(sourceFolder, ct).ConfigureAwait(false);
        if (revs.Count == 0) return 0;
        // RevisionBrowser returns newest-first (highest Number is the
        // latest restored backup); take that.
        return revs[0].Number;
    }

    private async Task<EngineBackedSnapshotTreeProvider?> GetSubProviderAsync(string sourceFolder, int revision, CancellationToken ct)
    {
        if (!_sourceByFolder.TryGetValue(sourceFolder, out var sourcePath)) return null;
        if (revision == 0) return null;
        var revs = await GetRevisionsAsync(sourceFolder, ct).ConfigureAwait(false);
        if (revs.All(r => r.Number != revision)) return null;

        var wasCreated = false;
        var sub = _subProviders.GetOrAdd((sourceFolder, revision), tuple =>
        {
            var snapshot = revs.First(r => r.Number == revision);
            // Revision numbers can be reassigned after retention. Only an
            // immutable snapshot ID allows reuse across mounts. Engines that
            // cannot supply one receive an isolated session cache instead.
            var cacheDir = Path.Combine(_cacheRootDir, CacheKey(sourcePath,
                snapshot.SnapshotId ?? _unidentifiedSession + "-r" + tuple.Rev));
            wasCreated = true;
            return new EngineBackedSnapshotTreeProvider(
                _engine, _backup, sourcePath, _destination, _storageName, tuple.Rev, cacheDir, snapshot.SnapshotId);
        });

        // First materialisation: fire-and-forget a background prefetch
        // of the whole sub-tree. The lazy listing for the dir Explorer
        // just asked about proceeds in parallel and returns its single
        // RPC quickly; the prefetch warms _dirCache for everything
        // else, so an Explorer "Calculating..." sweep on a revision
        // folder doesn't have to do thousands of sequential round-trips.
        if (wasCreated)
        {
            _ = Task.Run(async () =>
            {
                try { await sub.PrefetchAllAsync().ConfigureAwait(false); }
                catch { /* prefetch is best-effort */ }
            });
        }
        return sub;
    }

    /// <summary>Parse-result struct. Fields cleanly nullable so a
    /// caller can discriminate "root / branch / source / revision /
    /// inside" without re-splitting.</summary>
    internal readonly record struct ParsedPath(
        string? Branch,
        string? Src,
        string? SrcSegment,
        int? Rev,
        string? RevSegment,
        string Remainder);

    /// <summary>Resolve a path like
    /// <c>"All revisions/ntws (4 revisions)/r4 - 2026-06-12 07-49/Documents/file.txt"</c>
    /// or <c>"Latest revision/ntws/Documents/file.txt"</c> into its
    /// component segments. Returns the ORIGINAL <c>SrcSegment</c> and
    /// <c>RevSegment</c> alongside the canonical <c>Src</c> key — the
    /// originals are needed to re-prefix sub-provider entries so
    /// Explorer sees the same folder name on PROPFIND and on the
    /// child entries' self-paths (mismatch → empty dir in Explorer).</summary>
    internal static ParsedPath ParsePath(string relativePath)
    {
        var normalized = Normalize(relativePath);
        if (normalized.Length == 0)
            return new ParsedPath(null, null, null, null, null, "");

        var (branch, afterBranch) = SplitFirst(normalized);
        if (branch != LatestBranch && branch != AllBranch)
            return new ParsedPath(null, null, null, null, null, "");

        if (afterBranch.Length == 0)
            return new ParsedPath(branch, null, null, null, null, "");

        var (srcSegment, afterSrc) = SplitFirst(afterBranch);
        // For the All branch the source-folder name includes a
        // " (N revisions)" trailer; strip it to recover the canonical
        // key the source-folder map uses. The Latest branch uses bare
        // names so srcSegment == srcKey.
        var srcKey = branch == AllBranch ? StripCountSuffix(srcSegment) : srcSegment;

        if (afterSrc.Length == 0)
            return new ParsedPath(branch, srcKey, srcSegment, null, null, "");

        // Latest branch has no revision layer — anything after the
        // source is a file path inside the latest snapshot.
        if (branch == LatestBranch)
            return new ParsedPath(branch, srcKey, srcSegment, null, null, afterSrc);

        // All branch — third segment is a revision folder.
        var (revSegment, afterRev) = SplitFirst(afterSrc);
        var rev = TryParseRevisionNumber(revSegment);
        if (rev is null)
            return new ParsedPath(branch, srcKey, srcSegment, null, null, "");
        return new ParsedPath(branch, srcKey, srcSegment, rev, revSegment, afterRev);
    }

    private static (string Head, string Tail) SplitFirst(string s)
    {
        var idx = s.IndexOf('/');
        if (idx < 0) return (s, "");
        return (s.Substring(0, idx), s.Substring(idx + 1));
    }

    private static int? TryParseRevisionNumber(string segment)
    {
        // Accepts both the bare "r4" form and the dated
        // "r4 - 2026-06-12 07-49" form RevisionFolderName produces.
        if (segment.Length < 2 || segment[0] != 'r') return null;
        int digitEnd = 1;
        while (digitEnd < segment.Length && char.IsDigit(segment[digitEnd]))
            digitEnd++;
        if (digitEnd == 1) return null;
        if (!int.TryParse(segment.AsSpan(1, digitEnd - 1), out var rev)) return null;
        return rev;
    }

    /// <summary>Strip a trailing " (N revisions)" / " (1 revision)"
    /// suffix from an All-branch source-folder segment to recover the
    /// canonical key in <see cref="_sourceByFolder"/>. Anything else
    /// passes through unchanged so the parser stays tolerant of paths
    /// the user typed by hand.</summary>
    internal static string StripCountSuffix(string segment)
    {
        // Find the last " (" — could be part of a disambiguator like
        // "ntws (C)" too, so we only strip when the parenthesised
        // body ends with "revision" or "revisions)".
        int idx = segment.LastIndexOf(" (");
        if (idx < 0) return segment;
        var tail = segment.AsSpan(idx + 2);
        if (!tail.EndsWith(")")) return segment;
        var body = tail.Slice(0, tail.Length - 1);
        // body must look like "N revision" or "N revisions"
        int sp = body.IndexOf(' ');
        if (sp < 1) return segment;
        var num = body.Slice(0, sp);
        var word = body.Slice(sp + 1);
        bool numeric = true;
        foreach (var c in num) { if (!char.IsDigit(c)) { numeric = false; break; } }
        if (!numeric) return segment;
        if (!word.SequenceEqual("revision".AsSpan()) && !word.SequenceEqual("revisions".AsSpan()))
            return segment;
        return segment.Substring(0, idx);
    }

    /// <summary>Build the source-folder map for the mount root, with
    /// collision-safe disambiguation. Goal: short, human-recognisable
    /// folder names. Strategy:
    ///   1. First pass uses the leaf of the source path (the last
    ///      path component) — that's what the user thinks of as "the
    ///      folder name" of their backup target.
    ///   2. On collision, append the drive letter or the parent
    ///      directory to disambiguate.
    ///   3. Last resort: a numbered suffix.
    /// </summary>
    internal static Dictionary<string, string> BuildSourceFolderMap(IList<string> sourcePaths)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in sourcePaths)
        {
            var candidate = LeafOf(path);
            if (candidate.Length == 0) candidate = "source";
            candidate = SafeForFilesystem(candidate);

            if (!taken.Contains(candidate))
            {
                map[candidate] = path;
                taken.Add(candidate);
                continue;
            }

            // Disambiguate by drive letter or by parent directory.
            var disambig = SafeForFilesystem(DriveOrParentOf(path));
            var combo = disambig.Length > 0
                ? candidate + " (" + disambig + ")"
                : candidate;
            var attempt = combo;
            int n = 2;
            while (taken.Contains(attempt))
            {
                attempt = combo + " " + n;
                n++;
            }
            map[attempt] = path;
            taken.Add(attempt);
        }
        return map;
    }

    private static string LeafOf(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var s = path.Replace('\\', '/').TrimEnd('/');
        var idx = s.LastIndexOf('/');
        return idx < 0 ? s : s.Substring(idx + 1);
    }

    private static string DriveOrParentOf(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        // Drive letter case: "C:\foo\bar" → "C"
        if (path.Length >= 2 && path[1] == ':') return path[0].ToString();
        // UNC / POSIX: take the parent dir's leaf.
        var s = path.Replace('\\', '/').TrimEnd('/');
        var lastSlash = s.LastIndexOf('/');
        if (lastSlash <= 0) return "";
        var parent = s.Substring(0, lastSlash);
        var parentSlash = parent.LastIndexOf('/');
        return parentSlash < 0 ? parent : parent.Substring(parentSlash + 1);
    }

    private static string SafeForFilesystem(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Folder name for a revision inside its source. Format
    /// matches the Restore wizard's revision-picker display
    /// (<c>#N · yyyy-MM-dd HH:mm</c>) with Windows-path-safe
    /// punctuation — colons in filenames are illegal on NTFS, so the
    /// time uses hyphens. Both the number (so users can correlate with
    /// the wizard) and the local timestamp (so users can pick "the one
    /// from yesterday morning" without consulting Explorer's Modified
    /// column) live in the name. Local time is used because that's
    /// what a user thinks in — UTC would be technically purer but
    /// requires mental math.</summary>
    private static string RevisionFolderName(RevisionSummary r)
        => "r" + r.Number + " - " + r.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH-mm");

    private static string Normalize(string path)
        => string.IsNullOrEmpty(path) ? "" : path.Replace('\\', '/').Trim('/');

    private static string CacheKey(params string[] parts) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\0", parts))));
}
