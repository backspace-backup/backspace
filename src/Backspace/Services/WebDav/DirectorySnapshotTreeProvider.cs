using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services.WebDav;

/// <summary>
/// <see cref="ISnapshotTreeProvider"/> backed by a local filesystem
/// directory. Used to mount the InMemoryBackupEngine's
/// <c>.backspace-fake-snapshots</c> stores directly, and as the
/// terminal half of an engine-backed provider once that lands (the
/// engine materialises files into a temp dir; this provider serves
/// them over WebDAV).
/// </summary>
public sealed class DirectorySnapshotTreeProvider : ISnapshotTreeProvider
{
    public string Root { get; }

    public DirectorySnapshotTreeProvider(string root)
    {
        Root = Path.GetFullPath(root);
        if (!Directory.Exists(Root))
            throw new DirectoryNotFoundException($"Snapshot root does not exist: {Root}");
    }

    public Task<IReadOnlyList<WebDavEntry>?> ListAsync(string relativeDir, CancellationToken ct)
    {
        var abs = ResolveLocal(relativeDir);
        if (abs is null || !Directory.Exists(abs))
            return Task.FromResult<IReadOnlyList<WebDavEntry>?>(null);

        IReadOnlyList<WebDavEntry> entries = Directory
            .EnumerateFileSystemEntries(abs)
            .Select(p =>
            {
                var info = new FileInfo(p);
                var isDir = (info.Attributes & FileAttributes.Directory) != 0;
                var rel = Path.GetRelativePath(Root, p).Replace('\\', '/');
                return new WebDavEntry(
                    RelativePath: rel,
                    IsDirectory: isDir,
                    SizeBytes: isDir ? 0 : info.Length,
                    LastWriteUtc: info.LastWriteTimeUtc);
            })
            .OrderBy(e => e.IsDirectory ? 0 : 1)
            .ThenBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult<IReadOnlyList<WebDavEntry>?>(entries);
    }

    public Task<WebDavEntry?> StatAsync(string relativePath, CancellationToken ct)
    {
        // Empty path = the mount root itself, which is always a
        // directory and always exists (the constructor checked).
        if (string.IsNullOrEmpty(relativePath))
            return Task.FromResult<WebDavEntry?>(new WebDavEntry("", true, 0, Directory.GetLastWriteTimeUtc(Root)));

        var abs = ResolveLocal(relativePath);
        if (abs is null) return Task.FromResult<WebDavEntry?>(null);

        if (Directory.Exists(abs))
        {
            return Task.FromResult<WebDavEntry?>(new WebDavEntry(
                relativePath, true, 0, Directory.GetLastWriteTimeUtc(abs)));
        }
        if (File.Exists(abs))
        {
            var fi = new FileInfo(abs);
            return Task.FromResult<WebDavEntry?>(new WebDavEntry(
                relativePath, false, fi.Length, fi.LastWriteTimeUtc));
        }
        return Task.FromResult<WebDavEntry?>(null);
    }

    public Task<Stream> OpenAsync(string relativePath, CancellationToken ct)
    {
        var abs = ResolveLocal(relativePath)
            ?? throw new FileNotFoundException("Path escapes the WebDAV root.", relativePath);
        return Task.FromResult<Stream>(new FileStream(
            abs, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true));
    }

    /// <summary>Resolve a forward-slash relative path against
    /// <see cref="Root"/>, refusing anything that escapes the root via
    /// "..". Returns null on traversal attempt; the server treats null
    /// as "not found" so an attacker can't distinguish a non-existent
    /// path from a denied escape.</summary>
    private string? ResolveLocal(string relativePath)
    {
        var safe = (relativePath ?? "").Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(Root, safe));
        // Boundary check: ensure Root ends in a separator before
        // StartsWith so a sibling like "C:\Users\me\snapextra" cannot
        // satisfy a prefix match against a Root of "C:\Users\me\snap".
        // Also use a case-aware comparison: case-insensitive on
        // Windows (NTFS / ReFS), Ordinal on Linux/macOS (case-sensitive
        // filesystems). The Path.GetRelativePath alternative gives a
        // path starting with ".." on escape, which is the canonical
        // check, but StartsWith with a separator-anchored Root is
        // equivalent here and avoids the extra normalisation pass.
        var rootWithSep = Root.EndsWith(Path.DirectorySeparatorChar)
            ? Root
            : Root + Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        // candidate must equal Root exactly OR be under root + sep.
        if (!string.Equals(candidate, Root, cmp)
            && !candidate.StartsWith(rootWithSep, cmp))
            return null;
        return candidate;
    }
}
