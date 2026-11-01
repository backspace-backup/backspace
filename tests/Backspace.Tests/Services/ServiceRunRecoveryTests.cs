using System;
using System.IO;
using System.Linq;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceRunRecoveryTests
{
    private static ServiceRunRecovery.Context Context() => new(1, Guid.NewGuid().ToString("N"), "b", "Documents",
        RunKind.Backup, DateTime.UtcNow.AddSeconds(-5), true, true, false);

    [Fact]
    public void Early_worker_failure_survives_restart_as_one_failed_run_with_one_exact_alert()
    {
        using var ws = new TempWorkspace(); var logs = new LogStore(ws.Sub("logs")); var queue = new ServiceNotificationQueue(ws.Root);
        var context = Context(); new ServiceRunRecovery(ws.Root, logs, queue).Begin(context);
        var restarted = new ServiceRunRecovery(ws.Root, logs, queue);
        var record = restarted.Recover(context.HostRunId)!;
        Assert.Equal(BackupRunStatus.Failed, record.Status); Assert.Equal(context.HostRunId, record.Id);
        Assert.Equal(context.BackupId, record.BackupId); Assert.True(record.TriggeredAutomatically);
        var claim = queue.Take()!; Assert.Equal(record.Id, claim.Notice.RunId);
        Assert.True(queue.Acknowledge(claim.Notice.Id, claim.Receipt));
        Assert.Equal(record.Id, restarted.Recover(context.HostRunId)!.Id);
        Assert.Single(logs.LoadTimelineByBackupId("b")); Assert.Null(queue.Take());
        Assert.True(restarted.Read()!.Complete);
    }

    [Fact]
    public void A_recorded_worker_success_is_never_replaced_by_an_interruption()
    {
        using var ws = new TempWorkspace(); var logs = new LogStore(ws.Sub("logs")); var queue = new ServiceNotificationQueue(ws.Root);
        var recovery = new ServiceRunRecovery(ws.Root, logs, queue); var context = Context(); recovery.Begin(context);
        var success = new RunRecord { BackupId = "b", BackupName = "Documents", StartedUtc = context.StartedUtc,
            EndedUtc = DateTime.UtcNow, Status = BackupRunStatus.Success, Summary = "Verified successful outcome" };
        logs.AppendRun(success); recovery.TrackHistory(context.HostRunId, success.Id);
        Assert.Equal(success.Id, recovery.Recover(context.HostRunId)!.Id);
        Assert.Equal(BackupRunStatus.Success, logs.LoadTimelineByBackupId("b").Single().Status); Assert.Null(queue.Take());
        // Context confirmation can reach disk before the host status does.
        Assert.Equal(success.Id, new ServiceRunRecovery(ws.Root, logs, queue).Recover(context.HostRunId)!.Id);
    }

    [Fact]
    public void Explicit_Stop_is_recorded_as_skipped_without_an_unexpected_failure_alert()
    {
        using var ws = new TempWorkspace(); var logs = new LogStore(ws.Sub("logs")); var queue = new ServiceNotificationQueue(ws.Root);
        var recovery = new ServiceRunRecovery(ws.Root, logs, queue); var context = Context(); recovery.Begin(context);
        recovery.StopRequested(Guid.NewGuid().ToString("N")); Assert.False(recovery.Read()!.StopRequested);
        recovery.StopRequested(context.HostRunId);
        Assert.Equal(BackupRunStatus.Skipped, recovery.Recover(context.HostRunId)!.Status); Assert.Null(queue.Take());
    }

    [Fact]
    public void Another_run_cannot_replace_an_unresolved_context_or_change_its_history_identity()
    {
        using var ws = new TempWorkspace(); var recovery = new ServiceRunRecovery(ws.Root, new(ws.Sub("logs")), new(ws.Root));
        var context = Context(); recovery.Begin(context);
        Assert.Throws<IOException>(() => recovery.Begin(Context()));
        Assert.Null(recovery.Recover(Guid.NewGuid().ToString("N")));
        recovery.TrackHistory(context.HostRunId, Guid.NewGuid().ToString("N"));
        Assert.Throws<InvalidDataException>(() => recovery.TrackHistory(context.HostRunId, Guid.NewGuid().ToString("N")));
        Assert.Equal(context.HostRunId, recovery.Read()!.HostRunId);
    }

    [Fact]
    public void A_failed_alert_write_keeps_recovery_pending_and_retry_does_not_duplicate_history()
    {
        using var ws = new TempWorkspace(); var logs = new LogStore(ws.Sub("logs")); var queue = new ServiceNotificationQueue(ws.Root);
        var recovery = new ServiceRunRecovery(ws.Root, logs, queue); var context = Context(); recovery.Begin(context);
        var queuePath = Path.Combine(ws.Root, "desktop-notifications.json");
        File.WriteAllText(queuePath, "{\"version\":1,\"pending\":[],\"active\":null}");
        using (var blocked = new FileStream(queuePath, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => recovery.Recover(context.HostRunId));
        Assert.False(recovery.Read()!.Complete); Assert.Single(logs.LoadTimelineByBackupId("b"));
        recovery.Recover(context.HostRunId);
        Assert.True(recovery.Read()!.Complete); Assert.Single(logs.LoadTimelineByBackupId("b"));
        Assert.Equal(context.HostRunId, queue.Take()!.Notice.RunId);
    }

    /// <summary>U051: a scheduled backup to a renamed-away local folder was
    /// recorded as "The background worker stopped unexpectedly".</summary>
    [Fact]
    public void A_missing_destination_folder_is_named_instead_of_an_unexpected_stop()
    {
        using var ws = new TempWorkspace(); var logs = new LogStore(ws.Sub("logs")); var queue = new ServiceNotificationQueue(ws.Root);
        var present = new Destination { Name = "Here", Kind = DestinationKind.LocalFolder, PathOrSubpath = ws.Sub("present") };
        var gone = new Destination { Name = "USB folder", Kind = DestinationKind.LocalFolder, PathOrSubpath = Path.Combine(ws.Root, "renamed-away") };
        var config = new AppConfig(); config.Destinations.Add(present); config.Destinations.Add(gone);
        var backup = new Backup { Id = "b", Name = "Documents" };
        backup.Targets.Add(new BackupTarget { DestinationId = present.Id });
        var run = new ServiceRunAuthorization.AuthorizedRun("r", backup, config, RunKind.Backup);
        Assert.Null(ServiceApprovedWorker.MissingDestination(run));
        backup.Targets.Add(new BackupTarget { DestinationId = gone.Id });
        var reason = ServiceApprovedWorker.MissingDestination(run)!;
        Assert.Contains("«USB folder»", reason); Assert.Contains("renamed-away", reason);

        var recovery = new ServiceRunRecovery(ws.Root, logs, queue); var context = Context(); recovery.Begin(context);
        var record = recovery.Recover(context.HostRunId, reason)!;
        Assert.Equal(BackupRunStatus.Failed, record.Status); Assert.Equal(reason, record.Summary);
        Assert.Contains("is missing", queue.Take()!.Notice.Detail);
    }
}
