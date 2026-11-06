using System;
using System.Collections.Generic;
using System.IO;
using Backspace.Models;

namespace Backspace.Services.Platform.Windows;

/// <summary>Background service history the desktop reads from the portable
/// folder: the current (version-3) service's, and what earlier services left.</summary>
internal static class ServiceHistoryFiles
{
    internal static List<RunRecord> Read(string root, string backupId)
    {
        var all = new List<RunRecord>();
        // The version-3 service's history and logs (U100), running or not:
        // they are in Backspace.config\service and inherit the portable
        // folder's permissions, so the desktop reads them as they are (U138,
        // 6 November 2026). Only the earlier services (versions 1 and 2) kept
        // them in a folder only their own account could read, and are asked
        // through their pipes.
        all.AddRange(SystemServiceRuns(root, backupId));
        if (ServiceRetirement.Read(root) is { Stage: "complete" } retired)
            all.AddRange(ReadData(ServicePortableLayout.Data(root), retired, backupId));
        var config = ServicePortableLayout.Config(root);
        if (Directory.Exists(config))
            foreach (var directory in Directory.EnumerateDirectories(config, "windows-service-archive-*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Retained service history was redirected.");
                all.AddRange(ReadData(Path.Combine(directory, "data"), ServiceRetirement.ReadArchive(directory), backupId));
            }
        return all;
    }

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

    private static List<RunRecord> ReadData(string actualData, ServiceRetirement.Record retired, string backupId)
    {
        var logs = Path.Combine(actualData, "logs", "engine");
        var records = new LogStore(logs).LoadTimelineByBackupId(backupId);
        // A portable folder may have moved after service removal. Stored
        // transcript paths follow this retained data, never the old location.
        string Relocate(string path)
        {
            var previous = Path.Combine(retired.Binding.DataDirectory, "logs", "engine").TrimEnd('\\') + "\\";
            if (!path.StartsWith(previous, StringComparison.OrdinalIgnoreCase)) return "";
            var relocated = Path.GetFullPath(Path.Combine(logs, path[previous.Length..]));
            return relocated.StartsWith(logs.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ? relocated : "";
        }
        foreach (var record in records)
        {
            record.LogPath = Relocate(record.LogPath);
            foreach (var cell in record.Cells) cell.LogPath = Relocate(cell.LogPath);
        }
        return records;
    }
}
