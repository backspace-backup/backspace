using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>Persists the latest per-backup run history and its retention policy.
/// Every append is a cross-process transaction: a GUI, scheduler or helper cannot
/// replace another writer's history. Reads return fresh records, including runs
/// completed by a different process since the Logs view was last opened.</summary>
public sealed class LogStore
{
    public int RetainLastNRuns { get; init; } = 30;
    public event Action<RunRecord>? RunAppended;
    private static readonly object _gate = new();

    /// <summary>Every transcript has its own identity even when operations
    /// start in the same second or source folders share their final name.</summary>
    internal static string NewTranscriptPath(string backupName, string label, DateTime startedUtc)
        => Path.Combine(AppPaths.LogDirForBackup(backupName),
            $"{startedUtc:yyyyMMdd-HHmmss}-{AppPaths.Sanitize(label)}-{Guid.NewGuid():N}.log");

    public string GetHistoryFile(string backupName)
        => Path.Combine(AppPaths.LogDirForBackup(backupName), "history.json");

    public List<RunRecord> LoadHistory(string backupName)
    {
        var file = GetHistoryFile(backupName);
        lock (_gate)
        {
            try
            {
                using var transaction = StateFileLock.Acquire(file);
                return ReadHistory(file);
            }
            catch (Exception ex)
            {
                AppLogger.For<LogStore>().Warning(ex, "Couldn't read history for {Backup}", backupName);
                return new();
            }
        }
    }

    /// <summary>History is stored by backup name. Union by the immutable ID
    /// stitches together folders from before and after a rename.</summary>
    public List<RunRecord> LoadHistoryByBackupId(string backupId)
    {
        if (string.IsNullOrEmpty(backupId) || !Directory.Exists(AppPaths.EngineLogsRoot)) return new();
        var merged = new List<RunRecord>();
        lock (_gate)
        {
            foreach (var directory in Directory.EnumerateDirectories(AppPaths.EngineLogsRoot))
            {
                var file = Path.Combine(directory, "history.json");
                if (!File.Exists(file)) continue;
                try
                {
                    using var transaction = StateFileLock.Acquire(file);
                    merged.AddRange(ReadHistory(file).Where(r => r.BackupId == backupId));
                }
                catch (Exception ex)
                {
                    AppLogger.For<LogStore>().Warning(ex, "Skipping unreadable history at {File}", file);
                }
            }
        }
        return merged.OrderByDescending(r => r.StartedUtc).ToList();
    }

    public void AppendRun(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            var file = GetHistoryFile(record.BackupName);
            using var transaction = StateFileLock.Acquire(file);
            List<RunRecord> previous;
            try { previous = ReadHistory(file); }
            catch (JsonException ex)
            {
                var preserved = file + $".broken.{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
                // Do not replace even corrupt history if its recovery copy failed.
                File.Copy(file, preserved, overwrite: false);
                AppLogger.For<LogStore>().Error(ex, "Corrupt history preserved at {Preserved}", preserved);
                previous = new();
            }
            var all = previous.Append(record).OrderByDescending(r => r.StartedUtc).ToList();
            var retained = all.Take(Math.Max(0, RetainLastNRuns)).ToList();
            var expired = all.Skip(Math.Max(0, RetainLastNRuns)).ToList();
            var temporary = file + $".{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(retained, JsonOpts));
                if (File.Exists(file)) File.Replace(temporary, file, destinationBackupFileName: null);
                else File.Move(temporary, file);
            }
            finally { try { File.Delete(temporary); } catch { } }

            // Only a log named by an expired record is ours to remove. Sweeping
            // every unreferenced .log erased concurrent runs before they had a
            // terminal history entry. Keep pruning in the transaction so another
            // append cannot add a reference between the decision and deletion.
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var referenced = new HashSet<string>(retained.SelectMany(LogPaths), comparer);
            var directory = Path.GetFullPath(Path.GetDirectoryName(file)!);
            foreach (var obsolete in expired.SelectMany(LogPaths).Distinct(comparer))
            {
                if (string.IsNullOrWhiteSpace(obsolete) || referenced.Contains(obsolete)) continue;
                try
                {
                    var full = Path.GetFullPath(obsolete);
                    if (!comparer.Equals(Path.GetDirectoryName(full), directory)
                        || !string.Equals(Path.GetExtension(full), ".log", StringComparison.OrdinalIgnoreCase)) continue;
                    File.Delete(full);
                }
                catch (Exception ex) { AppLogger.For<LogStore>().Debug(ex, "Could not prune expired transcript"); }
            }
        }
        try { RunAppended?.Invoke(record); }
        catch { /* subscriber failures must not invalidate persisted history */ }
    }

    private static List<RunRecord> ReadHistory(string file)
    {
        if (!File.Exists(file)) return new();
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<List<RunRecord>>(stream, JsonOpts)
            ?? throw new JsonException("History contains no run list.");
    }

    private static IEnumerable<string> LogPaths(RunRecord record)
        => new[] { record.LogPath }.Concat(record.Cells.Select(c => c.LogPath));

    public RunRecord? MostRecent(string backupName)
        => LoadHistory(backupName).OrderByDescending(r => r.StartedUtc).FirstOrDefault();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}