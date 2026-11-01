using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;

namespace Backspace.Services;

public sealed record TestRestoreOptions(bool Full = false, double SamplePercent = 1, long MaximumSampleBytes = 1024L * 1024 * 1024,
    bool CompareOriginals = false)
{
    public string Label => Full ? "Full test restore" : $"Sample test restore ({SamplePercent:0.##}%)";
    public void Validate()
    {
        if (!double.IsFinite(SamplePercent) || SamplePercent <= 0 || SamplePercent > 100 || MaximumSampleBytes < 0)
            throw new ArgumentException("Choose a percentage above 0 and at most 100, and a non-negative download limit.");
    }
}

internal sealed class TestRestoreJournal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string BackupId { get; set; } = "";
    public string BackupName { get; set; } = "";
    public string RouteFingerprint { get; set; } = "";
    public TestRestoreOptions Options { get; set; } = new();
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EndedUtc { get; set; }
    public string State { get; set; } = "running";
    public string Summary { get; set; } = "Preparing test restore…";
    public bool Dismissed { get; set; }
    public bool Scheduled { get; set; }
    public List<TestRestoreCell> Cells { get; set; } = new();
}

internal sealed class TestRestoreCell
{
    public string Key { get; set; } = "";
    public RevisionSummary Snapshot { get; set; }
    public List<RevisionFile> Files { get; set; } = new();
    public List<FileVerifyResult> Results { get; set; } = new();
    public List<FileVerifyResult> Missing { get; set; } = new();
    public string? Error { get; set; }
    public bool Complete { get; set; }
}

/// <summary>One invocation's durable plan and completed batches. Restored bytes
/// are temporary; the journal is outside the scratch-folder sweep.</summary>
internal sealed class TestRestoreContext
{
    private readonly object _sync = new();
    private readonly string _path;
    private readonly string _historyPath;
    private readonly Dictionary<string, DateTime> _history;
    private readonly ActiveRestoreSession _session;
    private readonly CancellationToken _cancellation;
    internal TestRestoreJournal Journal { get; }
    internal RestoreControl? Control { get; }
    internal bool MaintenanceLeaseHeld { get; init; }
    internal TestRestoreContext(TestRestoreJournal journal, string path, ActiveRestoreSession session, string historyPath, RestoreControl? control = null, CancellationToken cancellation = default)
    {
        _cancellation = cancellation;
        Control = control;
        Journal = journal; _path = path; _session = session; _historyPath = historyPath;
        _history = File.Exists(historyPath)
            ? JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(historyPath)) ?? new()
            : new();
    }

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    internal static string CellKey(string source, Destination dest, string storage) => Hash(source + "\0" + dest.Id + "\0" + storage);
    internal static string FileKey(string cell, RevisionFile file) => Hash(cell + "\0" + file.Path + "\0" + file.SizeBytes + "\0" + file.ModifiedUtc.ToUniversalTime().Ticks);
    internal static string Fingerprint(Backup backup, ConfigStore config) => Hash(JsonSerializer.Serialize(new
    {
        backup.Id, backup.SourcePaths, backup.FiltersText, backup.ExcludeCloudFiles,
        Targets = backup.Targets.Select(t =>
        {
            var d = config.Current.Destinations.FirstOrDefault(d => d.Id == t.DestinationId);
            return new { t.DestinationId, t.StorageName, d?.Kind, d?.PathOrSubpath, d?.ExpectedDriveLetter, d?.ExpectedVolumeLabel,
                d?.NetworkUsername, d?.NetworkPasswordRef, d?.S3Endpoint, d?.S3Region, d?.S3Bucket, d?.S3AccessKeyRef, d?.S3SecretKeyRef,
                d?.OAuthTokenRef, d?.OneDriveDriveId, d?.OneDriveDriveType, d?.OneDriveAppFolderId,
                d?.CloudAppFolderOnly, d?.StoragePasswordRef, d?.Encrypted };
        })
    }));

    internal static void WriteAtomic<T>(string path, T value, CancellationToken ct = default, FileWaitReason? reason = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value);
                stream.Flush(flushToDisk: true);
            }
            FileAccessRetry.Run(() => File.Move(temp, path, overwrite: true), path, reason ?? FileWaitReason.Progress, ct);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* Preserve the original save error. */ }
        }
    }

    internal void Save() { lock (_sync) WriteAtomic(_path, Journal, _cancellation); }
    internal void Dismiss()
    {
        lock (_sync)
        {
            var previous = Journal.Dismissed;
            Journal.Dismissed = true;
            try { WriteAtomic(_path, Journal, reason: FileWaitReason.Dismiss); }
            catch { Journal.Dismissed = previous; throw; }
        }
    }
    internal bool Finish(string state, string summary, bool allowCancelledFailure = false)
    {
        lock (_sync)
        {
            if (state is "completed" or "failed" && !allowCancelledFailure && _cancellation.IsCancellationRequested) return false;
            if (Journal.State is not ("running" or "paused") && Journal.State != state
                && !(allowCancelledFailure && state == "failed" && Journal.State == "cancelled")) return false;
            var previous = (Journal.State, Journal.Summary, Journal.EndedUtc);
            Journal.State = state; Journal.Summary = summary; Journal.EndedUtc = DateTime.UtcNow;
            // Cancellation of the work must not prevent saving its stopped result.
            try { WriteAtomic(_path, Journal, reason: FileWaitReason.Result); }
            catch { (Journal.State, Journal.Summary, Journal.EndedUtc) = previous; throw; }
            return true;
        }
    }
    internal void SavePauseState()
    {
        if (Control is null || Control.State is not (RestoreControlState.Running or RestoreControlState.Pausing or RestoreControlState.Paused)) return;
        lock (_sync)
        {
            if (Journal.State is not ("running" or "paused")) return;
            var previous = Journal.State;
            Journal.State = Control.State == RestoreControlState.Running ? "running" : "paused";
            try { WriteAtomic(_path, Journal, _cancellation, FileWaitReason.Pause); }
            catch { Journal.State = previous; throw; }
        }
    }
    internal TestRestoreCell Cell(string source, Destination dest, string storage) =>
        Journal.Cells.Single(c => c.Key == CellKey(source, dest, storage));

    internal async Task PrepareAsync(Backup backup,
        IEnumerable<(Destination Destination, BackupTarget Target, string? Unavailable)> destinations,
        RevisionBrowser revisions, CancellationToken ct)
    {
        var targets = destinations.ToList();
        var count = Math.Max(1, targets.Count * backup.SourcePaths.Count);
        var cap = Journal.Options.MaximumSampleBytes == 0 ? long.MaxValue : Math.Max(1, Journal.Options.MaximumSampleBytes / count);
        foreach (var (dest, target, unavailable) in targets)
        foreach (var source in backup.SourcePaths.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            ct.ThrowIfCancellationRequested();
            if (Control is not null) await Control.WaitWhilePausedAsync(ct);
            var key = CellKey(source, dest, target.StorageName);
            if (Journal.Cells.Any(c => c.Key == key)) continue; // Keep the exact saved snapshot and sample.
            Phase($"Reading snapshot file lists · {dest.Name}");
            var cell = new TestRestoreCell { Key = key };
            try
            {
                if (unavailable is not null) throw new IOException(unavailable);
                var snapshots = await revisions.ListRevisionsAsync(backup, source, dest, target.StorageName, ct);
                if (snapshots.Count == 0) throw new IOException("No snapshots yet. Run a backup first.");
                cell.Snapshot = snapshots.OrderByDescending(s => s.Number).First();
                if (string.IsNullOrWhiteSpace(cell.Snapshot.SnapshotId))
                    throw new IOException("The engine did not provide a stable snapshot identity; this check cannot safely resume.");
                var files = await revisions.ListFilesAsync(backup, source, dest, target.StorageName,
                    cell.Snapshot.Number, ct, cell.Snapshot.SnapshotId);
                cell.Files = Journal.Options.Full ? files.ToList()
                    : SelectSample(files, Journal.Options.SamplePercent, cap,
                        f => _history.GetValueOrDefault(FileKey(key, f), DateTime.MinValue));
                if (Journal.Options.Full && Journal.Options.CompareOriginals)
                    cell.Missing = await Task.Run(() => BackupVerifier.FindMissingSnapshotFiles(backup, source, files, cell.Snapshot.CreatedUtc, ct), ct);
                if (cell.Files.Count == 0) throw new IOException("This snapshot has no files to restore.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { cell.Error = ex.Message; }
            lock (_sync) { Journal.Cells.Add(cell); WriteAtomic(_path, Journal, _cancellation); }
        }
        PublishProgress();
    }

    // Whole-file selection is intentional: a byte range cannot test whether
    // the whole file restores. A single oversized file may exceed the budget.
    internal static List<RevisionFile> SelectSample(IReadOnlyList<RevisionFile> files, double percent, long cap,
        Func<RevisionFile, DateTime> lastChecked)
    {
        var target = Math.Min(cap, Math.Max(1, (long)Math.Ceiling(files.Sum(f => (double)Math.Max(0, f.SizeBytes)) * percent / 100)));
        var pool = files.OrderBy(lastChecked).ThenBy(_ => Random.Shared.NextInt64()).ToList();
        var picked = new List<RevisionFile>(); long bytes = 0;
        var zeroOnly = files.All(f => f.SizeBytes == 0);
        var zeroLimit = Math.Max(1, (int)Math.Ceiling(files.Count * percent / 100));
        foreach (var file in pool)
        {
            if (picked.Count > 0 && (bytes >= target || (zeroOnly && picked.Count >= zeroLimit))) break;
            picked.Add(file); bytes = checked(bytes + Math.Max(0, file.SizeBytes));
        }
        return picked;
    }

    internal void Checkpoint(TestRestoreCell cell, List<FileVerifyResult> results, bool complete = false)
    {
        lock (_sync)
        {
            var alreadyRecorded = cell.Results.Select(r => r.Path).ToHashSet(StringComparer.Ordinal);
            cell.Results = results.ToList(); cell.Complete = complete;
            WriteAtomic(_path, Journal, _cancellation); // Keep confirmed results before updating sampling history.
            var success = results.Where(r => r.Status is FileVerifyStatus.OK or FileVerifyStatus.OkNoCompare or FileVerifyStatus.Recovered)
                .Select(r => r.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var file in cell.Files.Where(f => success.Contains(f.Path) && !alreadyRecorded.Contains(f.Path)))
                _history[FileKey(cell.Key, file)] = DateTime.UtcNow;
            WriteAtomic(_historyPath, _history, _cancellation);
        }
        PublishProgress();
    }

    internal void Phase(string detail) => ServiceLocator.ActiveRestores.Describe(_session, detail);
    internal void PublishProgress(long inFlightBytes = 0)
    {
        var cells = Journal.Cells;
        ServiceLocator.ActiveRestores.UpdateProgress(_session, cells.Sum(c => c.Files.Count),
            cells.Sum(c => c.Results.Count(r => r.Path != "(restore operation)")),
            cells.Sum(c => c.Files.Sum(f => Math.Max(0, f.SizeBytes))),
            cells.Sum(c => c.Results.Sum(r => Math.Max(0, r.ExpectedBytes))) + inFlightBytes);
    }
}
