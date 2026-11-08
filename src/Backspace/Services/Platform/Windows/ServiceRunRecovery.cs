using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Backspace.Models;

namespace Backspace.Services.Platform.Windows;

/// <summary>Retain the identity of an admitted worker before launching it, so
/// an early process failure or service restart cannot disappear from history.
/// One record per worker, in <c>service-runs</c>: the service runs several
/// workers at once (U156).</summary>
internal sealed class ServiceRunRecovery(string directory, LogStore logs, ServiceNotificationQueue notifications)
{
    internal sealed record Context(int Version, string HostRunId, string BackupId, string BackupName,
        RunKind Kind, DateTime StartedUtc, bool Automatic, bool Toast, bool Sound,
        string? HistoryRunId = null, bool StopRequested = false, DateTime? InterruptedUtc = null, bool Complete = false);
    private readonly string _directory = Path.Combine(directory, "service-runs");

    /// <summary>The record of one worker.</summary>
    internal static string PathFor(string directory, string hostRunId)
    {
        if (!Guid.TryParseExact(hostRunId, "N", out _)) throw new InvalidDataException("Invalid run identity.");
        return Path.Combine(directory, "service-runs", hostRunId + ".json");
    }

    private string PathOf(string hostRunId) => PathFor(Path.GetDirectoryName(_directory)!, hostRunId);

    internal Context? Read(string? hostRunId)
    {
        if (hostRunId is null || !Guid.TryParseExact(hostRunId, "N", out _)) return null;
        var path = PathOf(hostRunId);
        if (!File.Exists(path)) return null;
        using var file = FileAccessRetry.Get(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), path);
        if (file.Length is <= 0 or > 32 * 1024) throw new InvalidDataException("Invalid retained background-run context.");
        var context = JsonSerializer.Deserialize<Context>(file, ServiceRunAuthorization.Json) ?? throw new InvalidDataException();
        Validate(context);
        if (context.HostRunId != hostRunId) throw new InvalidDataException("The retained record belongs to another run.");
        return context;
    }

    /// <summary>Every retained worker record, oldest first. One that can't be
    /// read is left in place and skipped.</summary>
    internal IReadOnlyList<Context> All()
    {
        if (!Directory.Exists(_directory)) return [];
        var found = new List<Context>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try { if (Read(Path.GetFileNameWithoutExtension(file)) is { } context) found.Add(context); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            { AppLogger.For<ServiceRunRecovery>().Warning(ex, "A retained background-run record could not be read"); }
        }
        return found.OrderBy(c => c.StartedUtc).ToList();
    }

    /// <summary>Removes the records of runs whose outcome is saved.</summary>
    internal void ForgetCompleted()
    {
        foreach (var context in All().Where(c => c.Complete))
            try { File.Delete(PathOf(context.HostRunId)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal void Begin(Context context)
    {
        Validate(context);
        if (Read(context.HostRunId) is not null) throw new IOException("This background run is already recorded.");
        Save(context);
    }

    internal void TrackHistory(string hostRunId, string historyRunId)
    {
        if (!Guid.TryParseExact(historyRunId, "N", out _)) throw new InvalidDataException();
        var context = Read(hostRunId);
        if (context is null || context.Complete || context.HistoryRunId == historyRunId) return;
        if (context.HistoryRunId is not null) throw new InvalidDataException("One worker reported different history identities.");
        Save(context with { HistoryRunId = historyRunId });
    }

    internal void StopRequested(string? hostRunId)
    {
        var context = Read(hostRunId);
        if (context is not null && !context.Complete && !context.StopRequested)
            Save(context with { StopRequested = true });
    }

    internal void Confirm(string hostRunId)
    {
        var context = Read(hostRunId);
        if (context is not null) Save(context with { Complete = true });
    }

    /// <param name="reason">What the host knows about the failure, such as a
    /// missing destination folder; otherwise the generic interruption text.</param>
    /// <param name="resumes">The backup resumes by itself: it is recorded as
    /// interrupted, and nobody is notified (U131).</param>
    internal RunRecord? Recover(string? hostRunId, string? reason = null, bool resumes = false)
    {
        var context = Read(hostRunId);
        if (context is null) return null;
        if (context.Complete) return logs.LoadTimelineByBackupId(context.BackupId)
            .FirstOrDefault(r => r.Id == (context.HistoryRunId ?? context.HostRunId));
        if (context.HistoryRunId is { } historyId)
        {
            var recorded = logs.LoadTimelineByBackupId(context.BackupId).FirstOrDefault(r => r.Id == historyId);
            if (recorded is { EndedUtc: not null } && recorded.Status != BackupRunStatus.Running)
            { Confirm(context.HostRunId); return recorded; } // Never replace an already confirmed outcome.
        }
        if (context.InterruptedUtc is null) { context = context with { InterruptedUtc = DateTime.UtcNow }; Save(context); }
        var record = new RunRecord
        {
            Id = context.HostRunId, BackupId = context.BackupId, BackupName = context.BackupName,
            StartedUtc = context.StartedUtc, EndedUtc = context.InterruptedUtc, Kind = context.Kind,
            TriggeredAutomatically = context.Automatic,
            Status = context.StopRequested || resumes ? BackupRunStatus.Skipped : BackupRunStatus.Failed,
            Summary = context.StopRequested ? "Stopped before the background worker could save its final result."
                : reason ?? "The background worker stopped unexpectedly. Completion was not confirmed.",
        };
        // The fallback uses the host's stable ID, separate from the worker's
        // history ID. Retrying after a crash replaces only this same fallback.
        logs.AppendRun(record);
        if (!context.StopRequested && !resumes && (context.Toast || context.Sound))
            notifications.Add(new(context.HostRunId, context.InterruptedUtc.Value,
                "Backspace: background backup needs attention", context.BackupName + ": " + record.Summary,
                NotificationSeverity.Failure, context.Toast, context.Sound, context.BackupId, record.Id));
        Confirm(context.HostRunId);
        return record;
    }

    private void Save(Context context)
    {
        Directory.CreateDirectory(_directory);
        ServiceNotificationQueue.WriteRecord(PathOf(context.HostRunId), JsonSerializer.SerializeToUtf8Bytes(context, ServiceRunAuthorization.Json));
    }
    private static void Validate(Context context)
    {
        if (context.Version != 1 || !Guid.TryParseExact(context.HostRunId, "N", out _)
            || string.IsNullOrEmpty(context.BackupId) || context.BackupId.Length > 256
            || context.BackupName is null || context.BackupName.Length > 1024
            || context.StartedUtc.Kind != DateTimeKind.Utc || context.Kind is not (RunKind.Backup or RunKind.Check or RunKind.Prune)
            || context.HistoryRunId is not null && !Guid.TryParseExact(context.HistoryRunId, "N", out _)) throw new InvalidDataException();
    }
}
