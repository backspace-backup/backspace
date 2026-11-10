using System;
using System.Diagnostics;
using System.IO;

namespace Backspace.Services;

/// <summary>Opens a folder in the system file manager (Explorer on Windows).
/// A folder that no longer exists is not opened: the shell would otherwise
/// show its own error for a temporary folder that was just removed.</summary>
public static class FolderLauncher
{
    /// <summary>Test seam: replaces the shell launch. Returns the folder opened.</summary>
    internal static Action<string>? LaunchOverride { get; set; }

    public static bool Open(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
            if (LaunchOverride is { } launch) { launch(path); return true; }
            using var _ = Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.For(nameof(FolderLauncher)).Warning(ex, "Open folder failed for {Path}", path);
            return false;
        }
    }

    /// <summary>Opens a file in the app the system uses for it (a run's log
    /// file in Notepad, U176). A file that no longer exists is not opened.</summary>
    public static bool OpenFile(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            if (LaunchOverride is { } launch) { launch(path); return true; }
            using var _ = Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.For(nameof(FolderLauncher)).Warning(ex, "Open file failed for {Path}", path);
            return false;
        }
    }
}
