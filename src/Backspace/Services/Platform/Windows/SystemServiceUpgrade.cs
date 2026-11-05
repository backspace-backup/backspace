using System;
using System.IO;
using System.Linq;
using Backspace.Models;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// The one-time move from the background service of Backspace 1.0 (a virtual
/// account with its own protected folder) to the version-3 service (U100).
/// The earlier service first returns the saved sign-ins and restores every
/// permission it changed (<see cref="ServiceRetirement"/>). This then keeps
/// its run history and transcripts, in the new service's ordinary folder, and
/// deletes the protected folders, which Dropbox and the owner could not read.
/// </summary>
internal static class SystemServiceUpgrade
{
    internal static void MoveRetiredData(string root)
    {
        ServiceRegistration.RequireAdministrator();
        var config = ServicePortableLayout.Config(root);
        var target = Path.Combine(SystemService.DataDirectoryFor(root), "logs", "engine");
        var sources = new[] { ServicePortableLayout.Service(root) }
            .Concat(Directory.Exists(config) ? Directory.EnumerateDirectories(config, "windows-service-archive-*") : [])
            .Where(Directory.Exists).ToList();
        foreach (var folder in sources)
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
            var logs = Path.Combine(folder, "data", "logs", "engine");
            if (Directory.Exists(logs)) CopyHistory(logs, target);
        }
        foreach (var folder in sources)
        {
            try { DeleteTree(folder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { AppLogger.For(nameof(SystemServiceUpgrade)).Warning(ex, "Could not delete the earlier service folder {Folder}", folder); }
        }
    }

    /// <summary>Copies each backup's transcripts, then adds its runs to the
    /// new history with their transcript paths pointing at the copies.</summary>
    internal static void CopyHistory(string from, string to)
    {
        var store = new LogStore(to);
        foreach (var backup in Directory.EnumerateDirectories(from))
        {
            if ((File.GetAttributes(backup) & FileAttributes.ReparsePoint) != 0) continue;
            var destination = Path.Combine(to, Path.GetFileName(backup));
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(backup))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith("history.json", StringComparison.OrdinalIgnoreCase)) continue;
                var copy = Path.Combine(destination, name);
                if (!File.Exists(copy)) FileAccessRetry.Run(() => File.Copy(file, copy), file);
            }
            if (!RunHistoryFile.Exists(Path.Combine(backup, "history.json"))) continue;
            // Read so that a failure stops the move (the earlier folder is kept),
            // never as an empty history that the deletion below would lose (U135).
            var runs = new LogStore(from).LoadHistoryOrThrow(Path.GetFileName(backup));
            var existing = store.LoadHistoryOrThrow(Path.GetFileName(backup)).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var run in runs.Where(r => !existing.Contains(r.Id)).OrderBy(r => r.StartedUtc))
            {
                run.LogPath = Relocate(run.LogPath, destination);
                foreach (var cell in run.Cells) cell.LogPath = Relocate(cell.LogPath, destination);
                store.AppendRun(run);
            }
        }
    }

    private static string Relocate(string path, string to)
        => string.IsNullOrEmpty(path) ? path : Path.Combine(to, Path.GetFileName(path));

    private static void DeleteTree(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(entry, File.GetAttributes(entry) & ~FileAttributes.ReadOnly);
        Directory.Delete(path, recursive: true);
    }
}
