using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>
/// Removing a backup also deletes what it stored (U029, 29 October 2026), so
/// no copies are left behind that Backspace could no longer show or clean
/// up. Each destination's snapshots for the backup are erased through
/// <see cref="StorageCleaner"/>, which never touches other backups' data.
/// When a destination cannot be reached (an unplugged drive, no network,
/// a failed sign-in) the erase is remembered in pending-removals.json beside
/// the configuration and retried while Backspace is open, until it succeeds
/// or that destination is removed from Backspace. The list lives outside
/// config.json so it never changes what the background service approved.
/// </summary>
public sealed class BackupRemoval
{
    public sealed class Pending
    {
        /// <summary>A copy of the removed backup: its name, snapshot seed and
        /// sources identify the snapshots to erase.</summary>
        public Backup Backup { get; set; } = new();
        public string DestinationId { get; set; } = "";
        public string StorageName { get; set; } = "default";
        public DateTime RequestedUtc { get; set; }
        public DateTime? LastAttemptUtc { get; set; }
        public string? LastError { get; set; }
    }

    /// <summary>What happened at each destination when the backup was removed.</summary>
    public sealed record Outcome(IReadOnlyList<string> Erased, IReadOnlyList<string> Deferred);

    private static Serilog.ILogger _log => AppLogger.For<BackupRemoval>();
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(30);
    private readonly object _sync = new();
    private readonly string _path;
    private readonly ConfigStore _config;
    private readonly StorageCleaner _cleaner;
    private readonly MaintenanceCoordinator _maintenance;
    private readonly LogStore? _logs;
    private int _retrying;

    public BackupRemoval(ConfigStore config, StorageCleaner cleaner, MaintenanceCoordinator maintenance, string? path = null, LogStore? logs = null)
    {
        _config = config; _cleaner = cleaner; _maintenance = maintenance; _logs = logs;
        _path = path ?? Path.Combine(AppPaths.ConfigRoot, "pending-removals.json");
    }

    public IReadOnlyList<Pending> ReadPending() { lock (_sync) return Load(); }

    /// <summary>Remove the backup from Backspace and erase its stored copies.
    /// Unreachable destinations are queued first, so nothing is forgotten if
    /// the app closes part-way. The backup's definition and schedule are
    /// removed even when some erases have to wait.</summary>
    public async Task<Outcome> RemoveAsync(Backup backup, IProgress<string>? progress, IProgress<int?>? percent, CancellationToken ct)
    {
        var copy = JsonSerializer.Deserialize<Backup>(JsonSerializer.Serialize(backup))!;
        var targets = copy.Targets.DistinctBy(t => (t.DestinationId, t.StorageName))
            .Where(t => _config.Current.Destinations.Any(d => d.Id == t.DestinationId)).ToList();
        lock (_sync)
        {
            var pending = Load();
            pending.AddRange(targets.Select(t => new Pending { Backup = copy, DestinationId = t.DestinationId, StorageName = t.StorageName, RequestedUtc = DateTime.UtcNow }));
            Save(pending);
        }
        _config.Update(cfg => cfg.Backups.RemoveAll(b => b.Id == copy.Id));
        try { ServiceLocator.Scheduler.DeleteTask(copy); }
        catch (Exception ex) { _log.Warning(ex, "Scheduler delete failed for {Name}", copy.Name); }

        var erased = new List<string>();
        var deferred = new List<string>();
        foreach (var target in targets)
        {
            var destination = _config.Current.Destinations.FirstOrDefault(d => d.Id == target.DestinationId);
            if (destination is null) continue;
            var error = await TryEraseAsync(copy, destination, target.StorageName, progress, percent, ct);
            if (error is null) erased.Add(destination.Name);
            else deferred.Add(destination.Name);
        }
        if (deferred.Count == 0) DeleteLocalRecords(copy);
        return new Outcome(erased, deferred);
    }

    /// <summary>Retry every queued erase whose destination looks reachable.
    /// Waits instead of interrupting: it does nothing while a backup,
    /// restore or other cleanup is running.</summary>
    public async Task RetryPendingAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _retrying, 1) == 1) return;
        try
        {
            foreach (var item in ReadPending())
            {
                ct.ThrowIfCancellationRequested();
                var destination = _config.Current.Destinations.FirstOrDefault(d => d.Id == item.DestinationId);
                if (destination is null) { Forget(item); continue; }
                if (item.LastAttemptUtc is { } last && item.LastError is not null && !destination.IsLocalLike
                    && DateTime.UtcNow - last < RetryAfterFailure) continue;
                if (!LooksReachable(destination)) continue;
                if (_maintenance.IsActive || ServiceLocator.Orchestrator.SnapshotRunningBackupIds().Count > 0) return;
                await TryEraseAsync(item.Backup, destination, item.StorageName, null, null, ct);
                if (!ReadPending().Any(p => p.Backup.Id == item.Backup.Id)) DeleteLocalRecords(item.Backup);
            }
        }
        finally { Interlocked.Exchange(ref _retrying, 0); }
    }

    /// <summary>Runs <see cref="RetryPendingAsync"/> every two minutes for the
    /// life of the desktop app.</summary>
    public void StartRetrying(CancellationToken stop) => _ = Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            try { await RetryPendingAsync(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex) { _log.Warning(ex, "Retrying removed backups' cleanup failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(2), stop); }
            catch (OperationCanceledException) { return; }
        }
    });

    /// <summary>Queued erases for this destination, so removing the
    /// destination can erase them too before they are forgotten.</summary>
    public IReadOnlyList<(Backup Backup, string StorageName)> PendingFor(string destinationId)
        => ReadPending().Where(p => p.DestinationId == destinationId).Select(p => (p.Backup, p.StorageName)).ToList();

    /// <summary>Drop queued erases for a destination Backspace no longer uses.</summary>
    public void ForgetDestination(string destinationId)
    {
        lock (_sync)
        {
            var pending = Load();
            if (pending.RemoveAll(p => p.DestinationId == destinationId) > 0) Save(pending);
        }
    }

    private async Task<string?> TryEraseAsync(Backup backup, Destination destination, string storage,
        IProgress<string>? progress, IProgress<int?>? percent, CancellationToken ct)
    {
        string? error = null;
        if (!LooksReachable(destination)) error = $"«{destination.Name}» is not connected.";
        else
        {
            try
            {
                using var lease = await _maintenance.AcquireAsync($"Deleting «{backup.Name}» from «{destination.Name}»", progress, cancelGracePeriod: null, ct);
                var report = await _cleaner.EraseBackupFromDestinationAsync(backup, destination, storage, ct, progress, percent);
                if (report.Errors.Count > 0) error = string.Join("; ", report.Errors);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { error = ex.Message; }
        }
        lock (_sync)
        {
            var pending = Load();
            var match = pending.FirstOrDefault(p => p.Backup.Id == backup.Id && p.DestinationId == destination.Id && p.StorageName == storage);
            if (match is not null)
            {
                if (error is null) pending.Remove(match);
                else { match.LastAttemptUtc = DateTime.UtcNow; match.LastError = error; }
                Save(pending);
            }
        }
        if (error is not null) _log.Information("Erase of {Backup} at {Destination} postponed: {Error}", backup.Name, destination.Name, error);
        return error;
    }

    /// <summary>A cheap check before trying: the drive or share must exist.
    /// Cloud destinations are simply tried.</summary>
    internal static bool LooksReachable(Destination destination)
    {
        if (!destination.IsLocalLike) return true;
        if (VolumeDetector.Check(destination.ExpectedDriveLetter, destination.ExpectedVolumeLabel).Verdict != VolumeDetector.Verdict.Mounted)
            return false;
        try
        {
            var root = Path.GetPathRoot(destination.PathOrSubpath);
            return string.IsNullOrEmpty(root) || Directory.Exists(root);
        }
        catch (Exception) { return false; }
    }

    private void Forget(Pending item)
    {
        lock (_sync)
        {
            var pending = Load();
            if (pending.RemoveAll(p => p.Backup.Id == item.Backup.Id && p.DestinationId == item.DestinationId && p.StorageName == item.StorageName) > 0)
                Save(pending);
        }
    }

    /// <summary>Once nothing for the backup remains queued: its run history
    /// and transcripts (the person's decision, 29 October 2026) and
    /// Backspace's own working folder. A folder named like another configured
    /// backup is that backup's too, so it is left alone.</summary>
    private void DeleteLocalRecords(Backup backup)
    {
        try { _logs?.ForgetBackup(backup.Id); }
        catch (Exception ex) { _log.Information(ex, "The run history for {Backup} was kept", backup.Name); }
        try
        {
            if (_config.Current.Backups.Any(b => AppPaths.Sanitize(b.Name) == AppPaths.Sanitize(backup.Name))) return;
            var dir = AppPaths.RepoDirForBackup(backup.Name);
            if (Directory.Exists(dir) && !new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) { _log.Information(ex, "The working folder for {Backup} was kept", backup.Name); }
    }

    private List<Pending> Load()
    {
        using var transaction = StateFileLock.Acquire(_path);
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<List<Pending>>(File.ReadAllText(_path)) ?? new() : new(); }
        catch (JsonException ex)
        {
            // Never discard the record: keep the unreadable copy for support.
            _log.Error(ex, "pending-removals.json is unreadable; it was kept as pending-removals.unreadable.json");
            try { File.Copy(_path, Path.ChangeExtension(_path, ".unreadable.json"), overwrite: false); } catch { }
            return new();
        }
    }

    private void Save(List<Pending> pending)
    {
        using var transaction = StateFileLock.Acquire(_path);
        if (pending.Count == 0) { try { File.Delete(_path); } catch (FileNotFoundException) { } return; }
        TestRestoreContext.WriteAtomic(_path, pending);
    }
}
