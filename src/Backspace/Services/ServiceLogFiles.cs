using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>
/// The background service's run history and logs, read straight from the
/// portable folder (U138, 5 November 2026: "Why not just read it directly from
/// the logs in the folder, needed anyway if no service is running?"). The
/// service writes them in <c>Backspace.config\service\logs\engine\&lt;backup&gt;</c>,
/// beside the desktop's own, and the desktop account can read them: they
/// inherit the portable folder's permissions. Before, the app asked the
/// service through its transcript pipe, starting it if stopped, and got only
/// the last 64 KB. That pipe remains the fallback when the files can't be read.
/// Reading is safe in this direction: the desktop gains nothing it couldn't
/// already open; the service never reads paths the desktop supplies.
/// </summary>
internal static class ServiceLogFiles
{
    internal static string Root => Path.Combine(AppPaths.ConfigRoot, "service", "logs", "engine");

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime Written, long Length, List<RunRecord> Runs)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The service's record of this run and the folder its history
    /// is in, or null when the service has none.</summary>
    internal static (RunRecord Run, string Folder)? Find(string backupId, string runId)
    {
        if (string.IsNullOrEmpty(runId)) return null;
        string[] folders;
        try { folders = Directory.Exists(Root) ? Directory.GetDirectories(Root) : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        foreach (var folder in folders)
        {
            var match = Runs(Path.Combine(folder, "history.json"))
                .LastOrDefault(r => r.Id == runId && (string.IsNullOrEmpty(backupId) || r.BackupId == backupId));
            if (match is not null) return (match, folder);
        }
        return null;
    }

    /// <summary>The log files of a run the service made, as they are on this
    /// computer: the run's own log first, then its cells' (U138, U143).</summary>
    internal static IReadOnlyList<string> LogPaths(RunRecord run)
    {
        if (Find(run.BackupId, run.Id) is not { } found) return Array.Empty<string>();
        var paths = new List<string>();
        foreach (var recorded in new[] { found.Run.LogPath }.Concat(found.Run.Cells.Select(c => c.LogPath)))
            if (Local(recorded, found.Folder) is { } path && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                paths.Add(path);
        return paths;
    }

    /// <summary>The log of one of the run's cells, by source and destination.</summary>
    internal static string? CellLogPath(RunRecord run, string sourcePath, string destinationName)
    {
        if (Find(run.BackupId, run.Id) is not { } found) return null;
        var cell = found.Run.Cells.FirstOrDefault(c => string.Equals(c.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.DestinationName, destinationName, StringComparison.OrdinalIgnoreCase));
        return cell is null ? null : Local(cell.LogPath, found.Folder);
    }

    // A recorded path as it is now: the same file, or the file of that name
    // beside the history (the portable folder may have moved since).
    private static string? Local(string? recorded, string folder)
    {
        if (string.IsNullOrWhiteSpace(recorded)) return null;
        try
        {
            if (File.Exists(recorded)) return recorded;
            var beside = Path.Combine(folder, Path.GetFileName(recorded));
            return File.Exists(beside) ? beside : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return null; }
    }

    private static List<RunRecord> Runs(string historyFile)
    {
        try
        {
            var info = new FileInfo(historyFile);
            if (!info.Exists) return new();
            lock (Gate)
                if (Cache.TryGetValue(historyFile, out var cached) && cached.Written == info.LastWriteTimeUtc && cached.Length == info.Length)
                    return cached.Runs;
            // Another program (the service, Dropbox, a scanner) may hold the
            // file for a moment: waited out, as for every Backspace file (U135).
            var bytes = FileAccessRetry.Get(() =>
            {
                using var stream = new FileStream(historyFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                return copy.ToArray();
            }, historyFile, FileWaitReason.BackspaceFile);
            var runs = JsonSerializer.Deserialize<List<RunRecord>>(bytes, Json) ?? new();
            lock (Gate) Cache[historyFile] = (info.LastWriteTimeUtc, info.Length, runs);
            return runs;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException)
        {
            return new();
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
