using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services.WebDav;

/// <summary>
/// Read-only file tree the WebDAV server exposes to a mounted client.
/// Both nodes have a forward-slash <see cref="WebDavEntry.RelativePath"/>
/// rooted at the mount; directories carry no content, files lazily
/// produce a <see cref="Stream"/> when <see cref="OpenAsync"/> is called.
///
/// Two implementations are anticipated:
///   * Directory-backed (this commit): a thin wrapper over a local
///     folder. Used for the file-mirror snapshot store the
///     InMemoryBackupEngine produces, and for ad-hoc browsing.
///   * Engine-backed (future): walks
///     <see cref="IBackupEngine.ListSnapshotFilesAsync"/> for entries
///     and lazy-restores individual files via
///     <see cref="IBackupEngine.RestoreFilesAsync"/> on the GET path.
///     That's the path that turns "browse a 1 TB restic snapshot"
///     into an instant Explorer-mount instead of a 1 TB download.
/// </summary>
public interface ISnapshotTreeProvider
{
    /// <summary>Enumerate immediate children of a directory entry.
    /// <paramref name="relativeDir"/> = "" requests the root; deeper
    /// paths use forward-slash separators (e.g. "photos/2026").
    /// Returns null when the directory doesn't exist.</summary>
    Task<IReadOnlyList<WebDavEntry>?> ListAsync(string relativeDir, CancellationToken ct);

    /// <summary>Stat a single entry. Returns null if not found.</summary>
    Task<WebDavEntry?> StatAsync(string relativePath, CancellationToken ct);

    /// <summary>Open the byte content of a file entry. Caller disposes
    /// the returned stream. Implementations that need to materialise
    /// the file (engine-backed restore) do so here; latency lives in
    /// the GET handler, not the PROPFIND listing.</summary>
    Task<Stream> OpenAsync(string relativePath, CancellationToken ct);
}

/// <summary>One entry exposed through WebDAV. Paths use forward
/// slashes regardless of host OS; the server converts to URL form
/// at render time.</summary>
public sealed record WebDavEntry(
    string RelativePath,
    bool IsDirectory,
    long SizeBytes,
    System.DateTime LastWriteUtc);
