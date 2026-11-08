using System;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;

namespace Backspace.Services.WebDav;

/// <summary>
/// Cross-platform shape for the WebDAV snapshot-mount surface.
/// Defined here (in a file that compiles on every TFM) so
/// <see cref="ServiceLocator.SnapshotMounts"/> can be typed as
/// <c>ISnapshotMountRegistry?</c> regardless of whether the
/// concrete <see cref="MountedSnapshotRegistry"/> is present.
///
/// macOS / Linux builds leave the property null because no mount
/// implementation exists yet on those platforms; the UI binds
/// IsVisible to a per-card flag so the "Mount" button just isn't
/// rendered.
/// </summary>
public interface ISnapshotMountRegistry
{
    event Action<string>? Changed;
    bool HasPendingOperations { get; }
    void CancelPending();
    string? GetMountedDrive(string backupId);
    /// <summary>What a mount in progress is doing, in words ("Reading the
    /// backup's folders: 120,000 so far"); null when none is (U158).
    /// <see cref="Changed"/> fires when it changes.</summary>
    string? GetMountProgress(string backupId) => null;
    /// <summary>Opens Explorer at a mounted snapshot's drive.</summary>
    void OpenInExplorer(string backupId) { }
    Task UnmountAsync(string backupId, CancellationToken ct = default);
    /// <summary>Mount the latest snapshot of <paramref name="backup"/>'s
    /// first plan as a system-visible drive / mount, opening a
    /// file-browser at the result. Idempotent for the same backup id.</summary>
    Task<MountResult> MountForAsync(Backup backup, CancellationToken ct = default);

    /// <summary>Mount the revisions the person chose (U162): only their trees
    /// are read, all before the drive appears.</summary>
    Task<MountResult> MountForAsync(Backup backup, MountSelection selection, CancellationToken ct = default)
        => MountForAsync(backup, ct);

    /// <summary>What a mounted drive shows, for the card ("7 Nov 2026 14:30",
    /// "3 revisions"); null when not mounted or unknown.</summary>
    string? GetMountedDescription(string backupId) => null;

    /// <summary>Tear down every mount this registry owns. Wired
    /// into app shutdown so leaving Backspace running for hours
    /// doesn't leave orphan drive letters / mountpoints.</summary>
    Task DisposeAllAsync();
}

/// <summary>Outcome of a mount attempt. <c>DriveLetter</c> is
/// non-null iff <c>Succeeded</c> is true. Lives in the
/// cross-platform layer so the result type is reachable on every
/// TFM (the Windows-only coordinator's implementation builds on it
/// without re-declaring).</summary>
public readonly record struct MountResult(bool Succeeded, string? DriveLetter, string Message)
{
    public static MountResult Ok(string driveLetter)
        => new(true, driveLetter, $"Mounted at {driveLetter}");

    public static MountResult Failed(string reason)
        => new(false, null, reason);
}
