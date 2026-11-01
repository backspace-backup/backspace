using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Backspace.Models;
using Microsoft.Win32.TaskScheduler;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// Backups to an external drive run in the background service (U061, 1
/// November 2026): with Windows snapshots and before anyone signs in, like
/// the other backups. While the drive is unplugged the run is skipped, as in
/// the tray app. When the drive is plugged in and the service cannot write
/// its backup folder yet (a drive formatted elsewhere, or a folder made with
/// the owner's permissions only), the service starts the "Apply saved
/// settings" task, which as SYSTEM creates the folder and grants access.
/// Network shares stay with the tray app: the service's account cannot sign
/// in to a share.
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

    /// <summary>Connected external backup folders of the backups in this configuration.</summary>
    internal static IEnumerable<string> ConnectedRoots(AppConfig config)
    {
        var used = config.Backups.SelectMany(b => b.Targets).Select(t => t.DestinationId).ToHashSet(StringComparer.Ordinal);
        return config.Destinations.Where(d => used.Contains(d.Id))
            .Select(ConnectedPath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// Run by the service's worker before a backup: for each connected external
    /// destination it cannot write, ask the settings task to prepare it and wait
    /// up to <paramref name="timeout"/>. Returns the folders still not writable.
    /// </summary>
    internal static IReadOnlyList<string> EnsureAccess(ServiceWorkerBinding binding, Backup backup, AppConfig config,
        TimeSpan timeout, CancellationToken cancellation, Func<ServiceWorkerBinding, bool>? startSettingsTask = null)
    {
        var folders = backup.Targets
            .Select(t => config.Destinations.FirstOrDefault(d => d.Id == t.DestinationId))
            .OfType<Destination>().Select(ConnectedPath).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var blocked = folders.Where(f => !CanWrite(f)).ToList();
        if (blocked.Count == 0) return blocked;
        AppLogger.For(nameof(ServiceExternalDrives)).Information("Asking the settings task to prepare {Count} external backup folder(s)", blocked.Count);
        if (!(startSettingsTask ?? StartSettingsTask)(binding)) return blocked;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && blocked.Count > 0)
        {
            cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));
            cancellation.ThrowIfCancellationRequested();
            blocked = blocked.Where(f => !CanWrite(f)).ToList();
        }
        return blocked;
    }

    private static bool StartSettingsTask(ServiceWorkerBinding binding)
    {
        try
        {
            using var scheduler = new TaskService();
            using var task = scheduler.GetTask(ServiceScheduleRegistration.Folder(binding) + "\\" + ServiceSettingsTask.Name);
            if (task is null || !task.Enabled) return false;
            task.Run();
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException or IOException or InvalidOperationException)
        {
            AppLogger.For(nameof(ServiceExternalDrives)).Warning(ex, "The service could not start its settings task");
            return false;
        }
    }
}
