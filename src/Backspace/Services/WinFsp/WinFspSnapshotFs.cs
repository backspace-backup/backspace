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
///     reads them; <see cref="EngineBackedSnapshotTreeProvider.OpenAsync"/>
///     starts the file's download on first read. This is what makes
///     "browse a 1 TB snapshot" feel like O(1) instead of O(snapshot).
///   • Synchronous WinFSP API. The base class is synchronous; the
///     provider is async. Listings and look-ups come from memory and are
///     bridged with GetAwaiter on WinFSP's dispatch threads. A read of a
///     file still downloading is answered asynchronously instead
///     (STATUS_PENDING, then SendReadResponse) once its range has
///     arrived (U167).
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

    private FileSystemHost? _host;
    private readonly CancellationTokenSource _stopping = new();

    public WinFspSnapshotFs(ISnapshotTreeProvider provider)
    {
        _provider = provider;
        _volumeCreationUtc = DateTime.UtcNow;
        _readOnlySecurityDescriptor = BuildOwnerReadDescriptor();
    }

    /// <summary>Called by the host just before mounting: keeps the host to
    /// answer reads later.</summary>
    public override int Init(object Host)
    {
        _host = Host as FileSystemHost;
        return STATUS_SUCCESS;
    }

    public override void Unmounted(object Host)
    {
        try { _stopping.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>The longest a request may wait for its answer before Windows
    /// cancels it, in ms: WinFsp's maximum (its default is 5 minutes). The
    /// first read of a file waits for restic to find it in the destination,
    /// which on a slow cloud can be long (U167). WinFsp.Net does not expose
    /// the setting; it is a field of the volume parameters it passes to WinFsp.</summary>
    internal const uint IrpTimeoutMs = 600_000;

    internal static bool RaiseIrpTimeout(FileSystemHost host)
    {
        try
        {
            var field = typeof(FileSystemHost).GetField("_VolumeParams",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var parameters = field?.GetValue(host);
            var timeout = parameters?.GetType().GetField("IrpTimeout",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (field is null || parameters is null || timeout is null) { _log.Warning("WinFsp's read timeout could not be raised: this WinFsp.Net has no IrpTimeout field"); return false; }
            timeout.SetValue(parameters, IrpTimeoutMs);
            field.SetValue(host, parameters);
            return true;
        }
        catch (Exception ex) { _log.Warning(ex, "WinFsp's read timeout could not be raised"); return false; }
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
            NormalizedName = "\\" + entry.RelativePath.Replace('/', '\\');
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
            var toRead = (int)Math.Min(Length, size - Offset);
            if (node.EnsureStream(_provider) is ProgressiveFileStream progressive)
                return ReadArriving(node, progressive.File, Buffer, (long)Offset, toRead, out BytesTransferred);
            var managed = ArrayPoolRent(toRead);
            try
            {
                var read = node.ReadAt(_provider, (long)Offset, managed, toRead);
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

    /// <summary>A read of a file still downloading (U167). Bytes already here
    /// are answered at once. Otherwise the read is answered later, when its
    /// range has arrived, and this dispatcher thread returns: WinFsp's
    /// threads stay free for every other request. WinFsp opens a file only
    /// when none of its reads is in progress (anyone opening that same file
    /// waits), so each read waits only for its own range, never for the
    /// whole file.</summary>
    private int ReadArriving(SnapshotNode node, ProgressiveFile file, IntPtr buffer, long offset, int length, out uint bytesTransferred)
    {
        bytesTransferred = 0;
        var managed = ArrayPoolRent(length);
        var handedOver = false;
        try
        {
            if (file.TryRead(offset, managed.AsSpan(0, length), out var read))
            {
                if (read <= 0) return STATUS_END_OF_FILE;
                Marshal.Copy(managed, 0, buffer, read);
                bytesTransferred = (uint)read;
                return STATUS_SUCCESS;
            }
            if (_host is not { } host)
            {
                // Not mounted through a host (tests): wait here.
                read = file.ReadAsync(offset, managed.AsMemory(0, length), _stopping.Token).AsTask().GetAwaiter().GetResult();
                Marshal.Copy(managed, 0, buffer, read);
                bytesTransferred = (uint)read;
                return read > 0 ? STATUS_SUCCESS : STATUS_END_OF_FILE;
            }
            // WinFsp keeps the caller's buffer mapped until the answer is sent.
            var hint = host.GetOperationRequestHint();
            handedOver = true;
            _ = Task.Run(async () =>
            {
                var status = STATUS_UNEXPECTED_IO_ERROR;
                uint transferred = 0;
                try
                {
                    var arrived = await file.ReadAsync(offset, managed.AsMemory(0, length), _stopping.Token).ConfigureAwait(false);
                    Marshal.Copy(managed, 0, buffer, arrived);
                    transferred = (uint)arrived;
                    status = arrived > 0 ? STATUS_SUCCESS : STATUS_END_OF_FILE;
                }
                catch (OperationCanceledException) { status = STATUS_CANCELLED; }
                catch (Exception ex) { _log.Warning(ex, "Read failed for {Path}", node.Entry.RelativePath); }
                finally { ArrayPoolReturn(managed); }
                try { host.SendReadResponse(hint, status, transferred); }
                catch (Exception ex) { _log.Warning(ex, "Answering a read of {Path} failed", node.Entry.RelativePath); }
            });
            return STATUS_PENDING;
        }
        finally { if (!handedOver) ArrayPoolReturn(managed); }
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
            // Context belongs to one ReadDirectory request. Windows may retry
            // the same handle with a marker or restart it from the beginning.
            // The preceding request may also have fetched an entry that did
            // not fit its output buffer, so never reuse its advanced cursor.
            // Marker-resume: WinFSP passes back the filename of the last
            // returned entry; start after it (case-insensitive, the order
            // the children are sorted in). A binary search, so a 20,000-file
            // folder read in many requests does not rescan from the start.
            var children = node.GetChildren(_provider);
            enumerator = new DirEnumerator(children.Entries, Marker is null ? 0 : children.IndexAfter(Marker));
            Context = enumerator;
        }

        if (!enumerator.MoveNext()) return false;
        var child = enumerator.Current!;
        FileName = LeafFor(child);
        FileInfo = ToFileInfo(child);
        return true;
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

    internal static string LeafFor(WebDavEntry entry)
    {
        var path = entry.RelativePath;
        var idx = path.LastIndexOf('/');
        return idx >= 0 ? path[(idx + 1)..] : path;
    }

    private static uint AttributesFor(WebDavEntry entry)
    {
        const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        const uint FILE_ATTRIBUTE_READONLY = 0x1;
        // Tells the search indexer to leave the snapshot alone, should anyone
        // add the drive to it: indexing would open, and so fetch, every file
        // (U162). Listings and sizes come from memory regardless.
        const uint FILE_ATTRIBUTE_NOT_CONTENT_INDEXED = 0x2000;
        if (entry.IsDirectory) return FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_READONLY | FILE_ATTRIBUTE_NOT_CONTENT_INDEXED;
        return FILE_ATTRIBUTE_READONLY | FILE_ATTRIBUTE_NOT_CONTENT_INDEXED;
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

    /// <summary>Read-only access for the person who mounted the snapshot and
    /// for Windows itself, nobody else. The volume's device is reachable by
    /// every account through \\?\GLOBALROOT, and a snapshot can hold files
    /// the background service read with backup rights, such as other
    /// profiles or the registry hives (U152). Writes are refused by the file
    /// system itself whatever this says.</summary>
    internal static byte[] BuildOwnerReadDescriptor()
    {
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        return Descriptor(user);
    }

    internal static byte[] Descriptor(string userSid)
    {
        var sd = new RawSecurityDescriptor($"O:{userSid}G:{userSid}D:P(A;;FRFX;;;{userSid})(A;;FRFX;;;SY)");
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
        private SortedChildren? _children;
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

        public int ReadAt(ISnapshotTreeProvider provider, long offset, byte[] buffer, int count)
        {
            // WinFSP can issue concurrent reads on one open handle. Position
            // and Read must share the lock; otherwise one request can seek
            // underneath another and return bytes from the wrong offset.
            lock (_streamLock)
            {
                var stream = EnsureStream(provider);
                stream.Position = offset;
                return stream.Read(buffer, 0, count);
            }
        }

        public SortedChildren GetChildren(ISnapshotTreeProvider provider)
        {
            lock (_streamLock)
                return _children ??= new SortedChildren(provider.ListAsync(Entry.RelativePath, CancellationToken.None)
                    .GetAwaiter().GetResult() ?? Array.Empty<WebDavEntry>());
        }

        public void Dispose()
        {
            lock (_streamLock)
            {
                try { _stream?.Dispose(); } catch { }
                _stream = null;
            }
        }
    }

    /// <summary>A folder's entries in enumeration order (case-insensitive by
    /// name). A mounted snapshot's listings already come in that order, so
    /// sorting is skipped unless a provider returns them otherwise.</summary>
    internal sealed class SortedChildren
    {
        private readonly string[] _leaves;
        public IReadOnlyList<WebDavEntry> Entries { get; }

        public SortedChildren(IReadOnlyList<WebDavEntry> entries)
        {
            var leaves = new string[entries.Count];
            for (var i = 0; i < leaves.Length; i++) leaves[i] = LeafFor(entries[i]);
            var sorted = true;
            for (var i = 1; i < leaves.Length && sorted; i++)
                sorted = string.Compare(leaves[i - 1], leaves[i], StringComparison.OrdinalIgnoreCase) <= 0;
            if (!sorted)
            {
                var copy = entries.ToArray();
                Array.Sort(leaves, copy, StringComparer.OrdinalIgnoreCase);
                entries = copy;
            }
            _leaves = leaves;
            Entries = entries;
        }

        /// <summary>Index of the first entry named after <paramref name="marker"/>.</summary>
        public int IndexAfter(string marker)
        {
            int lo = 0, hi = _leaves.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (string.Compare(_leaves[mid], marker, StringComparison.OrdinalIgnoreCase) <= 0) lo = mid + 1; else hi = mid;
            }
            return lo;
        }
    }

    /// <summary>One-shot enumerator over a directory's children.
    /// Materialised on first <see cref="ReadDirectoryEntry"/>; held
    /// in WinFSP's <c>Context</c> slot for the duration of the
    /// enumeration. Children are sorted case-insensitively to make
    /// the Marker-resume protocol deterministic.</summary>
    private sealed class DirEnumerator
    {
        private readonly IReadOnlyList<WebDavEntry> _children;
        private int _index;

        public DirEnumerator(IReadOnlyList<WebDavEntry> children, int start)
        {
            _children = children;
            _index = start - 1;
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
