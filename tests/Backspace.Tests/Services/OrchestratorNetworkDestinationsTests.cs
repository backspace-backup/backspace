using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U226 (owner, 12 November 2026): "the local destination should at
/// least be processed while waiting". On a metered connection, or without a
/// network, a backup's local destinations run and its network destinations
/// wait, each named so that the schedule runs them once the network allows.
/// A backup with only local destinations never watches the connection.</summary>
public sealed class OrchestratorNetworkDestinationsTests
{
    private static (BackupOrchestrator Orchestrator, ConfigStore Config, InMemoryBackupEngine Engine, int[] Watchdogs) Setup(
        TempWorkspace workspace, Backup backup, params Destination[] destinations)
    {
        var config = new ConfigStore(); config.Load();
        var secrets = new SecretsStore();
        File.WriteAllText(Path.Combine(backup.SourcePaths[0], "fixture.txt"), "network destinations proof");
        config.Update(c =>
        {
            c.Backups.Add(backup); foreach (var d in destinations) c.Destinations.Add(d);
            c.Notifications.OnSuccess = new(); c.Notifications.OnSkipped = new(); c.Notifications.OnFailure = new();
        });
        var engine = new InMemoryBackupEngine();
        var watchdogs = new int[1];
        var orchestrator = new BackupOrchestrator(config, secrets, engine, new LogStore(),
            new HealthcheckService(config, secrets), new MailService(config, secrets), new NotificationService(config), new BackupProgressService())
        {
            MeteredWatchdogFactory = () =>
            {
                Interlocked.Increment(ref watchdogs[0]);
                return new MeteredNetworkWatchdog((_, token) => Task.Delay(Timeout.Infinite, token));
            },
        };
        return (orchestrator, config, engine, watchdogs);
    }

    [Theory]
    [InlineData(true, false, "unmetered", "Waits for an unmetered connection")]
    [InlineData(false, true, "connection", "Waits for a network connection")]
    public async Task On_a_metered_connection_or_offline_the_local_destination_runs_and_the_cloud_waits(bool metered, bool offline, string waitingFor, string summary)
    {
        using var workspace = new TempWorkspace();
        var drive = new Destination { Name = "Drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = workspace.Sub("drive") };
        // Reached by a UNC path, so it needs the network as a share or a cloud
        // would, without an account to sign in to.
        var cloud = new Destination { Name = "Cloud", Kind = DestinationKind.LocalFolder, PathOrSubpath = @"\\?\" + workspace.Sub("cloud") };
        var backup = new Backup { Name = "Mixed", UseVss = false, VssDisabledAcknowledged = true, SourcePaths = { workspace.Sub("source") },
            Targets = { new() { DestinationId = drive.Id }, new() { DestinationId = cloud.Id } } };
        var (orchestrator, _, engine, _) = Setup(workspace, backup, drive, cloud);
        orchestrator.MeteredOverride = () => metered;
        orchestrator.NetworkAvailableOverride = () => !offline;

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var record = await orchestrator.RunBackupAsync(backup, deadline.Token);

        var local = Assert.Single(record.Cells, c => c.DestinationName == "Drive");
        Assert.Equal(BackupRunStatus.Success, local.Status);
        Assert.Null(local.WaitingFor);
        var waiting = Assert.Single(record.Cells, c => c.DestinationName == "Cloud");
        Assert.Equal(BackupRunStatus.Skipped, waiting.Status);
        Assert.Equal(waitingFor, waiting.WaitingFor);
        Assert.Equal(cloud.Id, waiting.DestinationId);
        Assert.Equal(summary, waiting.Summary);
        // The local part was really backed up; the cloud was never touched.
        Assert.NotEmpty(engine.Calls);
        // Not a full success: the cloud part is still to do.
        Assert.Equal(BackupRunStatus.Warning, record.Status);
    }

    [Fact]
    public async Task On_an_unmetered_connection_both_run()
    {
        using var workspace = new TempWorkspace();
        var drive = new Destination { Name = "Drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = workspace.Sub("drive") };
        // Reached by a UNC path, so it needs the network as a share or a cloud
        // would, without an account to sign in to.
        var cloud = new Destination { Name = "Cloud", Kind = DestinationKind.LocalFolder, PathOrSubpath = @"\\?\" + workspace.Sub("cloud") };
        var backup = new Backup { Name = "Mixed", UseVss = false, VssDisabledAcknowledged = true, SourcePaths = { workspace.Sub("source") },
            Targets = { new() { DestinationId = drive.Id }, new() { DestinationId = cloud.Id } } };
        var (orchestrator, _, _, _) = Setup(workspace, backup, drive, cloud);
        orchestrator.MeteredOverride = () => false;
        orchestrator.NetworkAvailableOverride = () => true;
        var record = await orchestrator.RunBackupAsync(backup, CancellationToken.None);
        Assert.All(record.Cells, c => { Assert.True(c.Status == BackupRunStatus.Success, c.DestinationName + ": " + c.Status + " " + c.Summary + " | " + c.LastError); Assert.Null(c.WaitingFor); });
        Assert.Equal(BackupRunStatus.Success, record.Status);
    }

    [Fact]
    public async Task A_backup_to_local_folders_only_runs_on_a_metered_connection_and_never_watches_it()
    {
        using var workspace = new TempWorkspace();
        var drive = new Destination { Name = "Drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = workspace.Sub("drive") };
        var backup = new Backup { Name = "Local", UseVss = false, VssDisabledAcknowledged = true, AbortOnMeteredNetwork = true, WaitForUnmeteredNetwork = true,
            SourcePaths = { workspace.Sub("source") }, Targets = { new() { DestinationId = drive.Id } } };
        var (orchestrator, _, _, watchdogs) = Setup(workspace, backup, drive);
        orchestrator.MeteredOverride = () => true;
        orchestrator.NetworkAvailableOverride = () => false;
        var record = await orchestrator.RunBackupAsync(backup, CancellationToken.None);
        Assert.Equal(BackupRunStatus.Success, record.Status);
        Assert.Equal(0, watchdogs[0]);
    }

    [Fact]
    public void The_waiting_destinations_are_reported_by_their_keys()
    {
        var record = new RunRecord { Kind = RunKind.Backup, Cells =
        {
            new CellOutcome { DestinationName = "Drive", Status = BackupRunStatus.Success },
            new CellOutcome { DestinationName = "Cloud", DestinationId = "c1", StorageName = "Default", Status = BackupRunStatus.Skipped, WaitingFor = "unmetered" },
        } };
        // Outside a schedule run nothing listens; the key is what the schedule keeps.
        ScheduledRuns.ReportWaiting(record);
        Assert.Equal("c1/default", Backup.TargetKey("c1", "Default"));
        var copy = ScheduledRuns.ForTargets(new Backup { Targets = { new() { DestinationId = "d1" }, new() { DestinationId = "c1", StorageName = "Default" } } }, ["c1/default"]);
        Assert.Equal("c1", Assert.Single(copy.Targets).DestinationId);
    }
}
