using System;
using System.IO;
using Backspace.Models;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// Backups to an external drive run in the background service (U061, 1
/// November 2026): with Windows snapshots and before anyone signs in, like
/// the other backups. While the drive is unplugged the run is skipped, as in
/// the tray app. Network shares are connected with their saved credentials
/// (ServiceNetworkShares, U071).
/// </summary>
internal static class ServiceExternalDrives
{
    /// <summary>The backup folder of a connected external drive destination,
    /// or null while the expected drive (letter and label) is not connected.</summary>
    internal static string? ConnectedPath(Destination destination)
    {
        if (destination.Kind != DestinationKind.ExternalDrive || string.IsNullOrWhiteSpace(destination.PathOrSubpath)) return null;
        var check = VolumeDetector.Check(destination.ExpectedDriveLetter, destination.ExpectedVolumeLabel);
        if (check.Verdict != VolumeDetector.Verdict.Mounted) return null;
        var path = string.IsNullOrWhiteSpace(destination.ExpectedDriveLetter) && check.MountedRoot is { } root
            ? ResticUriResolver.RebaseDrivePath(destination.PathOrSubpath, root)
            : destination.PathOrSubpath;
        return ServiceInstallationSecurity.CanonicalLocalPath(path);
    }

    /// <summary>Whether this process can create and delete a file in the folder.</summary>
    internal static bool CanWrite(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return false;
            var probe = Path.Combine(folder, ".backspace-access-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return false; }
    }
}
