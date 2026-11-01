using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Backspace.Tests.TestHelpers;

/// <summary>
/// Builds a Windows-only test folder that exercises every NTFS edge
/// case a real backup engine has to survive:
///
///   * hardlinks       — two paths pointing at the same file content
///   * sparse files    — declared via FSCTL_SET_SPARSE, with a tiny
///                       on-disk footprint vs logical size
///   * alternate data streams (ADS) — content hung off a host file
///                       via the <c>file.txt:streamname</c> syntax
///   * junction points — directory reparse points that look like
///                       directories until you follow them
///   * long paths      — past the legacy MAX_PATH=260 limit; relies
///                       on .NET 6+ long-path support (the app's
///                       manifest opts in via LongPathAware)
///   * NTFS ACLs with inherited ACEs — a directory whose ACL is
///                       configured to flow down into child files
///                       even though we never set anything on the
///                       children directly
///   * bulky payload   — a randomly-named binary file padded out to
///                       a caller-configurable size so the total
///                       fixture lands at the requested footprint
///
/// Why this exists: bug reports about a backup tool that "lost my
/// files" almost always trace back to one of these features. Having
/// a deterministic generator means a developer can reproduce the
/// shape on demand, run a backup, restore it, and assert byte
/// equality — which is what
/// <see cref="Backspace.Tests.E2E.EdgeCaseBackupRestoreTests"/> does.
///
/// Every method is best-effort: hardlinks on a non-NTFS filesystem,
/// junctions across volumes, ACL writes without SeRestorePrivilege,
/// they all surface as <see cref="UnsupportedFeature"/> entries on
/// the returned manifest rather than aborting the whole generation.
/// Tests check the manifest to know which assertions they can make.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class EdgeCaseFileTree
{
    /// <summary>Approximate total payload, in bytes. Tests pass a small
    /// value (a few MB) for speed; the standalone "verify my real
    /// backup handles all this" runner uses 200 MB — large enough to
    /// exercise restic's chunked-upload path and rclone's
    /// multi-chunk cloud transfer, small enough that an end-to-end
    /// run against three real cloud accounts finishes in under five
    /// minutes on a residential up-link.</summary>
    public const long DefaultTotalBytes = 200L * 1024 * 1024;

    /// <summary>Build the tree under <paramref name="root"/>. The
    /// directory is created if it doesn't exist; otherwise it must be
    /// empty (a leftover fixture from a previous run is a coding
    /// error). Returns a <see cref="Manifest"/> describing what was
    /// successfully created vs what the host environment refused.</summary>
    public static Manifest Generate(string root, long totalBytes = DefaultTotalBytes, int randomSeed = 1337)
    {
        Directory.CreateDirectory(root);
        var manifest = new Manifest { Root = root };

        TryCreateBulkPayload(root, totalBytes, randomSeed, manifest);
        TryCreateHardLink(root, manifest);
        TryCreateSparseFile(root, manifest);
        TryCreateAdsFile(root, manifest);
        TryCreateJunctionPoint(root, manifest);
        TryCreateLongPath(root, manifest);
        TryCreateAclWithInheritedAces(root, manifest);

        return manifest;
    }

    // ---- bulky payload ----

    private static void TryCreateBulkPayload(string root, long totalBytes, int seed, Manifest manifest)
    {
        try
        {
            var path = Path.Combine(root, "bulk.dat");
            var size = Math.Max(0, totalBytes - ReservedSmallFilesBytes);
            using (var fs = File.Create(path))
            {
                var rng = new Random(seed);
                var buf = new byte[64 * 1024];
                long written = 0;
                while (written < size)
                {
                    rng.NextBytes(buf);
                    var write = (int)Math.Min(buf.Length, size - written);
                    fs.Write(buf, 0, write);
                    written += write;
                }
            }
            manifest.BulkPayloadPath = path;
            manifest.BulkPayloadBytes = new FileInfo(path).Length;
        }
        catch (Exception ex) { manifest.Failure(EdgeCaseFeature.BulkPayload, ex); }
    }

    /// <summary>How many bytes the small-file fixtures collectively use.
    /// Subtracted from the requested total so the BulkPayload can fill
    /// the rest and the fixture lands close to the asked-for size.</summary>
    private const int ReservedSmallFilesBytes = 256 * 1024;

    // ---- hardlinks ----

    private static void TryCreateHardLink(string root, Manifest manifest)
    {
        try
        {
            var src = Path.Combine(root, "hardlink-source.txt");
            var link = Path.Combine(root, "hardlink-clone.txt");
            File.WriteAllText(src, "two paths, one inode");
            if (!CreateHardLinkW(link, src, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            manifest.HardLinkSource = src;
            manifest.HardLinkClone = link;
        }
        catch (Exception ex) { manifest.Failure(EdgeCaseFeature.HardLinks, ex); }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr reserved);

    // ---- sparse files ----

    private static void TryCreateSparseFile(string root, Manifest manifest)
    {
        try
        {
            var path = Path.Combine(root, "sparse.dat");
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                // Mark sparse BEFORE writing the tail. SetLength on a
                // sparse-marked file leaves an unallocated hole; without
                // the IOCTL Windows commits real clusters for the full
                // length.
                if (!DeviceIoControl(fs.SafeFileHandle.DangerousGetHandle(),
                        FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

                // 64 MB logical, ~4 KB on-disk.
                fs.SetLength(64L * 1024 * 1024);
                fs.Seek(-1, SeekOrigin.End);
                fs.WriteByte((byte)'X');
            }

            manifest.SparseFilePath = path;
            manifest.SparseFileLogicalBytes = new FileInfo(path).Length;
        }
        catch (Exception ex) { manifest.Failure(EdgeCaseFeature.SparseFiles, ex); }
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    private const uint FSCTL_SET_SPARSE = 0x000900C4;

    // ---- alternate data streams ----

    private static void TryCreateAdsFile(string root, Manifest manifest)
    {
        try
        {
            // Host file carries the default-stream content the way a
            // normal file does; the ADS hangs off the same inode via
            // the colon-suffix syntax. Both have to round-trip through
            // a real backup.
            var hostPath = Path.Combine(root, "ads-host.txt");
            File.WriteAllText(hostPath, "default-stream content");
            var adsPath = hostPath + ":hidden";
            File.WriteAllText(adsPath, "alternate-stream content");

            manifest.AdsHostPath = hostPath;
            manifest.AdsStreamName = "hidden";
        }
        catch (Exception ex) { manifest.Failure(EdgeCaseFeature.AlternateDataStreams, ex); }
    }

    // ---- junction points ----

    private static void TryCreateJunctionPoint(string root, Manifest manifest)
    {
        try
        {
            // Target dir lives next to the junction so the relative
            // resolution stays portable across drives.
            var target = Path.Combine(root, "junction-target");
            var junction = Path.Combine(root, "junction-link");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "inside.txt"), "behind the junction");

            // `mklink /J` is the canonical shell-out for a directory
            // junction; the corresponding Win32 API
            // (DeviceIoControl FSCTL_SET_REPARSE_POINT) is ~80 lines
            // of P/Invoke we don't gain anything from owning.
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{junction}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new InvalidOperationException(
                    $"mklink /J failed (exit {p.ExitCode}): {p.StandardError.ReadToEnd().Trim()}");

            manifest.JunctionTarget = target;
            manifest.JunctionPath = junction;
        }
        catch (Exception ex) { manifest.Failure(EdgeCaseFeature.JunctionPoints, ex); }
    }

    // ---- long paths ----

    private static void TryCreateLongPath(string root, Manifest manifest)
    {
        try
        {
            // Build a deep tree where the leaf path exceeds the
            // legacy MAX_PATH=260 limit. Each segment is ~40 chars; 8
            // segments push us comfortably past 320 chars when joined
            // with the test root prefix.
            //
            // The "\\?\" prefix is the explicit opt-in to long-path
            // resolution at the Win32 layer. .NET 6+ uses it implicitly
            // when the manifest declares longPathAware (Backspace's
            // does), but tests run under the test host's manifest
            // which may not — so we apply it explicitly here.
            var seg = new string('a', 40);
            var deep = Path.Combine(root, "longpath");
            for (int i = 0; i < 8; i++) deep = Path.Combine(deep, $"{seg}-{i:00}");

            Directory.CreateDirectory(@"\\?\" + deep);
            var leafFile = Path.Combine(deep, "leaf.txt");
            File.WriteAllText(@"\\?\" + leafFile, "found at the bottom of a very long path");

            if (leafFile.Length <= 260)
                throw new InvalidOperationException($"Long-path leaf length {leafFile.Length} is not actually long.");

            manifest.LongPathLeafFile = leafFile;
            manifest.LongPathLeafLength = leafFile.Length;
        }
        catch (Exception ex) { manifest.Failure(EdgeCaseFeature.LongPaths, ex); }
    }

    // ---- ACLs with inherited ACEs ----

    private static void TryCreateAclWithInheritedAces(string root, Manifest manifest)
    {
        try
        {
            var aclDir = Path.Combine(root, "acl-parent");
            Directory.CreateDirectory(aclDir);
            var childFile = Path.Combine(aclDir, "child-inherits.txt");
            File.WriteAllText(childFile, "child file (should pick up the parent's inheritable ACE)");

            // Add an explicit Read+Write-Attributes ACE on the parent
            // marked OBJECT_INHERIT_ACE so the child file inherits it
            // even though we never touch the child's ACL. A real
            // backup engine has to roundtrip both the parent's ACE
            // descriptor AND the child's "this came from inheritance"
            // metadata so a restored child re-inherits the same way.
            var ds = new DirectoryInfo(aclDir).GetAccessControl();
            var me = WindowsIdentity.GetCurrent().User!;
            var rule = new FileSystemAccessRule(
                identity: me,
                fileSystemRights: FileSystemRights.ReadAndExecute | FileSystemRights.WriteAttributes,
                inheritanceFlags: InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                propagationFlags: PropagationFlags.None,
                type: AccessControlType.Allow);
            ds.AddAccessRule(rule);
            new DirectoryInfo(aclDir).SetAccessControl(ds);

            manifest.AclParentDir = aclDir;
            manifest.AclChildFile = childFile;
        }
        catch (Exception ex) { manifest.Failure(EdgeCaseFeature.NtfsAcls, ex); }
    }

    // ---- manifest ----

    /// <summary>Records what the generator was able to create and what
    /// the host environment refused. Tests check the manifest's
    /// boolean predicates before asserting against a feature.</summary>
    public sealed class Manifest
    {
        public string Root { get; set; } = "";

        public string? BulkPayloadPath { get; set; }
        public long BulkPayloadBytes { get; set; }

        public string? HardLinkSource { get; set; }
        public string? HardLinkClone  { get; set; }

        public string? SparseFilePath { get; set; }
        public long SparseFileLogicalBytes { get; set; }

        public string? AdsHostPath   { get; set; }
        public string? AdsStreamName { get; set; }

        public string? JunctionPath   { get; set; }
        public string? JunctionTarget { get; set; }

        public string? LongPathLeafFile { get; set; }
        public int LongPathLeafLength { get; set; }

        public string? AclParentDir { get; set; }
        public string? AclChildFile { get; set; }

        public System.Collections.Generic.List<UnsupportedFeature> Unsupported { get; } = new();

        internal void Failure(EdgeCaseFeature feature, Exception ex)
            => Unsupported.Add(new UnsupportedFeature(feature, ex.GetType().Name, ex.Message));

        public bool HardLinks => HardLinkClone is not null;
        public bool SparseFiles => SparseFilePath is not null;
        public bool AlternateDataStreams => AdsHostPath is not null;
        public bool JunctionPoints => JunctionPath is not null;
        public bool LongPaths => LongPathLeafFile is not null;
        public bool NtfsAcls => AclChildFile is not null;
        public bool BulkPayload => BulkPayloadPath is not null;
    }

    public enum EdgeCaseFeature
    {
        BulkPayload,
        HardLinks,
        SparseFiles,
        AlternateDataStreams,
        JunctionPoints,
        LongPaths,
        NtfsAcls,
    }

    public sealed record UnsupportedFeature(EdgeCaseFeature Feature, string ErrorKind, string ErrorMessage);
}
