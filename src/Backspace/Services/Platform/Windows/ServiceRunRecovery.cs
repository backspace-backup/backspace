using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Backspace.Models;

namespace Backspace.Services.Platform.Windows;

/// <summary>Retain the identity of an admitted worker before launching it, so
/// an early process failure or service restart cannot disappear from history.</summary>
internal sealed class ServiceRunRecovery(string directory, LogStore logs, ServiceNotificationQueue notifications)
{
    internal sealed record Context(int Version, string HostRunId, string BackupId, string BackupName,
        RunKind Kind, DateTime StartedUtc, bool Automatic, bool Toast, bool Sound,
        string? HistoryRunId = null, bool StopRequested = false, DateTime? InterruptedUtc = null, bool Complete = false);
    private readonly string _path = Path.Combine(directory, "service-worker-context.json");

    internal Context? Read()
    {
        if (!File.Exists(_path)) return null;
        using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 32 * 1024) throw new InvalidDataException("Invalid retained background-run context.");
        var context = JsonSerializer.Deserialize<Context>(file, ServiceRunAuthorization.Json) ?? throw new InvalidDataException();
        Validate(context); return context;
    }

    internal void Begin(Context context)
    {
        if (Read() is { Complete: false }) throw new IOException("The preceding background outcome still needs to be saved.");
        Validate(context); Save(context);
    }

    internal void TrackHistory(string hostRunId, string historyRunId)
    {
        if (!Guid.TryParseExact(historyRunId, "N", out _)) throw new InvalidDataException();
        var context = Read();
        if (context is null || context.HostRunId != hostRunId || context.Complete || context.HistoryRunId == historyRunId) return;
        if (context.HistoryRunId is not null) throw new InvalidDataException("One worker reported different history identities.");
        Save(context with { HistoryRunId = historyRunId });
    }

    internal void StopRequested(string? hostRunId)
    {
        var context = Read();
        if (context is not null && context.HostRunId == hostRunId && !context.Complete && !context.StopRequested)
            Save(context with { StopRequested = true });
    }

    internal void Confirm(string hostRunId)
    {
        var context = Read();
        if (context is not null && context.HostRunId == hostRunId) Save(context with { Complete = true });
    }

    /// <param name="reason">What the host knows about the failure, such as a
    /// missing destination folder; otherwise the generic interruption text.</param>
    internal RunRecord? Recover(string? hostRunId, string? reason = null)
    {
        var context = Read();
        if (context is null || context.HostRunId != hostRunId) return null;
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
            Status = context.StopRequested ? BackupRunStatus.Skipped : BackupRunStatus.Failed,
            Summary = context.StopRequested ? "Stopped before the background worker could save its final result."
                : reason ?? "The background worker stopped unexpectedly. Completion was not confirmed.",
        };
        // The fallback uses the host's stable ID, separate from the worker's
        // history ID. Retrying after a crash replaces only this same fallback.
        logs.AppendRun(record);
        if (!context.StopRequested && (context.Toast || context.Sound))
            notifications.Add(new(context.HostRunId, context.InterruptedUtc.Value,
                "Backspace: background backup needs attention", context.BackupName + ": " + record.Summary,
                NotificationSeverity.Failure, context.Toast, context.Sound, context.BackupId, record.Id));
        Confirm(context.HostRunId);
        return record;
    }

    private void Save(Context context)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(context, ServiceRunAuthorization.Json);
        using (var file = new FileStream(_path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { file.Write(bytes); file.Flush(true); }
        ServiceNotificationQueue.Publish(_path + ".tmp", _path);
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
