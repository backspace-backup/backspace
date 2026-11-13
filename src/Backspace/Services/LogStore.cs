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
    /// <summary>A cache refresh is not a newly completed operation. Consumers
    /// should refresh this backup without moving the user's selection.</summary>
    public event Action<string>? HistoryRefreshed;
    private static readonly object _gate = new();
    private readonly string? _explicitEngineLogsRoot;
    private string EngineLogsRoot => _explicitEngineLogsRoot ?? AppPaths.EngineLogsRoot;
    internal LogStore(string engineLogsRoot) => _explicitEngineLogsRoot = engineLogsRoot;
    /// <summary>The portable copy whose background service's history this
    /// store also reads, when set explicitly (tests).</summary>
    private readonly string? _explicitServiceInstallation;

    public LogStore() { }

    /// <summary>The desktop's own history, plus the background service's
    /// history and logs in <paramref name="installation"/>'s portable folder.</summary>
    internal static LogStore WithServiceOf(string installation) => new(installation, service: true);
    private LogStore(string installation, bool service) => _explicitServiceInstallation = installation;

    /// <summary>The copy whose service history is read: this one, when its
    /// settings are the portable ones beside it.</summary>
    private string? ServiceInstallation()
    {
#if WINDOWS
        if (_explicitServiceInstallation is not null) return _explicitServiceInstallation;
        if (_explicitEngineLogsRoot is not null) return null;
        var root = AppContext.BaseDirectory.TrimEnd('\\');
        return Platform.Windows.ServicePortableLayout.IsPortableConfig(root, AppPaths.ConfigRoot) ? root : null;
#else
        return null;
#endif
    }

    private List<RunRecord> WithServiceHistory(string backupId, List<RunRecord> local)
    {
#if WINDOWS
        if (ServiceInstallation() is { } installation)
        {
            // The service's history and logs are in the portable folder,
            // readable by the desktop: read as they are (U138).
            try { local.AddRange(Platform.Windows.ServiceHistoryFiles.Read(installation, backupId)); }
            catch (Exception ex) { AppLogger.For<LogStore>().Warning(ex, "The background service's history could not be read"); }
        }
        return local.GroupBy(r => r.Id).Select(g => g.LastOrDefault(r => !string.IsNullOrEmpty(r.LogPath)) ?? g.Last())
            .OrderByDescending(r => r.StartedUtc).ToList();
#else
        return local;
#endif
    }

    public void RefreshBackgroundHistory(string backupId)
    {
#if WINDOWS
        if (string.IsNullOrEmpty(backupId) || ServiceInstallation() is not { } installation) return;
        // The service's files are read on every load (U138): tell the views
        // only when they changed since this backup last looked.
        var signature = ServiceFilesSignature(installation);
        if (signature.Length == 0 || _seenServiceFiles.TryGetValue(backupId, out var seen) && seen == signature) return;
        _seenServiceFiles[backupId] = signature;
        System.Threading.Tasks.Task.Run(() =>
        {
            try { HistoryRefreshed?.Invoke(backupId); } catch { /* views reload on their own next refresh */ }
        });
#endif
    }

#if WINDOWS
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _seenServiceFiles = new(StringComparer.Ordinal);

    /// <summary>When and how large the service's history files are, all
    /// backups together: it changes whenever the service records a run.</summary>
    private (DateTime At, string Installation, string Value) _lastSignature;

    private string ServiceFilesSignature(string installation)
    {
        // Every card asks on the same tick: look once for all of them.
        var last = _lastSignature;
        if (DateTime.UtcNow - last.At < TimeSpan.FromSeconds(1) && last.Installation == installation && last.Value is not null) return last.Value;
        var value = ReadServiceFilesSignature(installation);
        _lastSignature = (DateTime.UtcNow, installation, value);
        return value;
    }

    private static string ReadServiceFilesSignature(string installation)
    {
        try
        {
            var logs = Path.Combine(Platform.Windows.SystemService.DataDirectoryFor(installation), "logs", "engine");
            if (!Directory.Exists(logs)) return "";
            var parts = new System.Text.StringBuilder();
            foreach (var directory in Directory.EnumerateDirectories(logs).Order(StringComparer.OrdinalIgnoreCase))
                foreach (var name in new[] { "history.json", "timeline.json" })
                {
                    var file = new FileInfo(Path.Combine(directory, name));
                    if (file.Exists) parts.Append(file.FullName).Append('|').Append(file.LastWriteTimeUtc.Ticks).Append('|').Append(file.Length).Append(';');
                }
            return parts.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
#endif

    /// <summary>Every transcript has its own identity even when operations
    /// start in the same second or source folders share their final name.</summary>
    internal static string NewTranscriptPath(string backupName, string label, DateTime startedUtc)
        => Path.Combine(AppPaths.LogDirForBackup(backupName),
            $"{startedUtc:yyyyMMdd-HHmmss}-{AppPaths.Sanitize(label)}-{Guid.NewGuid():N}.log");

    public string GetHistoryFile(string backupName)
        => Path.Combine(EngineLogsRoot, AppPaths.Sanitize(backupName), "history.json");

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
        if (string.IsNullOrEmpty(backupId)) return new();
        if (!Directory.Exists(EngineLogsRoot)) return WithServiceHistory(backupId, new());
        var merged = new List<RunRecord>();
        lock (_gate)
        {
            foreach (var directory in Directory.EnumerateDirectories(EngineLogsRoot))
            {
                var file = Path.Combine(directory, "history.json");
                if (!RunHistoryFile.Exists(file)) continue;
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
        return WithServiceHistory(backupId, merged.OrderByDescending(r => r.StartedUtc).ToList());
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
            var all = previous.Where(r => r.Id != record.Id).Append(record).OrderByDescending(r => r.StartedUtc).ToList();
            // Keep compact dated outcomes after detailed transcripts expire.
            // The same cross-process transaction protects both files. Writing
            // this first preserves the terminal result even if history fails.
            var timelinePath = Path.Combine(Path.GetDirectoryName(file)!, "timeline.json");
            var timelineSaved = false;
            try
            {
                var timeline = ReadHistory(timelinePath).Concat(all).GroupBy(r => r.Id)
                    .Select(g => g.Last()).OrderByDescending(r => r.StartedUtc)
                    .Select(r => new RunRecord
                    {
                        Id = r.Id, BackupId = r.BackupId, BackupName = r.BackupName, Kind = r.Kind,
                        TriggeredAutomatically = r.TriggeredAutomatically, StartedUtc = r.StartedUtc,
                        EndedUtc = r.EndedUtc, Status = r.Status, Summary = r.Summary, Checks = r.Checks,
                        LogPath = r.LogPath,
                        // What it added and freed stays after its log expires (U142).
                        FilesNew = r.FilesNew, FilesModified = r.FilesModified, BytesUploaded = r.BytesUploaded,
                        SnapshotsRemoved = r.SnapshotsRemoved, BytesFreed = r.BytesFreed, QueuedFor = r.QueuedFor,
                        Notes = r.Notes,
                    }).ToList();
                WriteTimeline(timelinePath, timeline);
                timelineSaved = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The archive is an extra copy. If a scanner holds it, keep
                // every pending result and transcript in detailed history.
                // The next append retries the archive before expiring anything.
                AppLogger.For<LogStore>().Warning(ex, "Timeline is unavailable; retaining all detailed history until it can be saved: {Path}", timelinePath);
            }
            var retained = timelineSaved ? all.Take(Math.Max(0, RetainLastNRuns)).ToList() : all;
            var expired = timelineSaved ? all.Skip(Math.Max(0, RetainLastNRuns)).ToList() : new List<RunRecord>();
            RunHistoryFile.Write(file, retained, JsonOpts);

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

    /// <summary>Delete every run record and transcript of a removed backup,
    /// across the folders of its earlier names (U029, 29 October 2026).
    /// Records of other backups in a shared folder are kept; a folder left
    /// with nothing else in it is removed.</summary>
    public void ForgetBackup(string backupId)
    {
        if (string.IsNullOrEmpty(backupId) || !Directory.Exists(EngineLogsRoot)) return;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        lock (_gate)
        {
            foreach (var directory in Directory.EnumerateDirectories(EngineLogsRoot))
            {
                if (new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                var history = Path.Combine(directory, "history.json");
                var timelinePath = Path.Combine(directory, "timeline.json");
                try
                {
                    using (StateFileLock.Acquire(history))
                    {
                        var records = ReadHistory(history);
                        var timeline = ReadHistory(timelinePath);
                        var removed = records.Concat(timeline).Where(r => r.BackupId == backupId).ToList();
                        if (removed.Count == 0) continue;
                        var keptRecords = records.Where(r => r.BackupId != backupId).ToList();
                        var keptTimeline = timeline.Where(r => r.BackupId != backupId).ToList();
                        var stillReferenced = new HashSet<string>(keptRecords.Concat(keptTimeline).SelectMany(LogPaths), comparer);
                        var full = Path.GetFullPath(directory);
                        foreach (var log in removed.SelectMany(LogPaths).Distinct(comparer))
                        {
                            if (string.IsNullOrWhiteSpace(log) || stillReferenced.Contains(log)) continue;
                            try
                            {
                                var path = Path.GetFullPath(log);
                                if (comparer.Equals(Path.GetDirectoryName(path), full)
                                    && string.Equals(Path.GetExtension(path), ".log", StringComparison.OrdinalIgnoreCase)) FileAccessRetry.DeleteIfExists(path);
                            }
                            catch (Exception ex) { AppLogger.For<LogStore>().Debug(ex, "Could not delete a removed backup's transcript"); }
                        }
                        RunHistoryFile.Write(history, keptRecords, JsonOpts);
                        WriteTimeline(timelinePath, keptTimeline);
                        if (keptRecords.Count > 0 || keptTimeline.Count > 0) continue;
                    }
                    // Nothing else lives here: drop the folder, unless another
                    // file (for example an unrelated transcript) is still in it.
                    var others = Directory.EnumerateFileSystemEntries(directory)
                        .Where(e => !Path.GetFileName(e).StartsWith("history.json", StringComparison.OrdinalIgnoreCase)
                                 && !Path.GetFileName(e).StartsWith("timeline.json", StringComparison.OrdinalIgnoreCase));
                    if (!others.Any()) FileAccessRetry.Run(() => { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }, directory, directoryCleanup: true);
                }
                catch (Exception ex) { AppLogger.For<LogStore>().Warning(ex, "A removed backup's history in {Directory} was kept", directory); }
            }
        }
    }

    public List<RunRecord> LoadTimelineByBackupId(string backupId)
    {
        if (string.IsNullOrEmpty(backupId)) return new();
        if (!Directory.Exists(EngineLogsRoot)) return WithServiceHistory(backupId, new());
        var merged = new List<RunRecord>();
        lock (_gate)
        {
            foreach (var directory in Directory.EnumerateDirectories(EngineLogsRoot))
            {
                var history = Path.Combine(directory, "history.json");
                using var transaction = StateFileLock.Acquire(history);
                merged.AddRange(ReadHistory(Path.Combine(directory, "timeline.json")).Where(r => r.BackupId == backupId));
                merged.AddRange(ReadHistory(history).Where(r => r.BackupId == backupId));
            }
        }
        return WithServiceHistory(backupId, merged.GroupBy(r => r.Id).Select(g => g.Last()).OrderByDescending(r => r.StartedUtc).ToList());
    }

    private static void WriteTimeline(string path, List<RunRecord> records) => RunHistoryFile.Write(path, records, JsonOpts);

    private static List<RunRecord> ReadHistory(string file) => RunHistoryFile.Read(file, JsonOpts);

    /// <summary>Like <see cref="LoadHistory"/>, but a history that can't be
    /// read throws instead of reading as empty: before copying runs and then
    /// deleting their source, an empty read lost them (U135).</summary>
    internal List<RunRecord> LoadHistoryOrThrow(string backupName)
    {
        var file = GetHistoryFile(backupName);
        lock (_gate)
        {
            using var transaction = StateFileLock.Acquire(file);
            return ReadHistory(file);
        }
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
