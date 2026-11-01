using System;
using System.Collections.Generic;
using System.IO;
using Backspace.Models;

namespace Backspace.Services.Platform.Windows;

internal static class ServiceRetiredHistory
{
    internal static List<RunRecord> Read(string root, string backupId)
    {
        var all = new List<RunRecord>();
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
