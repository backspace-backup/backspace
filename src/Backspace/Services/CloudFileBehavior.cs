using System;
using System.IO;
using System.Runtime.Versioning;

namespace Backspace.Services;

/// <summary>
/// Inspection helpers + documented answer to the question "what
/// happens when restic backs up a OneDrive / Dropbox / iCloud
/// online-only placeholder file?"
///
/// Empirically-established behavior (Windows; tested against restic
/// 0.18, OneDrive Files-On-Demand, and Dropbox Smart Sync):
///
/// 1. The cloud provider's filter driver marks online-only files
///    with <c>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS</c> (0x00400000).
///    The file appears in the directory with its full size and
///    metadata; the bytes live in the cloud.
/// 2. <b>With <c>--exclude-cloud-files</c></b> (Backspace's default):
///    restic checks the attribute, skips the file, and emits it in
///    the snapshot with a "cloud-only" marker. Restoring later does
///    NOT round-trip the content — there was nothing to back up.
///    This is the right default for a local-drive backup.
/// 3. <b>Without <c>--exclude-cloud-files</c></b>: restic opens the
///    file and reads it. The filter driver intercepts the read and
///    pulls the bytes from the cloud, which materialises the file
///    on local disk. <b>That state persists</b> — the cloud client
///    does not re-virtualise the file after the read completes.
///    A backup that touched 200 GB of online-only OneDrive files
///    means the user now has 200 GB of newly-materialised local
///    files until they manually right-click → "Free up space" on
///    each one.
///
/// Consequence for the "mirror my Dropbox to a different destination
/// without cluttering local disk" workflow: <c>--exclude-cloud-files=false</c>
/// is NOT the right tool. The correct shape is either (a) point
/// Backspace at an rclone remote so the bytes flow cloud→restic
/// without ever touching the local disk, or (b) accept the local
/// materialisation and use the cloud provider's API to set the
/// affected files back to "online-only" after the run. Backspace
/// doesn't currently automate (b); the rclone-remote path (a) lands
/// once the destination editor supports cloud-source backups
/// (separate feature).
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
            return (attrs & RecallOnDataAccess) != 0;
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
        ? "Cloud-only files (OneDrive / Dropbox / iCloud) are skipped. Restoring later won't re-create them, but your backups stay small and your local disk stays free."
        : "Cloud-only files are downloaded on the fly during backup. The cloud provider keeps the local copy after the read, so your local disk fills up with whatever you backed up — you'll need to manually free space afterward.";
}
