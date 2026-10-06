using System;
using System.IO;
using System.Runtime.Versioning;

namespace Backspace.Services;

/// <summary>
/// Windows placeholder inspection. This does not read file contents.
/// Restic's --exclude-cloud-files asks the engine to omit supported
/// online-only files; omitted content cannot be restored from that backup.
/// Without that flag, opening a supported placeholder can download it.
/// Provider behavior varies: a real Dropbox placeholder tested with the
/// bundled Restic 0.18.1 was rejected as an unsupported irregular file,
/// with no content saved and no local download, with either flag setting.
/// Do not promise hydration or automatic removal of downloaded content.
/// </summary>
public static class CloudFileBehavior
{
    /// <summary>Win32 file attribute set by cloud provider filter
    /// drivers on online-only / placeholder files. Constant value
    /// from winnt.h.</summary>
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>True when the file is an online-only placeholder
    /// (OneDrive Files-On-Demand, Dropbox cloud-only, iCloud
    /// optimised). False for ordinary local files. Returns false on
    /// non-Windows hosts (the attribute is Windows-specific).</summary>
    public static bool IsCloudOnly(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (string.IsNullOrEmpty(path)) return false;
        return HasRecallAttribute(path);
    }

    [SupportedOSPlatform("windows")]
    private static bool HasRecallAttribute(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & RecallOnDataAccess) != 0 || (attrs & (FileAttributes.Offline | FileAttributes.ReparsePoint)) == (FileAttributes.Offline | FileAttributes.ReparsePoint);
        }
        catch (OperationCanceledException)
        {
            // Cancellation propagates: a caller that arms a CT and
            // walks a directory must not see the predicate silently
            // swallow the cancel signal.
            throw;
        }
        catch
        {
            // File missing / access denied — treat as "not cloud-only"
            // so callers don't false-positive on transient I/O errors.
            return false;
        }
    }

    /// <summary>Human-readable explanation for the GUI's tooltip /
    /// docs. Wraps the answer above in one sentence each side of the
    /// flag so users see what they're signing up for.</summary>
    public static string ExplainBackupBehavior(bool excludeCloudFiles) => excludeCloudFiles
        ? "Supported cloud-only files are skipped and cannot be recovered from this backup. Some providers use placeholders Restic cannot read; check the run for warnings."
        : "Cloud-only files may download during backup and use local disk space afterward. Some placeholders cannot be read by Restic and will not be backed up. Check warnings; Backspace does not automatically free downloaded files.";
}
