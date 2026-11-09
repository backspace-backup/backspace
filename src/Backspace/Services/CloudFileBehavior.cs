using System;
using System.IO;
using System.Runtime.Versioning;

namespace Backspace.Services;

/// <summary>
/// Windows online-only files (U168, 9 November 2026). Detection uses the
/// attributes of a directory listing and never opens a file's data, so it
/// downloads nothing. Backspace's Windows restic (patch 0003 in
/// engine/restic/patches) skips the same files with --exclude-cloud-files,
/// quietly; official restic checked RECALL_ON_DATA_ACCESS only, and reported
/// each Dropbox online-only file (0x1620) as an "irregular" file error.
/// </summary>
public static class CloudFileBehavior
{
    /// <summary>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS (winnt.h): the data is
    /// downloaded when read. OneDrive placeholders seen as they are.</summary>
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>FILE_ATTRIBUTE_RECALL_ON_OPEN (winnt.h).</summary>
    public const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    /// <summary>
    /// True for an online-only file: its data is not on this computer. The
    /// same rule as patch 0003's IsCloudPlaceholder: RECALL_ON_DATA_ACCESS
    /// for anything; OFFLINE or RECALL_ON_OPEN for a file, not a folder.
    /// Most processes see Cloud Files placeholders disguised: a Dropbox
    /// online-only file reads ARCHIVE | SPARSE_FILE | REPARSE_POINT | OFFLINE
    /// (0x1620). A reparse tag alone does not count: a downloaded placeholder
    /// keeps its tag and its data is local.
    /// </summary>
    public static bool IsCloudOnly(FileAttributes attributes)
    {
        if ((attributes & RecallOnDataAccess) != 0) return true;
        if ((attributes & FileAttributes.Directory) != 0) return false;
        return (attributes & (FileAttributes.Offline | RecallOnOpen)) != 0;
    }

    /// <summary>True when the file at <paramref name="path"/> is online-only.
    /// False for ordinary local files, missing paths, and on other systems.</summary>
    public static bool IsCloudOnly(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (string.IsNullOrEmpty(path)) return false;
        return HasCloudAttributes(path);
    }

    [SupportedOSPlatform("windows")]
    private static bool HasCloudAttributes(string path)
    {
        try
        {
            // GetFileAttributesEx: metadata only, the data is not opened.
            return IsCloudOnly(File.GetAttributes(path));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // File missing / access denied: not online-only, so callers
            // don't false-positive on transient I/O errors.
            return false;
        }
    }

    /// <summary>"8,335 online-only files skipped", or null for none.</summary>
    public static string? SkippedPhrase(long count) => count switch
    {
        <= 0 => null,
        1 => "1 online-only file skipped",
        _ => $"{count:N0} online-only files skipped",
    };

    /// <summary>Human-readable explanation for the GUI's tooltip /
    /// docs. Wraps the answer above in one sentence each side of the
    /// flag so users see what they're signing up for.</summary>
    public static string ExplainBackupBehavior(bool excludeCloudFiles) => excludeCloudFiles
        ? "Online-only files (Dropbox, OneDrive and other cloud folders) are skipped without being downloaded, and cannot be recovered from this backup. The run says how many were skipped."
        : "Online-only files may download during backup and use local disk space afterward. Some placeholders cannot be read and will not be backed up. Check the run for warnings; Backspace does not automatically remove downloaded copies.";
}
