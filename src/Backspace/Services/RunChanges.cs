using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>A file a backup added or changed, with its size in the snapshot.</summary>
internal readonly record struct ChangedFile(string Path, long Size, bool IsNew);

/// <summary>
/// What one backup added or changed (U143, 5 November 2026): the files of its
/// snapshot compared with those of the snapshot before it, by size and date,
/// as restic decides what to read. Restic keeps every snapshot's file list, so
/// this works for any backup whose snapshot and the one before are still
/// kept; Verbose is not needed. Only the file lists are read (cached on this
/// computer), never file contents.
/// </summary>
internal static class RunChanges
{
    /// <summary>The snapshot a backup made, from the recap at the end of its
    /// log ("· snapshot a4042f53"), or null.</summary>
    internal static string? SnapshotOf(string? logText)
    {
        if (string.IsNullOrEmpty(logText)) return null;
        var matches = Regex.Matches(logText, @"^\[recap\] Backup complete: [^\n]*· snapshot (\S+)\s*$", RegexOptions.Multiline);
        return matches.Count == 0 ? null : matches[^1].Groups[1].Value;
    }

    /// <summary>The snapshot a run made of one source to one destination, from
    /// that cell's log (the app's own, or the background service's).</summary>
    internal static string? SnapshotOf(RunRecord run, CellOutcome cell)
    {
        // A background run's cells name the service's logs as they are on
        // this computer (ServiceHistoryFiles, U138).
        var path = cell.LogPath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                // The recap is the last lines; a long log need not be read whole.
                if (stream.Length > 256 * 1024) stream.Seek(-256 * 1024, SeekOrigin.End);
                using var reader = new StreamReader(stream);
                if (SnapshotOf(reader.ReadToEnd()) is { } id) return id;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>Files in <paramref name="after"/> that are not in
    /// <paramref name="before"/> (new) or differ in size or date (changed).
    /// Paths compare as restic stores them: exactly.</summary>
    internal static List<ChangedFile> Compare(IReadOnlyList<RevisionFile>? before, IReadOnlyList<RevisionFile> after)
    {
        var earlier = new Dictionary<string, (long Size, DateTime Written)>(StringComparer.Ordinal);
        if (before is not null)
            foreach (var f in before) earlier[f.Path] = (f.SizeBytes, f.ModifiedUtc);
        var changes = new List<ChangedFile>();
        foreach (var f in after)
        {
            if (!earlier.TryGetValue(f.Path, out var was)) changes.Add(new(f.Path, f.SizeBytes, IsNew: true));
            else if (was.Size != f.SizeBytes || was.Written != f.ModifiedUtc) changes.Add(new(f.Path, f.SizeBytes, IsNew: false));
        }
        return changes;
    }

    internal sealed record Result(string SnapshotId, DateTime CreatedUtc, string? PreviousId, DateTime? PreviousUtc, List<ChangedFile> Files);

    /// <summary>Reads the two snapshots' file lists and compares them. Null
    /// when the snapshot is no longer kept.</summary>
    internal static async Task<Result?> LoadAsync(IBackupEngine engine, Backup backup, string sourcePath, Destination destination,
        string storageName, string snapshotPrefix, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Finding this backup's snapshot…");
        var snapshots = await engine.ListSnapshotsAsync(backup, sourcePath, destination, storageName, ct).ConfigureAwait(false);
        var index = -1;
        for (var i = 0; i < snapshots.Count; i++)
            if (snapshots[i].SnapshotId is { } id && id.StartsWith(snapshotPrefix, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
        if (index < 0) return null;
        var current = snapshots[index];
        // Newest first: the one after it in the list is the backup before.
        RevisionSummary? previous = index + 1 < snapshots.Count ? snapshots[index + 1] : null;
        progress?.Report(previous is null ? "Reading this backup's file list…" : "Reading this backup's file list (1 of 2)…");
        var after = await engine.ListSnapshotFilesAsync(backup, sourcePath, destination, storageName, current.Number, ct, current.SnapshotId).ConfigureAwait(false);
        IReadOnlyList<RevisionFile>? before = null;
        if (previous is { } p)
        {
            progress?.Report("Reading the previous backup's file list (2 of 2)…");
            before = await engine.ListSnapshotFilesAsync(backup, sourcePath, destination, storageName, p.Number, ct, p.SnapshotId).ConfigureAwait(false);
        }
        progress?.Report("Comparing…");
        var files = await Task.Run(() => Compare(before, after), ct).ConfigureAwait(false);
        return new(current.SnapshotId!, current.CreatedUtc, previous?.SnapshotId, previous?.CreatedUtc, files);
    }
}

/// <summary>A folder of changed files, sized by their bytes, for the treemap.</summary>
public sealed class ChangeNode
{
    public string Name { get; }
    public string Path { get; }
    public ChangeNode? Parent { get; }
    public long Bytes { get; private set; }
    public int Files { get; private set; }
    public bool IsFile { get; }
    public bool IsNew { get; }
    private Dictionary<string, ChangeNode>? _children;
    public IEnumerable<ChangeNode> Children => _children?.Values ?? Enumerable.Empty<ChangeNode>();
    public bool HasChildren => _children is { Count: > 0 };

    private ChangeNode(string name, string path, ChangeNode? parent, bool isFile = false, bool isNew = false)
        => (Name, Path, Parent, IsFile, IsNew) = (name, path, parent, isFile, isNew);

    /// <summary>The folder tree of <paramref name="files"/> (paths with "/").</summary>
    internal static ChangeNode Build(string rootName, IEnumerable<ChangedFile> files)
    {
        var root = new ChangeNode(rootName, "", null);
        foreach (var f in files)
        {
            var parts = f.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            var node = root;
            for (var i = 0; i < parts.Length; i++)
            {
                node.Bytes += f.Size;
                node.Files++;
                node._children ??= new(StringComparer.Ordinal);
                var last = i == parts.Length - 1;
                if (!node._children.TryGetValue(parts[i], out var child))
                {
                    child = new ChangeNode(parts[i], string.Join('/', parts, 0, i + 1), node, last, last && f.IsNew);
                    node._children[parts[i]] = child;
                }
                node = child;
            }
            node.Bytes += f.Size;
            node.Files++;
        }
        return root;
    }

    /// <summary>Follows single-folder chains ("C/Users/me") so the map opens
    /// where the changes branch out.</summary>
    internal ChangeNode Collapsed()
    {
        var node = this;
        while (node._children is { Count: 1 } && node._children.Values.First() is { IsFile: false } only) node = only;
        return node;
    }
}

/// <summary>Squarified treemap layout (Bruls, Huizing and van Wijk): boxes
/// close to square, so their sizes can be compared by eye.</summary>
internal static class Treemap
{
    internal readonly record struct Box(double X, double Y, double W, double H);

    internal static List<(T Item, Box Box)> Layout<T>(IReadOnlyList<(T Item, double Weight)> items, Box area)
    {
        var result = new List<(T, Box)>();
        var sorted = items.Where(i => i.Weight > 0).OrderByDescending(i => i.Weight).ToList();
        var total = sorted.Sum(i => i.Weight);
        if (total <= 0 || area.W <= 0 || area.H <= 0) return result;
        var scale = area.W * area.H / total;
        var queue = sorted.Select(i => (i.Item, Area: i.Weight * scale)).ToList();
        var rect = area;
        var row = new List<(T Item, double Area)>();
        var at = 0;
        while (at < queue.Count)
        {
            var side = Math.Min(rect.W, rect.H);
            var next = queue[at];
            if (row.Count == 0 || Worst(row.Append(next), side) <= Worst(row, side)) { row.Add(next); at++; continue; }
            rect = Place(row, rect, result);
            row.Clear();
        }
        if (row.Count > 0) Place(row, rect, result);
        return result;
    }

    private static double Worst<T>(IEnumerable<(T Item, double Area)> row, double side) => WorstOf(row.Select(r => r.Area).ToList(), side);

    private static double WorstOf(List<double> areas, double side)
    {
        var sum = areas.Sum();
        if (sum <= 0 || side <= 0) return double.MaxValue;
        var max = areas.Max();
        var min = areas.Min();
        var s2 = side * side;
        return Math.Max(s2 * max / (sum * sum), sum * sum / (s2 * min));
    }

    private static Box Place<T>(List<(T Item, double Area)> row, Box rect, List<(T, Box)> result)
    {
        var sum = row.Sum(r => r.Area);
        if (rect.W >= rect.H)
        {
            // A column along the left side.
            var width = rect.H > 0 ? sum / rect.H : 0;
            var y = rect.Y;
            foreach (var (item, a) in row)
            {
                var h = width > 0 ? a / width : 0;
                result.Add((item, new Box(rect.X, y, width, h)));
                y += h;
            }
            return new Box(rect.X + width, rect.Y, Math.Max(0, rect.W - width), rect.H);
        }
        else
        {
            var height = rect.W > 0 ? sum / rect.W : 0;
            var x = rect.X;
            foreach (var (item, a) in row)
            {
                var w = height > 0 ? a / height : 0;
                result.Add((item, new Box(x, rect.Y, w, height)));
                x += w;
            }
            return new Box(rect.X, rect.Y + height, rect.W, Math.Max(0, rect.H - height));
        }
    }
}
