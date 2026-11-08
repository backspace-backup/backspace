using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services.WebDav;
using Fsp;
using Serilog;

namespace Backspace.Services.WinFsp;

/// <summary>
/// Mount a snapshot tree as a Windows drive letter using the WinFSP
/// runtime. Replaces a prior WebDAV-based mount path that was broken in
/// practice because Windows 10/11 silently reject the loopback WebDAV
/// redirector on custom ports (see GOTCHAS.md and ROADMAP "Mount
/// feature actually working" for the back-story).
///
/// The flow on Windows:
///   1. Check that WinFSP runtime is installed
///      (<see cref="WinFspRuntimeDetector.IsInstalled"/>). If missing,
///      surface an actionable "install WinFSP" message and bail.
///   2. Pick a free drive letter walking down from Z:.
///   3. Construct a <see cref="WinFspSnapshotFs"/> over the provided
///      tree provider, host it via <see cref="FileSystemHost"/>, and
///      Mount() it at that drive letter.
///   4. Launch Explorer.exe at the drive root.
///   5. <see cref="DisposeAsync"/> calls Unmount() + Dispose() so the
///      drive vanishes cleanly when the user clicks "unmount" or
///      Backspace shuts down.
///
/// Non-Windows / no-WinFSP hosts: <see cref="MountAsync"/> returns
/// a <see cref="MountResult"/> with <c>Succeeded = false</c> and a
/// human-readable reason; the caller surfaces it as a toast.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinFspMountCoordinator : IAsyncDisposable
{
    private static ILogger _log => AppLogger.For<WinFspMountCoordinator>();
    // Hold allocation through OS registration; a second mount cannot choose
    // the same letter while the first is still warming its snapshot tree.
    private static readonly object MountGate = new();

    private readonly ISnapshotTreeProvider _provider;
    private readonly bool _openExplorer;
    private WinFspSnapshotFs? _fs;
    private FileSystemHost? _host;
    private string? _mountedDriveLetter;
    private bool _disposed;

    public WinFspMountCoordinator(ISnapshotTreeProvider provider, bool openExplorer = true)
    {
        _provider = provider;
        _openExplorer = openExplorer;
    }

    /// <summary>The drive letter the snapshot is mounted at, e.g.
    /// "Z:". Null before <see cref="MountAsync"/> completes
    /// successfully.</summary>
    public string? DriveLetter => _mountedDriveLetter;

    /// <summary>Reads what the drive shows first, then mounts it.
    /// <paramref name="progress"/> hears which revision is being read and how
    /// many of its files and folders have been read so far.</summary>
    public async Task<MountResult> MountAsync(CancellationToken ct = default, IProgress<MountReading>? progress = null)
    {
        if (!OperatingSystem.IsWindows())
            return MountResult.Failed("Snapshot mount is Windows-only.");

        if (!WinFspRuntimeDetector.IsInstalled())
        {
            return MountResult.Failed(
                "WinFSP runtime isn't installed. Download and install the latest " +
                $"signed MSI from {WinFspRuntimeDetector.InstallUrl} and try again.");
        }

        // Defence-in-depth: pre-load winfsp-x64.dll by absolute path
        // before we touch any Fsp.* type. Idempotent — a no-op when
        // already loaded. This catches every caller (dist app, test
        // harness, ad-hoc tooling) under the same DLL-load conditions
        // so the test suite can't false-green while production fails
        // (which is exactly what happened earlier).
        if (!WinFspNativeLoader.EnsureRegistered())
        {
            return MountResult.Failed(
                "Backspace couldn't pre-load the WinFSP native library. " +
                "Reinstall WinFSP via the bundled installer (click Mount again) " +
                $"or directly from {WinFspRuntimeDetector.InstallUrl}.");
        }

        // Read the snapshot's folder tree BEFORE the drive appears (U158).
        // A snapshot never changes, so once it is read every folder opens
        // from memory; an initial wait with visible progress replaces a
        // drive whose every folder made Explorer wait for the engine. It
        // also surfaces an unreachable destination as an error, not as a
        // drive that silently fails to open.
        try
        {
            switch (_provider)
            {
                case BackupMountTreeProvider backup:
                    if (await backup.PrepareAsync(progress, ct).ConfigureAwait(false) == 0)
                        return MountResult.Failed("No snapshots yet. Run a backup first, then come back to mount it.");
                    break;
                case EngineBackedSnapshotTreeProvider snapshot:
                    await snapshot.LoadTreeAsync(ct).ConfigureAwait(false);
                    break;
                default:
                    await _provider.ListAsync("", ct).ConfigureAwait(false);
                    break;
            }
            _log.Information("Snapshot tree read before mounting");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception prefetchEx)
        {
            _log.Error(prefetchEx, "Reading the snapshot tree failed");
            return MountResult.Failed(
                "Couldn't read the snapshot's folders from the destination. " +
                "Check the destination is reachable and try again. " +
                "Underlying error: " + prefetchEx.Message);
        }

        // WinFSP's Mount() call is synchronous and lightweight (it
        // hands off to a kernel worker thread) — keep this method async
        // for caller symmetry, but the work itself is sync.
        return await Task.Run(() =>
        {
            lock (MountGate)
            {
                ct.ThrowIfCancellationRequested();
                if (_disposed) return MountResult.Failed("This mount has already been closed.");
                return MountCore(ct);
            }
        }, ct).ConfigureAwait(false);
    }

    private MountResult MountCore(CancellationToken ct)
    {
        try
        {
            var letter = FindFirstFreeDriveLetter();
            if (letter is null)
            {
                return MountResult.Failed(
                    "No free drive letters available. Free up a letter and try again.");
            }

            _fs = new WinFspSnapshotFs(_provider);
            _host = new FileSystemHost(_fs)
            {
                // FileSystemName shows up in Explorer's "Properties →
                // File system" line. "Snapshot" is friendlier than
                // "WinFsp.Net".
                FileSystemName = "Snapshot",
                SectorSize = 4096,
                SectorsPerAllocationUnit = 1,
                MaxComponentLength = 255,
                VolumeCreationTime = (ulong)DateTime.UtcNow.ToFileTimeUtc(),
                VolumeSerialNumber = unchecked((uint)DateTime.UtcNow.Ticks),
                // A snapshot never changes while mounted: Windows may keep
                // what it learnt about a file, a folder listing, a security
                // descriptor and file contents for as long as the drive is
                // mounted, instead of asking again every second (U158).
                FileInfoTimeout = uint.MaxValue,
                CaseSensitiveSearch = false,
                CasePreservedNames = true,
                UnicodeOnDisk = true,
                PersistentAcls = false,
                PostCleanupWhenModifiedOnly = true,
                FlushAndPurgeOnCleanup = false,
                VolumeInfoTimeout = uint.MaxValue,
                DirInfoTimeout = uint.MaxValue,
                SecurityTimeout = uint.MaxValue,
            };

            var mountPoint = letter + ":";
            var status = _host.Mount(mountPoint);
            if (status < 0)
            {
                _host.Dispose();
                _host = null;
                _fs = null;
                return MountResult.Failed(
                    $"WinFSP mount failed with NTSTATUS 0x{status:X8}. " +
                    "Check the Backspace log for the kernel driver error code.");
            }

            _mountedDriveLetter = mountPoint;

            // WinFsp.Net's Mount() returns success when the dispatcher
            // is set up + FspFileSystemSetMountPointEx succeeds, but
            // the actual OS drive-letter REGISTRATION can lag by tens
            // to hundreds of milliseconds (the WinFSP kernel driver
            // signals the SCM, which broadcasts WM_DEVICECHANGE; until
            // that propagates, DriveInfo / Explorer / shell don't see
            // the new volume). If we launch Explorer in that gap, the
            // user sees "Z: is unavailable. Location is not available."
            // Wait up to 5 s for the drive to become visible before
            // continuing.
            var driveLetter = mountPoint.TrimEnd(':');
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            bool driveVisible = false;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (LogicalDriveLetterExists(driveLetter))
                {
                    driveVisible = true;
                    break;
                }
                System.Threading.Thread.Sleep(100);
            }
            if (!driveVisible)
            {
                _log.Warning("WinFSP Mount reported success but {Letter} didn't become visible within 5s — Explorer would show 'Location not available'. Treating as failure and tearing down.", _mountedDriveLetter);
                try { _host?.Unmount(); } catch { }
                try { _host?.Dispose(); } catch { }
                _host = null;
                _fs = null;
                _mountedDriveLetter = null;
                return MountResult.Failed(
                    $"WinFSP reported {mountPoint} was mounted but the drive didn't appear in Windows. " +
                    "This usually means the WinFSP kernel driver couldn't claim the letter. " +
                    "Try a different letter or check whether another mount is holding it.");
            }

            // Pop Explorer at the mount root. Two pitfalls we have
            // to dodge:
            //
            //   1. Backspace runs ELEVATED. WinFSP's symbolic-link
            //      mount (\??\Z:) lives in the admin LSA session,
            //      invisible to medium-integrity processes. The
            //      user's existing shell Explorer can't see Z: at
            //      all — Test-Path Z:\ returns False from a normal
            //      shell while True from an elevated shell, with
            //      the same mount up.
            //
            //   2. Default Process.Start("explorer.exe", "Z:\\")
            //      routes through the shell's COM activation
            //      pathway, which forwards the open request to the
            //      pre-existing desktop Explorer instance (medium
            //      IL, no Z:). The user sees an instant "Z: is
            //      unavailable. Location is not available." popup.
            //
            // Fix: force-spawn a SEPARATE explorer.exe process via
            // /separate, with the absolute system32 path so the
            // dispatch can't be hijacked. The new process inherits
            // our elevated token, so it sees the WinFSP mount in
            // its own LSA session. The user gets a normal Explorer
            // window (with an admin-style "Administrator" prefix in
            // the title bar) browsing Z:\.
            if (_openExplorer) OpenExplorerAt(_mountedDriveLetter);

            _log.Information("Snapshot mounted at {Letter} (WinFSP)", _mountedDriveLetter);
            return MountResult.Ok(_mountedDriveLetter);
        }
        catch (DllNotFoundException ex)
        {
            _log.Error(ex, "WinFSP native runtime DLL not found");
            return MountResult.Failed(
                "WinFSP runtime is missing or corrupted. Reinstall from " +
                $"{WinFspRuntimeDetector.InstallUrl} and try again.");
        }
        catch (Exception ex)
        {
            try { _host?.Unmount(); _host?.Dispose(); } catch { }
            _host = null;
            _mountedDriveLetter = null;
            _log.Error(ex, "WinFSP mount failed");
            return MountResult.Failed("Mount failed: " + ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_host is not null)
        {
            try
            {
                _host.Unmount();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "WinFSP Unmount on {Letter} threw", _mountedDriveLetter);
            }
            try { _host.Dispose(); }
            catch (Exception ex) { _log.Warning(ex, "WinFSP host dispose threw"); }
            _host = null;
        }
        _mountedDriveLetter = null;

        // Dispose the tree provider — EngineBackedSnapshotTreeProvider
        // holds SemaphoreSlims that the GC won't reclaim cleanly on
        // its own.
        if (_provider is IDisposable disposable)
        {
            try { disposable.Dispose(); }
            catch (Exception ex) { _log.Warning(ex, "Snapshot tree provider dispose threw"); }
        }

        await Task.CompletedTask;
    }

    /// <summary>Opens a separate Explorer window at <paramref name="drive"/>
    /// (see the reasons above MountCore's call).</summary>
    internal static void OpenExplorerAt(string drive)
    {
        try
        {
            var system32Explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = File.Exists(system32Explorer) ? system32Explorer : "explorer.exe",
                Arguments = "/separate," + drive.TrimEnd('\\') + "\\",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Explorer launch failed; mount at {Letter}", drive);
        }
    }

    /// <summary>Walk Z: → D: looking for the first letter not in the
    /// system's drive map. Returns null if nothing is free (rare on
    /// a desktop; possible on a server with mapped network drives
    /// claiming most of the alphabet).</summary>
    internal static string? FindFirstFreeDriveLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => d.Name[0]).ToHashSet();
        for (char letter = 'Z'; letter >= 'D'; letter--)
        {
            if (!used.Contains(letter)) return letter.ToString();
        }
        return null;
    }

    /// <summary>True iff <paramref name="letter"/> (a single uppercase
    /// drive letter without colon) is currently a logical drive on
    /// this host AND actually ready for I/O. DriveInfo.GetDrives()
    /// alone is NOT enough: WinFSP can register the drive letter
    /// briefly during dispatcher startup even when the volume
    /// immediately tears down — Explorer trying to open the path
    /// in that window gets "Location not available". This check
    /// requires both the letter to exist AND a Directory.Exists
    /// probe to confirm the volume actually accepts I/O.</summary>
    private static bool LogicalDriveLetterExists(string letter)
    {
        try
        {
            var rootPath = letter + ":\\";
            // Directory.Exists on a drive root is the cleanest "is
            // this drive open for business?" check — it issues a
            // FindFirstFile against the root which the kernel
            // routes through WinFSP's dispatcher; if the
            // dispatcher is alive AND our WinFspSnapshotFs returns
            // success for the root stat, this returns true.
            return Directory.Exists(rootPath);
        }
        catch { /* polling — never throw */ }
        return false;
    }
}
