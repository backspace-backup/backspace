using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;

namespace Backspace.Services.WebDav;

/// <summary>
/// A backup's chosen revisions as one drive (U162). The person picks the
/// revisions when mounting (<see cref="MountSelection"/>); the drive shows
/// those and nothing else, and every level with a single choice is left out:
/// <code>
///   one source, one revision      Z:\&lt;files&gt;
///   one source, several           Z:\2026-11-05 09-12\&lt;files&gt;
///                                 Z:\2026-11-07 14-30\&lt;files&gt;
///   several sources               Z:\Documents\&lt;files&gt;           (one revision of Documents)
///                                 Z:\Photos\2026-11-05 09-12\...    (several of Photos)
/// </code>
///
/// <para>Every chosen revision's whole tree is read by
/// <see cref="PrepareAsync"/>, one after the other, before the drive appears.
/// Afterwards folder listings, look-ups and size sweeps (Explorer, thumbnails,
/// the search indexer) are answered from memory; nothing asks the engine for a
/// tree or a revision list again. Only opening a file fetches it. Before
/// U162 an "All revisions" folder read each older tree the first time
/// Explorer touched it, which on the owner's 600,000-file cloud backup kept
/// restic busy for four minutes after the drive appeared.</para>
///
/// <para>This provider only dispatches: the per-revision work stays in
/// <see cref="EngineBackedSnapshotTreeProvider"/>, one per (source,
/// revision).</para>
/// </summary>
public sealed class BackupMountTreeProvider : ISnapshotTreeProvider, IDisposable
{
    private sealed record RevisionView(string Folder, RevisionSummary Revision, EngineBackedSnapshotTreeProvider Tree);
    private sealed record SourceView(string Folder, string SourcePath, IReadOnlyList<RevisionView> Revisions)
    {
        public DateTime NewestUtc => Revisions.Max(r => r.Revision.CreatedUtc);
    }

    private readonly IReadOnlyList<SourceView> _sources;
    private readonly bool _sourceLevel;

    public BackupMountTreeProvider(
        IBackupEngine engine, Backup backup, Destination destination,
        string storageName, string cacheRootDir, MountSelection selection)
    {
        // Ignore legacy ordinal-only caches. Destination identity and its
        // configured location both matter when reusing verified cached bytes.
        var cacheRoot = Path.Combine(cacheRootDir, "v2-" + CacheKey(
            destination.Id, destination.PathOrSubpath, storageName));
        Directory.CreateDirectory(cacheRoot);
        var unidentifiedSession = Guid.NewGuid().ToString("N");

        var chosen = selection.Sources.Where(s => s.Revisions.Count > 0).ToList();
        var folders = BuildSourceFolderMap(chosen.Select(s => s.Path).ToList())
            .ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);
        _sources = chosen.Select(source =>
        {
            var revisions = source.Revisions.OrderByDescending(r => r.CreatedUtc).ToList();
            var names = RevisionFolderNames(revisions);
            return new SourceView(folders[source.Path], source.Path, revisions.Select((r, i) =>
                new RevisionView(names[i], r, new EngineBackedSnapshotTreeProvider(
                    engine, backup, source.Path, destination, storageName, r.Number,
                    // Revision numbers can be reassigned after retention. Only an
                    // immutable snapshot ID allows reuse across mounts. Engines that
                    // cannot supply one receive an isolated session cache instead.
                    Path.Combine(cacheRoot, CacheKey(source.Path, r.SnapshotId ?? unidentifiedSession + "-r" + r.Number)),
                    r.SnapshotId))).ToList());
        }).ToList();
        _sourceLevel = _sources.Count > 1;
    }

    /// <summary>How many revisions the drive shows.</summary>
    public int RevisionCount => _sources.Sum(s => s.Revisions.Count);

    /// <summary>Before the drive appears: reads every chosen revision's whole
    /// tree, one after the other (each listing already shares its folders out
    /// between several restic processes). <paramref name="progress"/> hears
    /// which revision is being read and how many of its files and folders have
    /// been read. Returns how many revisions the drive shows (0: nothing to
    /// mount). Cancelling stops at once; disposing stops the listing.</summary>
    public async Task<int> PrepareAsync(IProgress<MountReading>? progress, CancellationToken ct)
    {
        var all = _sources.SelectMany(s => s.Revisions.Select(r => (Source: s, View: r))).ToList();
        for (var i = 0; i < all.Count; i++)
        {
            var (source, view) = all[i];
            var label = all.Count == 1 ? null
                : (_sourceLevel ? source.Folder + ", " : "") + MountSelection.When(view.Revision.CreatedUtc);
            int Read() => view.Tree.Tree is { } tree ? tree.FileCount + tree.DirectoryCount : view.Tree.TreeEntriesRead;
            var reading = view.Tree.LoadTreeAsync(ct);
            while (!reading.IsCompleted)
            {
                progress?.Report(new MountReading(Read(), i + 1, all.Count, label));
                await Task.WhenAny(reading, Task.Delay(250, ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
            await reading.ConfigureAwait(false);
            progress?.Report(new MountReading(Read(), i + 1, all.Count, label));
        }
        return all.Count;
    }

    public async Task<IReadOnlyList<WebDavEntry>?> ListAsync(string relativeDir, CancellationToken ct)
    {
        var at = Resolve(relativeDir);
        switch (at.Kind)
        {
            case Level.Root:
                return _sources.Select(s => new WebDavEntry(s.Folder, IsDirectory: true, SizeBytes: 0, LastWriteUtc: s.NewestUtc)).ToList();
            case Level.Source:
                return at.Source!.Revisions.Select(r => new WebDavEntry(
                    at.Prefix + r.Folder, IsDirectory: true, SizeBytes: 0, LastWriteUtc: r.Revision.CreatedUtc)).ToList();
            case Level.Inside:
                var listed = await at.Revision!.Tree.ListAsync(at.Remainder, ct).ConfigureAwait(false);
                return listed?.Select(e => e with { RelativePath = at.Prefix + e.RelativePath }).ToList();
            default:
                return null;
        }
    }

    public async Task<WebDavEntry?> StatAsync(string relativePath, CancellationToken ct)
    {
        var at = Resolve(relativePath);
        switch (at.Kind)
        {
            case Level.Root:
                return new WebDavEntry("", IsDirectory: true, SizeBytes: 0,
                    LastWriteUtc: _sources.Count > 0 ? _sources.Max(s => s.NewestUtc) : DateTime.UtcNow);
            case Level.Source:
                return new WebDavEntry(at.Path, IsDirectory: true, SizeBytes: 0, LastWriteUtc: at.Source!.NewestUtc);
            case Level.Inside when at.Remainder.Length == 0:
                return new WebDavEntry(at.Path, IsDirectory: true, SizeBytes: 0, LastWriteUtc: at.Revision!.Revision.CreatedUtc);
            case Level.Inside:
                var entry = await at.Revision!.Tree.StatAsync(at.Remainder, ct).ConfigureAwait(false);
                return entry is null ? null : entry with { RelativePath = at.Prefix + entry.RelativePath };
            default:
                return null;
        }
    }

    public async Task<Stream> OpenAsync(string relativePath, CancellationToken ct)
    {
        var at = Resolve(relativePath);
        if (at.Kind != Level.Inside || at.Remainder.Length == 0)
            throw new FileNotFoundException("Not a file in this snapshot.", relativePath);
        return await at.Revision!.Tree.OpenAsync(at.Remainder, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        foreach (var view in _sources.SelectMany(s => s.Revisions))
        {
            try { view.Tree.Dispose(); }
            catch { /* best-effort */ }
        }
    }

    // ---- paths ----

    internal enum Level { Missing, Root, Source, Inside }

    /// <summary>Where a drive path points: the root, a source folder listing
    /// its revision folders, or inside one revision (<see cref="Remainder"/>
    /// relative to the snapshot, <see cref="Prefix"/> the drive folders before
    /// it, spelled as asked: Windows may uppercase an open ancestor).</summary>
    private readonly record struct Resolved(Level Kind, string Path, string Prefix, SourceView? Source, RevisionView? Revision, string Remainder);

    private Resolved Resolve(string relativePath)
    {
        var path = Normalize(relativePath);
        var missing = new Resolved(Level.Missing, path, "", null, null, "");
        if (_sources.Count == 0) return path.Length == 0 ? missing with { Kind = Level.Root } : missing;
        var segments = path.Length == 0 ? Array.Empty<string>() : path.Split('/');
        var used = 0;

        SourceView source;
        if (_sourceLevel)
        {
            if (segments.Length == 0) return missing with { Kind = Level.Root };
            var match = _sources.FirstOrDefault(s => string.Equals(s.Folder, segments[0], StringComparison.OrdinalIgnoreCase));
            if (match is null) return missing;
            source = match;
            used = 1;
        }
        else source = _sources[0];

        RevisionView revision;
        if (source.Revisions.Count > 1)
        {
            if (segments.Length == used)
                return missing with { Kind = Level.Source, Source = source, Prefix = path.Length == 0 ? "" : path + "/" };
            var match = source.Revisions.FirstOrDefault(r => string.Equals(r.Folder, segments[used], StringComparison.OrdinalIgnoreCase));
            if (match is null) return missing;
            revision = match;
            used++;
        }
        else revision = source.Revisions[0];

        var prefix = used == 0 ? "" : string.Join('/', segments, 0, used) + "/";
        var remainder = string.Join('/', segments, used, segments.Length - used);
        return new Resolved(Level.Inside, path, prefix, source, revision, remainder);
    }

    /// <summary>Folder names for a source's chosen revisions, newest first:
    /// local date and time ("2026-11-07 14-30"; NTFS forbids colons), with
    /// seconds when two share a minute, and a counter as a last resort.</summary>
    internal static IReadOnlyList<string> RevisionFolderNames(IReadOnlyList<RevisionSummary> revisions)
    {
        string Name(RevisionSummary r, string format) => r.CreatedUtc.ToLocalTime().ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        var minutes = revisions.Select(r => Name(r, "yyyy-MM-dd HH-mm")).ToList();
        var names = minutes.Select((minute, i) => minutes.Count(m => m == minute) > 1
            ? Name(revisions[i], "yyyy-MM-dd HH-mm-ss") : minute).ToList();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            for (var n = 2; !taken.Add(name); n++) name = names[i] + " (" + n + ")";
            names[i] = name;
        }
        return names;
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

    private static string Normalize(string path)
        => string.IsNullOrEmpty(path) ? "" : path.Replace('\\', '/').Trim('/');

    private static string CacheKey(params string[] parts) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\0", parts))));
}
