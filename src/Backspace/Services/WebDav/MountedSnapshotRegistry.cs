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
/// Process-wide registry of mounted snapshots, one drive per backup.
///   • The mount (its drive letter and its read of the snapshot) lives here,
///     not in the command that started it.
///   • <see cref="DisposeAllAsync"/> unmounts everything when Backspace exits.
///     The drive is served by this process: if it ends any other way (a crash,
///     Task Manager), WinFsp removes the drive with it.
///   • Each mount holds its destination as a snapshot reader
///     (<see cref="SnapshotReaders"/>) until it is unmounted, so no cleanup,
///     snapshot removal or password change, in this process or another,
///     rewrites what the drive shows (U159).
///   • While a mount is being prepared, <see cref="GetMountProgress"/> says
///     what it is doing, for the card (U158).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MountedSnapshotRegistry : ISnapshotMountRegistry
{
    private static ILogger _log => AppLogger.For<MountedSnapshotRegistry>();

    private sealed record Mounted(WinFspMountCoordinator Coordinator, IDisposable Readers, string Description) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await Coordinator.DisposeAsync().ConfigureAwait(false); }
            finally { Readers.Dispose(); }
        }
    }

    private readonly ConcurrentDictionary<string, Mounted> _byBackupId = new();
    private readonly ConcurrentDictionary<string, string> _progress = new();
    private readonly ConcurrentDictionary<string, int> _percent = new();
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _cancelGate = new();
    private CancellationTokenSource _pendingCancellation = new();
    private int _pendingCount;
    public bool HasPendingOperations => Volatile.Read(ref _pendingCount) != 0;
    public void CancelPending()
    {
        CancellationTokenSource prior;
        lock (_cancelGate) { prior = _pendingCancellation; _pendingCancellation = new(); }
        prior.Cancel();
        prior.Dispose();
    }
    public event Action<string>? Changed;

    /// <summary>Opens the mount's Explorer window, or not (tests).</summary>
    public bool OpenExplorer { get; set; } = true;

    public string? GetMountedDrive(string backupId) =>
        _byBackupId.TryGetValue(backupId, out var mount) && mount.Coordinator.DriveLetter is { } letter
        && DriveLetterExists(letter) ? letter : null;

    public string? GetMountProgress(string backupId) => _progress.TryGetValue(backupId, out var text) ? text : null;

    public int? GetMountPercent(string backupId) => _percent.TryGetValue(backupId, out var percent) ? percent : null;

    public string? GetMountedDescription(string backupId) =>
        GetMountedDrive(backupId) is not null && _byBackupId.TryGetValue(backupId, out var mount) ? mount.Description : null;

    public void OpenInExplorer(string backupId)
    {
        if (GetMountedDrive(backupId) is { } drive) WinFspMountCoordinator.OpenExplorerAt(drive);
    }

    public async Task UnmountAsync(string backupId, CancellationToken ct = default)
    {
        await _mutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_byBackupId.TryRemove(backupId, out var mount))
                await mount.DisposeAsync().ConfigureAwait(false);
        }
        finally { _mutationGate.Release(); Changed?.Invoke(backupId); }
    }

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

    /// <summary>Mount the latest revision of every source of
    /// <paramref name="backup"/> (what the picker pre-selects).</summary>
    public Task<MountResult> MountForAsync(Backup backup, CancellationToken ct = default)
        => MountAsync(backup, null, ct);

    /// <summary>Mount the chosen revisions of <paramref name="backup"/> at its
    /// first destination, pop Explorer at the drive letter, and remember the
    /// live mount under the backup id so subsequent calls reuse it instead of
    /// fighting for a second drive letter (U162).</summary>
    public Task<MountResult> MountForAsync(Backup backup, MountSelection selection, CancellationToken ct = default)
        => MountAsync(backup, selection, ct);

    private async Task<MountResult> MountAsync(Backup backup, MountSelection? selection, CancellationToken ct)
    {
        if (backup is null) return MountResult.Failed("No backup selected.");
        CancellationTokenSource pending;
        lock (_cancelGate)
            pending = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token, _pendingCancellation.Token);
        using var linked = pending;
        Interlocked.Increment(ref _pendingCount);
        Report(backup.Id, "Starting");
        try
        {
            await _mutationGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try { return await MountCoreAsync(backup, selection, linked.Token).ConfigureAwait(false); }
            finally { _mutationGate.Release(); }
        }
        finally
        {
            Interlocked.Decrement(ref _pendingCount);
            _progress.TryRemove(backup.Id, out _);
            _percent.TryRemove(backup.Id, out _);
            Changed?.Invoke(backup.Id);
        }
    }

    private void Report(string backupId, string text, int? percent = null)
    {
        var samePercent = _percent.TryGetValue(backupId, out var was) ? percent == was : percent is null;
        if (_progress.TryGetValue(backupId, out var shown) && shown == text && samePercent) return;
        if (percent is { } p) _percent[backupId] = p; else _percent.TryRemove(backupId, out _);
        _progress[backupId] = text;
        Changed?.Invoke(backupId);
    }

    private void Report(string backupId, MountReading reading) => Report(backupId, ReadingText(reading), reading.Percent);

    /// <summary>"Reading the backup's folders: 120,000 files so far".</summary>
    internal static string ReadingText(int files) => ReadingText(new MountReading(files));

    /// <summary>"Reading the backup's folders: 41%, 120,000 files so far" for
    /// one revision; with several, which one: "Reading the backup's folders,
    /// revision 2 of 3 (5 Nov 2026 09:12): 41%, 120,000 files so far". The
    /// percentage is the whole mount's (U166), left out when unknown.</summary>
    internal static string ReadingText(MountReading reading)
    {
        var which = reading.Count > 1
            ? $", revision {reading.Index} of {reading.Count}" + (reading.Revision is { } r ? $" ({r})" : "")
            : "";
        var parts = new System.Collections.Generic.List<string>(2);
        if (reading.Percent is { } percent) parts.Add($"{percent}%");
        if (reading.Files > 0) parts.Add($"{reading.Files:N0} files so far");
        return "Reading the backup's folders" + which + (parts.Count > 0 ? ": " + string.Join(", ", parts) : "");
    }

    private async Task<MountResult> MountCoreAsync(Backup backup, MountSelection? selection, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return MountResult.Failed("Snapshot mount is Windows-only.");

        // Already mounted? Only reuse the existing mount if the drive letter
        // ACTUALLY EXISTS on this host. Otherwise the kernel-side mount has
        // been torn down out from under us (WinFSP service restart,
        // sleep/resume) — evict the stale entry and mount again.
        if (_byBackupId.TryGetValue(backup.Id, out var existing) && existing.Coordinator.DriveLetter is not null)
        {
            if (DriveLetterExists(existing.Coordinator.DriveLetter))
            {
                if (OpenExplorer) WinFspMountCoordinator.OpenExplorerAt(existing.Coordinator.DriveLetter);
                return MountResult.Ok(existing.Coordinator.DriveLetter);
            }
            _log.Information("Mount registry entry for {Backup} points at {Letter} but the drive is gone; remounting", backup.Name, existing.Coordinator.DriveLetter);
            if (_byBackupId.TryRemove(backup.Id, out var stale))
            {
                try { await stale.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _log.Warning(ex, "Stale-mount dispose threw for {Backup}", backup.Name); }
            }
        }

        // The first target, as Restore and Browse use. With several sources,
        // each has its own folder in the drive (BackupMountTreeProvider).
        if (backup.SourcePaths.Count == 0 || backup.Targets.Count == 0)
            return MountResult.Failed("Add a source folder and destination before mounting.");
        var target = backup.Targets[0];
        var destination = ServiceLocator.Config.Current.Destinations
            .FirstOrDefault(d => d.Id == target.DestinationId);
        if (destination is null)
            return MountResult.Failed("This backup's first destination is missing from config.");

        // WinFSP runtime check + auto-install on first Mount click, before
        // reading anything from the destination.
        if (!WinFspRuntimeDetector.IsInstalled())
        {
            Report(backup.Id, "Installing the WinFsp driver");
            if (RequestInstallConsent is { } prompt)
            {
                bool ok;
                try { ok = await prompt().WaitAsync(ct).ConfigureAwait(false); }
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

        // The WinFsp.Net wrapper's static ctor needs winfsp-x64.dll on the
        // DLL search path; its installer doesn't add itself to PATH.
        if (!WinFspNativeLoader.EnsureRegistered())
        {
            return MountResult.Failed(
                "WinFSP looks installed but Backspace couldn't add its bin folder to the DLL " +
                "search path. Try restarting Backspace; if that doesn't help, reinstall WinFSP from " +
                $"{WinFspRuntimeDetector.InstallUrl}.");
        }

        // Hold the destination for as long as the drive shows it (U159).
        // Waits while a cleanup there is running.
        var readers = await SnapshotReaders.HoldAsync(destination, target.StorageName, SnapshotReaders.Kind.Mount,
            waiting => { if (waiting.Length > 0) Report(backup.Id, waiting); }, ct).ConfigureAwait(false);

        // Nobody chose (tests, callers without the picker): the latest
        // revision of every source, as the picker pre-selects.
        if (selection is null)
        {
            Report(backup.Id, "Reading the backup's revisions");
            try { selection = await MountSelection.LatestAsync(backup, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { readers.Dispose(); throw; }
            catch (Exception ex)
            {
                readers.Dispose();
                _log.Error(ex, "Listing the revisions to mount failed for {Backup}", backup.Name);
                return MountResult.Failed(
                    "Couldn't read the backup's revisions from the destination. " +
                    "Check the destination is reachable and try again. Underlying error: " + ex.Message);
            }
        }
        // Only this backup's own sources.
        selection = new MountSelection(selection.Sources
            .Where(s => backup.SourcePaths.Contains(s.Path, StringComparer.Ordinal) && s.Revisions.Count > 0).ToList());
        Report(backup.Id, new MountReading(0, 1, selection.RevisionCount));

        // The composite provider separates destination/source/snapshot IDs
        // below this root; retention changing a display number cannot reuse
        // another snapshot's cached bytes.
        WinFspMountCoordinator coord;
        try
        {
            var cacheDir = System.IO.Path.Combine(AppPaths.BackrestDataDir, "mount-cache", backup.Id);
            coord = new WinFspMountCoordinator(new BackupMountTreeProvider(
                ServiceLocator.Engine, backup, destination, target.StorageName, cacheDir, selection), OpenExplorer);
        }
        catch { readers.Dispose(); throw; }
        var mount = new Mounted(coord, readers, selection.Describe());
        MountResult result;
        try
        {
            result = await coord.MountAsync(ct, new InlineProgress(reading => Report(backup.Id, reading)))
                .ConfigureAwait(false);
        }
        catch
        {
            await mount.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        if (!result.Succeeded)
        {
            await mount.DisposeAsync().ConfigureAwait(false);
            return result;
        }
        if (!_byBackupId.TryAdd(backup.Id, mount))
        {
            await mount.DisposeAsync().ConfigureAwait(false);
            return _byBackupId.TryGetValue(backup.Id, out var winner) && winner.Coordinator.DriveLetter is not null
                ? MountResult.Ok(winner.Coordinator.DriveLetter)
                : result;
        }
        return result;
    }

    /// <summary>Reports on the reporting thread, unlike <see cref="Progress{T}"/>,
    /// which posts to whatever context created it.</summary>
    private sealed class InlineProgress(Action<MountReading> report) : IProgress<MountReading>
    {
        public void Report(MountReading value) => report(value);
    }

    /// <summary>True iff <paramref name="driveLetter"/> (e.g. "Z:")
    /// is currently a real logical drive on this host.</summary>
    private static bool DriveLetterExists(string driveLetter)
    {
        try
        {
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

    /// <summary>Unmount every snapshot. Called when Backspace exits (Quit,
    /// Windows signing out or shutting down, an unhandled error), so no drive
    /// outlives the app that serves it.</summary>
    public async Task DisposeAllAsync()
    {
        _shutdown.Cancel();
        await _mutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var copies = _byBackupId.ToArray();
            _byBackupId.Clear();
            foreach (var kv in copies)
            {
                try { await kv.Value.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _log.Warning(ex, "Mount dispose threw for {Backup}", kv.Key); }
                Changed?.Invoke(kv.Key);
            }
        }
        finally { _mutationGate.Release(); }
    }
}
