using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services;

/// <summary>
/// Directory listing shared by the size probe, the savings estimate and
/// the filter preview. Sizes and attributes come from the directory
/// listing itself, so a file costs no extra call to the file system:
/// stat-ing each file separately made a whole-drive estimate stall
/// long before the end of the drive.
/// </summary>
internal static class TreeWalk
{
    public readonly record struct Entry(string FullPath, bool IsDirectory, long Length, bool IsLink, FileAttributes Attributes = 0);

    // Set by cloud providers on files kept only online (winnt.h).
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000, RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>True for a file kept only in the cloud (Dropbox, OneDrive,
    /// Google Drive placeholders): its size is not on this computer, and a
    /// backup that skips such files never reads it (U123).</summary>
    public static bool IsCloudOnly(FileAttributes attributes)
        => (attributes & (RecallOnOpen | RecallOnDataAccess | FileAttributes.Offline)) != 0;

    // Hidden and system files are part of a backup, so nothing is
    // skipped by attribute (the default would skip both).
    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>The folder's full path without a trailing separator,
    /// except on a drive root: <c>C:\</c> must stay <c>C:\</c>, because
    /// <c>C:</c> means the current folder on drive C.</summary>
    public static string Root(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>True when <paramref name="path"/> is a whole drive or share root.</summary>
    public static bool IsDriveRoot(string path)
    {
        var full = Root(path);
        return string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The entries directly inside <paramref name="dir"/>, or
    /// an empty list when the folder can't be read.</summary>
    public static IReadOnlyList<Entry> List(string dir) => ListOrNull(dir) ?? Array.Empty<Entry>();

    /// <summary>Like <see cref="List"/>, but null when the folder can't be read.</summary>
    public static IReadOnlyList<Entry>? ListOrNull(string dir)
    {
        try
        {
            return new FileSystemEnumerable<Entry>(dir,
                (ref FileSystemEntry e) => new Entry(e.ToFullPath(), e.IsDirectory, e.IsDirectory ? 0 : e.Length,
                    (e.Attributes & FileAttributes.ReparsePoint) != 0, e.Attributes),
                Options).ToList();
        }
        catch (UnauthorizedAccessException) { return null; }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// Visits every entry below <paramref name="root"/>, reading several
    /// folders at once: on a whole drive this is about four times faster
    /// than one folder at a time. Links are never followed or visited.
    /// </summary>
    /// <param name="visit">Called on several threads at once, with each
    /// entry and the mark of the folder holding it (the root's is false).
    /// For a folder it returns the mark its own entries get, or null to
    /// leave the folder out.</param>
    /// <param name="unreadable">Called with each folder that couldn't be read
    /// (another account's profile, for example), so it isn't silently missing.</param>
    /// <param name="link">Called with each link left out, with the mark of its folder.</param>
    /// <returns>False when <paramref name="deadline"/> passed before the end.</returns>
    public static bool Walk(string root, Func<Entry, bool, bool?> visit, DateTime deadline, CancellationToken ct,
        Action<string>? unreadable = null, Action<Entry, bool>? link = null)
    {
        var queue = new ConcurrentStack<(string Dir, bool Mark)>();
        queue.Push((root, false));
        var pending = 1;
        var finished = true;
        var workers = Math.Clamp(Environment.ProcessorCount, 2, 8);
        var options = new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct };
        Parallel.For(0, workers, options, _ =>
        {
            while (Volatile.Read(ref pending) > 0 && Volatile.Read(ref finished))
            {
                ct.ThrowIfCancellationRequested();
                if (DateTime.UtcNow > deadline) { Volatile.Write(ref finished, false); break; }
                if (!queue.TryPop(out var item)) { Thread.Sleep(1); continue; }
                try
                {
                    var entries = ListOrNull(item.Dir);
                    if (entries is null) { unreadable?.Invoke(item.Dir); continue; }
                    foreach (var entry in entries)
                    {
                        if (entry.IsLink) { link?.Invoke(entry, item.Mark); continue; }
                        var mark = visit(entry, item.Mark);
                        if (entry.IsDirectory && mark is { } m)
                        {
                            Interlocked.Increment(ref pending);
                            queue.Push((entry.FullPath, m));
                        }
                    }
                }
                finally { Interlocked.Decrement(ref pending); }
            }
        });
        return finished;
    }

    /// <summary>Path of <paramref name="full"/> below <paramref name="root"/>, with forward slashes.</summary>
    public static string Relative(string root, string full)
    {
        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            full = full.Substring(root.Length).TrimStart('\\', '/');
        return full.Replace('\\', '/');
    }
}
