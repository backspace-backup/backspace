using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services.WinFsp;
using Serilog;

namespace Backspace.Services.WebDav;

/// <summary>
/// Process-wide registry of live <see cref="WinFspMountCoordinator"/>
/// instances. The "Mount in Explorer" command instantiates one per
/// backup and stashes it here so:
///   • The coordinator (and its drive-letter lease) outlives the
///     command's task, since net use is a process-global mapping.
///   • <see cref="DisposeAllAsync"/> can clean every mount on app
///     shutdown — otherwise a forced quit leaves orphan WebDAV
///     drive letters until the user reboots.
///   • A future "Unmount" / "Browse" command can look up the live
///     coordinator by backup id rather than re-mounting.
///
/// Windows-only; the entire WebDAV mount surface compiles out on
/// macOS / Linux via the csproj's Compile Remove.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MountedSnapshotRegistry : ISnapshotMountRegistry
{
    private static ILogger _log => AppLogger.For<MountedSnapshotRegistry>();
    private readonly ConcurrentDictionary<string, WinFspMountCoordinator> _byBackupId = new();

    /// <summary>
    /// Pre-install consent callback. Invoked the first time a Mount
    /// runs without the WinFSP runtime installed. The UI wires a
    /// "Backspace needs to install the WinFSP filesystem driver
    /// (one-time, prompts for Yes)" confirmation dialog here so the
    /// user has context before the UAC prompt appears.
    ///
    /// Returns true → registry proceeds to <see cref="WinFspInstaller.InstallAsync"/>.
    /// Returns false → registry surfaces "Install cancelled" and the
    /// user clicks Mount again whenever they're ready.
    /// Null (the default) → registry auto-consents and goes straight
    /// to UAC. Tests and headless runs use this path.
    /// </summary>
    public Func<Task<bool>>? RequestInstallConsent { get; set; }

    /// <summary>Mount the latest revision of <paramref name="backup"/>
    /// against its first (source × destination × storage) plan,
    /// pop Explorer at the drive letter, and remember the live
    /// coordinator under the backup id so subsequent calls reuse
    /// it instead of fighting for a second drive letter.</summary>
    public async Task<MountResult> MountForAsync(Backup backup, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return MountResult.Failed("Snapshot mount is Windows-only.");
        if (backup is null)
            return MountResult.Failed("No backup selected.");

        // Already mounted? Only reuse the existing coordinator if
        // the drive letter ACTUALLY EXISTS on this host. Otherwise
        // the kernel-side mount has been torn down out from under
        // us (WinFSP service restart, sleep/resume, external
        // `net use /delete`, etc.) — clicking Mount in that state
        // would just re-open Explorer at a dead letter and silently
        // fail (no error, no remount), exactly what the user just
        // hit. Evict the stale entry and fall through to the fresh-
        // mount path.
        if (_byBackupId.TryGetValue(backup.Id, out var existing) && existing.DriveLetter is not null)
        {
            if (DriveLetterExists(existing.DriveLetter))
            {
                try
                {
                    using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = existing.DriveLetter + "\\",
                        UseShellExecute = true,
                    });
                }
                catch (Exception ex) { _log.Warning(ex, "Re-open Explorer failed for {Letter}", existing.DriveLetter); }
                return MountResult.Ok(existing.DriveLetter);
            }
            // Stale entry — drive letter is gone from the OS. Dispose
            // the dead coord and continue to the fresh-mount path.
            _log.Information("Mount registry entry for {Backup} points at {Letter} but the drive is gone; remounting", backup.Name, existing.DriveLetter);
            if (_byBackupId.TryRemove(backup.Id, out var stale))
            {
                try { await stale.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _log.Warning(ex, "Stale-mount dispose threw for {Backup}", backup.Name); }
            }
        }

        // Resolve the plan we'll mount. Same first-target convention
        // RevisionBrowser + RestoreEngine use — multiple targets stay
        // out of scope here because Explorer can only mount one drive
        // letter per backup. Multiple SOURCES *are* in scope — the
        // mount tree exposes one folder per source × one subfolder
        // per revision (handled inside BackupMountTreeProvider).
        if (backup.SourcePaths.Count == 0 || backup.Targets.Count == 0)
            return MountResult.Failed("Add a source folder and destination before mounting.");
        var target = backup.Targets[0];
        var destination = ServiceLocator.Config.Current.Destinations
            .FirstOrDefault(d => d.Id == target.DestinationId);
        if (destination is null)
            return MountResult.Failed("This backup's first destination is missing from config.");

        // Sanity check: there has to be AT LEAST one snapshot
        // somewhere in this backup, or the mount root would be empty
        // and the user would assume the mount itself failed. We probe
        // the FIRST source's snapshot list; if it has zero revisions
        // AND every other source also has zero, the mount would be
        // useless and we surface that upfront. (Probing every source
        // here would just slow down the common case where the user
        // backs everything up at once.)
        var firstSource = backup.SourcePaths[0];
        var revisions = await ServiceLocator.Revisions
            .ListRevisionsAsync(backup, firstSource, destination, target.StorageName, ct)
            .ConfigureAwait(false);
        if (revisions.Count == 0)
            return MountResult.Failed(
                "No snapshots yet — run a backup first, then come back to mount it.");

        // WinFSP runtime check + auto-install on first Mount click.
        // We do this BEFORE building the provider / coordinator so a
        // missing runtime doesn't waste a snapshot listing or leak a
        // half-built tree provider. The MSI ships next to Backspace.exe
        // (FetchWinFsp.targets + a Content item in the csproj); the
        // installer service spawns msiexec with UAC elevation.
        if (!WinFspRuntimeDetector.IsInstalled())
        {
            if (RequestInstallConsent is { } prompt)
            {
                bool ok;
                try { ok = await prompt().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _log.Warning(ex, "RequestInstallConsent threw — treating as declined");
                    ok = false;
                }
                if (!ok)
                    return MountResult.Failed(
                        "Mount needs Windows' WinFSP filesystem driver to work. " +
                        "Click Mount again whenever you're ready and accept the install prompt.");
            }

            var installOutcome = await WinFspInstaller.InstallAsync(ct).ConfigureAwait(false);
            if (!installOutcome.Succeeded)
                return MountResult.Failed(installOutcome.Message);
            _log.Information("WinFSP runtime installed on first Mount click");
        }

        // Bring the WinFSP bin folder onto the process DLL search
        // path. This is a hard prerequisite for the WinFsp.Net
        // managed wrapper's static ctor to find winfsp-x64.dll
        // (its installer doesn't add itself to PATH). Idempotent —
        // a no-op when already registered, returns true when the
        // search path now contains the WinFSP bin dir. Without
        // this the next line throws "The type initializer for
        // 'Fsp.Interop.Api' threw an exception" at the moment we
        // touch any Fsp.* type.
        if (!WinFspNativeLoader.EnsureRegistered())
        {
            return MountResult.Failed(
                "WinFSP looks installed but Backspace couldn't add its bin folder to the DLL " +
                "search path. Try restarting Backspace; if that doesn't help, reinstall WinFSP from " +
                $"{WinFspRuntimeDetector.InstallUrl}.");
        }

        // One cache root per (backup, destination). Inside it the
        // composite provider partitions per (sourceFolder, revision)
        // sub-cache so two mount sessions for the same backup share
        // already-restored files. Drop the "-rN" suffix the old
        // single-revision mount used — the composite holds many.
        var cacheDir = System.IO.Path.Combine(AppPaths.BackrestDataDir, "mount-cache", backup.Id);
        var provider = new BackupMountTreeProvider(
            ServiceLocator.Engine, backup, destination, target.StorageName, cacheDir);

        var coord = new WinFspMountCoordinator(provider);
        var result = await coord.MountAsync(ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            // Clean up the half-built coordinator so we don't leak
            // a WebDAV server / drive-letter intent.
            await coord.DisposeAsync().ConfigureAwait(false);
            return result;
        }

        // Race-safe insert: if a parallel MountForAsync for the same
        // backup beat us to it, our freshly-mounted drive letter is
        // now redundant. Tear it down rather than leaking the WebDAV
        // server + drive-letter mapping; the caller still gets a
        // success result that points at the winner's drive letter.
        if (!_byBackupId.TryAdd(backup.Id, coord))
        {
            await coord.DisposeAsync().ConfigureAwait(false);
            return _byBackupId.TryGetValue(backup.Id, out var winner) && winner.DriveLetter is not null
                ? MountResult.Ok(winner.DriveLetter)
                : result;
        }
        return result;
    }

    /// <summary>True iff <paramref name="driveLetter"/> (e.g. "Z:")
    /// is currently a real logical drive on this host. Used as the
    /// liveness check before reusing a coordinator from
    /// <see cref="_byBackupId"/> — if the kernel-side mount has
    /// been torn down, the coordinator's stored letter no longer
    /// resolves and we need to remount.</summary>
    private static bool DriveLetterExists(string driveLetter)
    {
        try
        {
            // DriveInfo.GetDrives() enumerates the OS's live drive
            // letters; an unmounted Z: will not appear. The Name
            // comes back as "Z:\" (trailing slash), so we compare
            // letter-only.
            var letter = driveLetter.TrimEnd(':', '\\');
            foreach (var d in System.IO.DriveInfo.GetDrives())
            {
                if (d.Name.Length > 0 && d.Name[0].ToString().Equals(letter, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { /* better to remount than crash */ }
        return false;
    }

    /// <summary>Tear down every active mount. Wired into
    /// <see cref="App"/>'s shutdown handler so leaving Backspace
    /// running for hours doesn't leave orphan drive letters
    /// pointing at a stopped WebDAV server.</summary>
    public async Task DisposeAllAsync()
    {
        var copies = _byBackupId.ToArray();
        _byBackupId.Clear();
        foreach (var kv in copies)
        {
            try { await kv.Value.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _log.Warning(ex, "Mount dispose threw for {Backup}", kv.Key); }
        }
    }
}
