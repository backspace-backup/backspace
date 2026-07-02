using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services.WebDav;
using Fsp;
using Fsp.Interop;
using Serilog;
using FspFileInfo = Fsp.Interop.FileInfo;
using FspVolumeInfo = Fsp.Interop.VolumeInfo;

namespace Backspace.Services.WinFsp;

/// <summary>
/// Read-only WinFSP file system backed by a
/// <see cref="ISnapshotTreeProvider"/>. WinFSP mounts a user-mode
/// callback object as a real Windows volume; Explorer / cmd / any
/// process opens files via standard Win32 calls and the kernel pipes
/// the requests back to this class.
///
/// Design constraints:
///   • Read-only. We override only the dispatch path WinFSP needs for
///     enumerate + read; Write / Create / Delete / Rename return the
///     base-class default of STATUS_INVALID_DEVICE_REQUEST.
///   • Lazy materialisation. Files don't exist on disk until the user
///     opens them; <see cref="EngineBackedSnapshotTreeProvider.OpenAsync"/>
///     spawns the restic restore on first read. This is what makes
///     "browse a 1 TB snapshot" feel like O(1) instead of O(snapshot).
///   • Synchronous WinFSP API. The base class is synchronous; the
///     provider is async. Bridge via <see cref="GetAwaiter"/> on a
///     captured task — this runs on WinFSP's own dispatch threads
///     (one per FspFileSystemStartDispatcher worker), so blocking is
///     correct and expected.
///   • No security model. We return a single "everyone read-only"
///     security descriptor for every path. Restic snapshots have no
///     concept of NTFS ACLs; preserving the original ACL would
///     require a per-file metadata side-channel we don't have.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinFspSnapshotFs : FileSystemBase
{
    private static ILogger _log => AppLogger.For<WinFspSnapshotFs>();

    private readonly ISnapshotTreeProvider _provider;
    private readonly DateTime _volumeCreationUtc;
    private readonly byte[] _readOnlySecurityDescriptor;

    public WinFspSnapshotFs(ISnapshotTreeProvider provider)
    {
        _provider = provider;
        _volumeCreationUtc = DateTime.UtcNow;
        _readOnlySecurityDescriptor = BuildEveryoneReadDescriptor();
    }

    // -------- volume --------

    public override int GetVolumeInfo(out FspVolumeInfo VolumeInfo)
    {
        VolumeInfo = default;
        // Snapshot is read-only; report a fake "1 TB used, 0 free" so
        // Explorer doesn't show a misleading "0 % full" disk-space
        // bar. Free space = 0 is the conventional signal to clients
        // that this is not a writeable volume.
        VolumeInfo.TotalSize = 1L << 40;
        VolumeInfo.FreeSize = 0;
        VolumeInfo.SetVolumeLabel("Snapshot");
        return STATUS_SUCCESS;
    }

    public override int SetVolumeLabel(string VolumeLabel, out FspVolumeInfo VolumeInfo)
    {
        VolumeInfo = default;
        return STATUS_MEDIA_WRITE_PROTECTED;
    }

    // -------- security --------

    public override int GetSecurityByName(
        string FileName,
        out uint FileAttributes,
        ref byte[] SecurityDescriptor)
    {
        FileAttributes = 0;
        try
        {
            var entry = StatSync(FileName);
            if (entry is null) return STATUS_OBJECT_NAME_NOT_FOUND;
            FileAttributes = AttributesFor(entry);
            SecurityDescriptor = _readOnlySecurityDescriptor;
            return STATUS_SUCCESS;
        }
        catch
        {
            // Stat surfaced an unexpected exception; treat as I/O
            // error rather than crash the dispatcher. StatSync
            // already logged via Serilog so we don't double-log
            // on the hot path.
            return STATUS_UNEXPECTED_IO_ERROR;
        }
    }

    // -------- open / close --------

    public override int Open(
        string FileName,
        uint CreateOptions,
        uint GrantedAccess,
        out object? FileNode,
        out object? FileDesc,
        out FspFileInfo FileInfo,
        out string? NormalizedName)
    {
        FileNode = null;
        FileDesc = null;
        FileInfo = default;
        NormalizedName = null;

        try
        {
            var entry = StatSync(FileName);
            if (entry is null) return STATUS_OBJECT_NAME_NOT_FOUND;

            var node = new SnapshotNode(entry);
            FileDesc = node;
            FileInfo = ToFileInfo(entry);
            return STATUS_SUCCESS;
        }
        catch
        {
            return STATUS_UNEXPECTED_IO_ERROR;
        }
    }

    public override void Close(object FileNode, object FileDesc)
    {
        if (FileDesc is SnapshotNode node)
        {
            node.Dispose();
        }
    }

    // -------- read --------

    public override int GetFileInfo(object FileNode, object FileDesc, out FspFileInfo FileInfo)
    {
        FileInfo = default;
        if (FileDesc is not SnapshotNode node) return STATUS_INVALID_HANDLE;
        FileInfo = ToFileInfo(node.Entry);
        return STATUS_SUCCESS;
    }

    public override int Read(
        object FileNode,
        object FileDesc,
        IntPtr Buffer,
        ulong Offset,
        uint Length,
        out uint BytesTransferred)
    {
        BytesTransferred = 0;
        if (FileDesc is not SnapshotNode node) return STATUS_INVALID_HANDLE;
        if (node.Entry.IsDirectory) return STATUS_FILE_IS_A_DIRECTORY;

        var size = (ulong)node.Entry.SizeBytes;
        if (Offset >= size) return STATUS_END_OF_FILE;

        try
        {
            var stream = node.EnsureStream(_provider);
            stream.Position = (long)Offset;

            var toRead = (int)Math.Min(Length, size - Offset);
            var managed = ArrayPoolRent(toRead);
            try
            {
                var read = stream.Read(managed, 0, toRead);
                if (read <= 0) return STATUS_END_OF_FILE;
                Marshal.Copy(managed, 0, Buffer, read);
                BytesTransferred = (uint)read;
                return STATUS_SUCCESS;
            }
            finally { ArrayPoolReturn(managed); }
        }
        catch (FileNotFoundException ex)
        {
            _log.Warning(ex, "Read failed for {Path}: file vanished from snapshot", node.Entry.RelativePath);
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Read failed for {Path}", node.Entry.RelativePath);
            return STATUS_UNEXPECTED_IO_ERROR;
        }
    }

    // -------- enumerate --------

    public override bool ReadDirectoryEntry(
        object FileNode,
        object FileDesc,
        string? Pattern,
        string? Marker,
        ref object? Context,
        out string? FileName,
        out FspFileInfo FileInfo)
    {
        FileName = null;
        FileInfo = default;
        if (FileDesc is not SnapshotNode node || !node.Entry.IsDirectory) return false;

        if (Context is not DirEnumerator enumerator)
        {
            enumerator = node.EnsureEnumerator(_provider);
            Context = enumerator;
        }

        // Marker-resume: WinFSP passes back the filename of the last
        // returned entry. Skip everything ≤ Marker (case-insensitive,
        // matching NTFS's CasePreservedNames=true convention) so
        // resumption picks up at the next entry.
        while (enumerator.MoveNext())
        {
            var child = enumerator.Current!;
            var leaf = LeafFor(child);
            if (Marker is not null)
            {
                if (string.Compare(leaf, Marker, StringComparison.OrdinalIgnoreCase) <= 0)
                    continue;
            }
            FileName = leaf;
            FileInfo = ToFileInfo(child);
            return true;
        }

        return false;
    }

    public override int CanDelete(object FileNode, object FileDesc, string FileName)
        => STATUS_MEDIA_WRITE_PROTECTED;

    // -------- helpers --------

    private WebDavEntry? StatSync(string fileName)
    {
        var rel = NormalizeFromWinFsp(fileName);
        try
        {
            return _provider.StatAsync(rel, CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Stat failed for {Path}", rel);
            return null;
        }
    }

    /// <summary>WinFSP passes paths in Windows form: '\foo\bar'. Our
    /// provider uses forward-slash relative paths with no leading
    /// slash. Root = '\' → "".</summary>
    internal static string NormalizeFromWinFsp(string fileName)
    {
        if (string.IsNullOrEmpty(fileName) || fileName == "\\") return "";
        return fileName.Replace('\\', '/').Trim('/');
    }

    private static string LeafFor(WebDavEntry entry)
    {
        var path = entry.RelativePath;
        var idx = path.LastIndexOf('/');
        return idx >= 0 ? path[(idx + 1)..] : path;
    }

    private static uint AttributesFor(WebDavEntry entry)
    {
        const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        const uint FILE_ATTRIBUTE_READONLY = 0x1;
        const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        if (entry.IsDirectory) return FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_READONLY;
        return FILE_ATTRIBUTE_READONLY | FILE_ATTRIBUTE_NORMAL;
    }

    private FspFileInfo ToFileInfo(WebDavEntry entry)
    {
        var fi = default(FspFileInfo);
        fi.FileAttributes = AttributesFor(entry);
        fi.FileSize = (ulong)entry.SizeBytes;
        fi.AllocationSize = ((fi.FileSize + 4095) / 4096) * 4096;
        var ts = (ulong)entry.LastWriteUtc.ToFileTimeUtc();
        fi.CreationTime = ts;
        fi.LastAccessTime = ts;
        fi.LastWriteTime = ts;
        fi.ChangeTime = ts;
        fi.IndexNumber = 0;
        fi.HardLinks = 0;
        return fi;
    }

    private static byte[] BuildEveryoneReadDescriptor()
    {
        // SDDL: Owner=BUILTIN\Administrators, Group=BUILTIN\Administrators,
        // DACL: Allow (FA = file all access — meaning read on a r/o FS)
        // to everyone (WD = World). The actual read-only-ness is
        // enforced by our overrides (or lack thereof, defaulting to
        // STATUS_INVALID_DEVICE_REQUEST). The SD just keeps Explorer
        // and AV scanners from balking when they probe for "can I
        // read this".
        var sd = new RawSecurityDescriptor("O:BAG:BAD:P(A;;FA;;;WD)");
        var bytes = new byte[sd.BinaryLength];
        sd.GetBinaryForm(bytes, 0);
        return bytes;
    }

    // Use ArrayPool to avoid GC churn on the hot read path. Each Read
    // pulls at most Length bytes (typically 64 KB or 1 MB depending
    // on the caller's I/O size), so pooled buffers pay off vs new[].
    private static byte[] ArrayPoolRent(int min) => System.Buffers.ArrayPool<byte>.Shared.Rent(min);
    private static void ArrayPoolReturn(byte[] buf) => System.Buffers.ArrayPool<byte>.Shared.Return(buf);

    // -------- node + enumerator --------

    /// <summary>Per-handle state. Holds the entry stat we already
    /// resolved during Open, plus lazy backers for reads and
    /// directory enumerations.</summary>
    private sealed class SnapshotNode : IDisposable
    {
        public WebDavEntry Entry { get; }
        private Stream? _stream;
        private DirEnumerator? _enumerator;
        private readonly object _streamLock = new();

        public SnapshotNode(WebDavEntry entry) { Entry = entry; }

        public Stream EnsureStream(ISnapshotTreeProvider provider)
        {
            lock (_streamLock)
            {
                if (_stream is null)
                {
                    _stream = provider.OpenAsync(Entry.RelativePath, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    if (!_stream.CanSeek)
                    {
                        // The provider contract returns a FileStream over a
                        // local cache file (EngineBackedSnapshotTreeProvider).
                        // If a custom provider returned a non-seekable
                        // stream we'd need to buffer; surface the bug here.
                        throw new InvalidOperationException(
                            "ISnapshotTreeProvider returned a non-seekable stream; " +
                            "WinFSP requires random-access reads.");
                    }
                }
                return _stream;
            }
        }

        public DirEnumerator EnsureEnumerator(ISnapshotTreeProvider provider)
        {
            _enumerator ??= new DirEnumerator(provider, Entry.RelativePath);
            return _enumerator;
        }

        public void Dispose()
        {
            try { _stream?.Dispose(); } catch { }
            _stream = null;
        }
    }

    /// <summary>One-shot enumerator over a directory's children.
    /// Materialised on first <see cref="ReadDirectoryEntry"/>; held
    /// in WinFSP's <c>Context</c> slot for the duration of the
    /// enumeration. Children are sorted case-insensitively to make
    /// the Marker-resume protocol deterministic.</summary>
    private sealed class DirEnumerator
    {
        private readonly List<WebDavEntry> _children;
        private int _index;

        public DirEnumerator(ISnapshotTreeProvider provider, string dirRelativePath)
        {
            var listed = provider.ListAsync(dirRelativePath, CancellationToken.None)
                .GetAwaiter().GetResult() ?? Array.Empty<WebDavEntry>();
            _children = listed
                .OrderBy(e => LeafFor(e), StringComparer.OrdinalIgnoreCase)
                .ToList();
            _index = -1;
        }

        public WebDavEntry? Current => _index >= 0 && _index < _children.Count ? _children[_index] : null;

        public bool MoveNext()
        {
            if (_index + 1 >= _children.Count) return false;
            _index++;
            return true;
        }
    }
}
