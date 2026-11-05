using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services;

/// <summary>
/// What an incremental backup will have to read, worked out before restic
/// gets there (U144, 5 November 2026). Restic's percentage is of the whole
/// source: an unchanged file counts as done as soon as its date and size match
/// the last snapshot, so local_c reached 91% within minutes and then spent most
/// of an hour on the changes. Backspace lists the source itself (seconds, from
/// the folder listings), compares each file's size and date with its listing
/// from the last successful backup, and knows which files restic will read and
/// how many bytes they hold. The progress line then follows the work: checking
/// unchanged files counts for little, reading and sending changes for most.
///
/// The files are kept in restic's order (depth first, names in byte order), so
/// restic's current file, or its count of files done, shows how far it is in
/// this list. The listing is a reproducible cache in the owner's
/// %LOCALAPPDATA%\Backspace\change-lists: without it, or when the last snapshot
/// is not the one it was taken for, progress is shown as before.
/// </summary>
internal sealed class ChangePlan
{
    internal readonly record struct FileState(string Path, long Size, long WrittenTicks);

    private readonly string _root;
    private readonly long[] _sizes;
    private readonly bool[] _changed;
    private readonly long[] _bytesBefore;          // all bytes before index i
    private readonly long[] _changedBytesBefore;   // changed bytes before index i
    private readonly int[] _unchangedFilesBefore;  // unchanged files before index i
    // By path hash, so the paths themselves are not kept while a backup runs
    // (600,000 of them on a whole drive).
    private readonly Dictionary<ulong, int> _index;

    internal int Files => _sizes.Length;
    internal long TotalBytes => _bytesBefore[^1];
    internal long ChangedBytes => _changedBytesBefore[^1];
    internal int ChangedFiles { get; }
    internal int UnchangedFiles => _unchangedFilesBefore[^1];

    /// <param name="now">The source's files as listed now, in any order.</param>
    /// <param name="before">Size and last-write time of each file at the last
    /// successful backup, by path hash.</param>
    internal ChangePlan(string root, IEnumerable<FileState> now, IReadOnlyDictionary<ulong, (long Size, long WrittenTicks)> before)
    {
        _root = root;
        var files = now.ToArray();
        Array.Sort(files, (a, b) => ResticOrder(a.Path, b.Path));
        var n = files.Length;
        _sizes = new long[n];
        _changed = new bool[n];
        _bytesBefore = new long[n + 1];
        _changedBytesBefore = new long[n + 1];
        _unchangedFilesBefore = new int[n + 1];
        _index = new Dictionary<ulong, int>(n);
        for (var i = 0; i < n; i++)
        {
            var f = files[i];
            _sizes[i] = f.Size;
            _changed[i] = !before.TryGetValue(Hash(f.Path), out var was) || was.Size != f.Size || was.WrittenTicks != f.WrittenTicks;
            if (_changed[i]) ChangedFiles++;
            _bytesBefore[i + 1] = _bytesBefore[i] + f.Size;
            _changedBytesBefore[i + 1] = _changedBytesBefore[i] + (_changed[i] ? f.Size : 0);
            _unchangedFilesBefore[i + 1] = _unchangedFilesBefore[i] + (_changed[i] ? 0 : 1);
            _index.TryAdd(Hash(f.Path), i);
        }
    }

    /// <summary>Unchanged files checked and changed bytes read so far. The
    /// position is the earliest of restic's current files (it reads a large
    /// file while its walk goes on past it), or its count of files done when
    /// none of them is in the list. The changed bytes are what restic has read
    /// beyond the unchanged files before that position, never more than the
    /// changes up to and including it: a position ahead of a file still being
    /// read cannot count that file as done.</summary>
    internal (long UnchangedFilesDone, long ChangedBytesDone) Done(string? currentFiles, long filesDone, long bytesDone)
    {
        var i = int.MaxValue;
        foreach (var file in (currentFiles ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            if (_index.TryGetValue(Hash(TreeWalk.Relative(_root, file)), out var at)) i = Math.Min(i, at);
        if (i == int.MaxValue) i = (int)Math.Clamp(filesDone, 0, Files);
        var unchangedBytesBefore = _bytesBefore[i] - _changedBytesBefore[i];
        var upTo = _changedBytesBefore[i] + (i < Files && _changed[i] ? _sizes[i] : 0);
        return (_unchangedFilesBefore[i], Math.Clamp(bytesDone - unchangedBytesBefore, 0, upTo));
    }

    /// <summary>Restic's walk order: depth first, entries of a folder sorted by
    /// name. Comparing whole relative paths with "/" lowest gives that order.</summary>
    internal static int ResticOrder(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            var x = a[i] == '/' ? '\0' : a[i];
            var y = b[i] == '/' ? '\0' : b[i];
            if (x != y) return x < y ? -1 : 1;
        }
        return a.Length.CompareTo(b.Length);
    }

    /// <summary>64-bit FNV-1a of the relative path, case-insensitive like
    /// Windows paths.</summary>
    internal static ulong Hash(string path)
    {
        var h = 14695981039346656037UL;
        foreach (var c in path)
        {
            h = (h ^ char.ToUpperInvariant(c)) * 1099511628211UL;
        }
        return h;
    }

    /// <summary>The source's files as the backup sees them: its rules applied,
    /// files kept only in the cloud left out when it skips them. Null when
    /// listing took longer than <paramref name="limit"/>.</summary>
    internal static List<FileState>? List(string sourcePath, SourceSizeRules rules, TimeSpan limit, CancellationToken ct)
    {
        var root = TreeWalk.Root(sourcePath);
        var files = new System.Collections.Concurrent.ConcurrentBag<FileState>();
        var compiled = rules.HasRules ? FilterTranslator.CompileRules(FilterTranslator.ScopeToSource(rules.FiltersText, root)) : null;
        bool Excluded(string fullPath, bool directory)
            => compiled is not null && FilterTranslator.Evaluate(compiled, TreeWalk.Relative(root, fullPath) + (directory ? "/" : "")).Excluded;
        var finished = TreeWalk.Walk(root, (entry, _) =>
        {
            if (RecommendedFilters.IsAlwaysExcludedAtRoot(root, entry.FullPath, entry.IsDirectory)) return null;
            if (Excluded(entry.FullPath, entry.IsDirectory)) return null;
            if (entry.IsDirectory) return false;
            if (rules.SkipCloudOnly && TreeWalk.IsCloudOnly(entry.Attributes)) return null;
            files.Add(new FileState(TreeWalk.Relative(root, entry.FullPath), entry.Length, entry.LastWriteUtc.Ticks));
            return null;
        }, DateTime.UtcNow + limit, ct);
        return finished ? files.ToList() : null;
    }
}

/// <summary>The listing of a source at its last successful backup, saved per
/// backup cell (source and storage), with the pace that backup had.</summary>
internal sealed class ChangePlanStore(string directory)
{
    private const string Magic = "BSCHG1";
    internal sealed record Saved(string SnapshotId, double FilesPerSecond, double BytesPerSecond, Dictionary<ulong, (long Size, long WrittenTicks)> Files);
    internal readonly record struct Entry(ulong PathHash, long Size, long WrittenTicks);

    /// <summary>Beside the engines in the owner's %LOCALAPPDATA%\Backspace: a
    /// cache Backspace rebuilds, never part of the portable folder (it would be
    /// synced after every backup). A configuration outside this copy's folder
    /// (tests, review set-ups) keeps its own, inside that configuration.</summary>
    internal static ChangePlanStore ForOwner()
    {
        var config = Path.GetFullPath(AppPaths.ConfigRoot);
        var portable = Path.GetFullPath(Path.Combine(AppPaths.ExeDirectory, AppPaths.AppFolderName)).TrimEnd('\\', '/');
        return config.StartsWith(portable, StringComparison.OrdinalIgnoreCase)
            ? new(Path.Combine(Path.GetDirectoryName(EngineInstallation.Root.TrimEnd('\\', '/'))!, "change-lists"))
            : new(Path.Combine(config, "change-lists"));
    }

    private string PathFor(string planId)
        => Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(planId)))[..24].ToLowerInvariant() + ".bin");

    internal Saved? Read(string planId)
    {
        try
        {
            var path = PathFor(planId);
            if (!File.Exists(path)) return null;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var reader = new BinaryReader(new System.IO.Compression.BrotliStream(file, System.IO.Compression.CompressionMode.Decompress));
            if (reader.ReadString() != Magic) return null;
            var snapshot = reader.ReadString();
            var fps = reader.ReadDouble();
            var bps = reader.ReadDouble();
            var count = reader.ReadInt32();
            if (count < 0 || count > 50_000_000) return null;
            var files = new Dictionary<ulong, (long, long)>(count);
            for (var i = 0; i < count; i++) files[reader.ReadUInt64()] = (reader.ReadInt64(), reader.ReadInt64());
            return new(snapshot, fps, bps, files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException) { return null; }
    }

    internal void Write(string planId, string snapshotId, double filesPerSecond, double bytesPerSecond, IReadOnlyCollection<ChangePlan.FileState> files)
        => Write(planId, snapshotId, filesPerSecond, bytesPerSecond, files.Select(f => new Entry(ChangePlan.Hash(f.Path), f.Size, f.WrittenTicks)).ToArray());

    internal void Write(string planId, string snapshotId, double filesPerSecond, double bytesPerSecond, IReadOnlyCollection<Entry> files)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = PathFor(planId);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(new System.IO.Compression.BrotliStream(file, System.IO.Compression.CompressionLevel.Fastest)))
            {
                writer.Write(Magic);
                writer.Write(snapshotId);
                writer.Write(filesPerSecond);
                writer.Write(bytesPerSecond);
                writer.Write(files.Count);
                foreach (var f in files)
                {
                    writer.Write(f.PathHash);
                    writer.Write(f.Size);
                    writer.Write(f.WrittenTicks);
                }
            }
            File.Move(temporary, path, overwrite: true);
            // Listings of backups removed long ago.
            foreach (var old in Directory.EnumerateFiles(directory, "*.bin"))
                if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-90)) File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>One backup cell's change plan: the listing starts with the backup,
/// the plan is ready seconds later, and the listing is saved once the backup
/// succeeded, for the next one.</summary>
internal sealed class ChangePlanner
{
    private readonly ChangePlanStore _store;
    private readonly string _planId;
    private readonly Task<(ChangePlanStore.Entry[]? Files, ChangePlan? Plan, ChangePlanStore.Saved? Before)> _work;

    private ChangePlanner(ChangePlanStore store, string planId, string sourcePath, SourceSizeRules rules, string? previousSnapshotId, CancellationToken ct)
    {
        _store = store;
        _planId = planId;
        _work = Task.Run(() =>
        {
            var files = ChangePlan.List(sourcePath, rules, TimeSpan.FromMinutes(3), ct);
            if (files is null) return ((ChangePlanStore.Entry[]?)null, (ChangePlan?)null, (ChangePlanStore.Saved?)null);
            var before = previousSnapshotId is null ? null : store.Read(planId);
            // Only against the snapshot restic compares with.
            var plan = before is not null && string.Equals(before.SnapshotId, previousSnapshotId, StringComparison.OrdinalIgnoreCase)
                ? new ChangePlan(TreeWalk.Root(sourcePath), files, before.Files) : null;
            return (files.Select(f => new ChangePlanStore.Entry(ChangePlan.Hash(f.Path), f.Size, f.WrittenTicks)).ToArray(), plan, before);
        }, ct);
    }

    internal static ChangePlanner Start(ChangePlanStore store, string planId, string sourcePath, SourceSizeRules rules, string? previousSnapshotId, CancellationToken ct)
        => new(store, planId, sourcePath, rules, previousSnapshotId, ct);

    /// <summary>The plan once ready, or null (still listing, or nothing to compare with).</summary>
    internal ChangePlan? Plan => _work.IsCompletedSuccessfully ? _work.Result.Plan : null;

    /// <summary>The pace the last backup measured, as a first guess.</summary>
    internal (double FilesPerSecond, double BytesPerSecond)? PreviousPace
        => _work.IsCompletedSuccessfully && _work.Result.Before is { FilesPerSecond: > 0, BytesPerSecond: > 0 } b ? (b.FilesPerSecond, b.BytesPerSecond) : null;

    /// <summary>After a successful backup: this listing, for the next one.</summary>
    internal async Task SaveAsync(string snapshotId, (double FilesPerSecond, double BytesPerSecond)? pace)
    {
        try
        {
            var (files, _, before) = await _work.ConfigureAwait(false);
            if (files is null) return;
            var keep = pace ?? (before is not null ? (before.FilesPerSecond, before.BytesPerSecond) : (0, 0));
            await Task.Run(() => _store.Write(_planId, snapshotId, keep.Item1, keep.Item2, files)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }
}
