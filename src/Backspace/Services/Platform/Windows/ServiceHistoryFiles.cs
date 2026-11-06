using System;
using System.Collections.Generic;
using System.IO;
using Backspace.Models;

namespace Backspace.Services.Platform.Windows;

/// <summary>Background service history the desktop reads from the portable folder.</summary>
internal static class ServiceHistoryFiles
{
    /// <summary>The service's history and logs (U100), running or not: they
    /// are in Backspace.config\service and inherit the portable folder's
    /// permissions, so the desktop reads them as they are (U138, 6 November 2026).</summary>
    internal static List<RunRecord> Read(string root, string backupId) => SystemServiceRuns(root, backupId);

    /// <summary>The version-3 service's runs of this backup, every one it
    /// recorded, with their logs as they are on this computer now.</summary>
    internal static List<RunRecord> SystemServiceRuns(string root, string backupId)
    {
        var logs = Path.Combine(SystemService.DataDirectoryFor(root), "logs", "engine");
        if (!Directory.Exists(logs)) return new();
        var records = new LogStore(logs).LoadTimelineByBackupId(backupId);
        foreach (var record in records)
        {
            record.LogPath = LocalLog(logs, record.LogPath);
            foreach (var cell in record.Cells) cell.LogPath = LocalLog(logs, cell.LogPath);
        }
        return records;
    }

    /// <summary>A log the service recorded, as it is now: the same file, or
    /// the file of that name in the same backup folder when the portable
    /// folder has moved since. Only a .log inside the service's logs.</summary>
    internal static string LocalLog(string logs, string? recorded)
    {
        if (string.IsNullOrWhiteSpace(recorded)) return "";
        try
        {
            var root = Path.GetFullPath(logs).TrimEnd('\\') + "\\";
            var full = Path.GetFullPath(recorded);
            if (!string.Equals(Path.GetExtension(full), ".log", StringComparison.OrdinalIgnoreCase)) return "";
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full)) return full;
            var folder = Path.GetFileName(Path.GetDirectoryName(full));
            var moved = string.IsNullOrEmpty(folder) ? "" : Path.GetFullPath(Path.Combine(root, folder, Path.GetFileName(full)));
            // A log that is gone keeps its recorded path: the log view then
            // says it is no longer on disk.
            return moved.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(moved) ? moved : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return ""; }
    }
}
