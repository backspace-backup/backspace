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

public sealed class OrchestratorUnexpectedFailureTests
{
    [Fact]
    public async Task Unexpected_startup_failure_persists_failure_releases_ownership_and_allows_retry()
    {
        using var workspace = new TempWorkspace();
        var config = new ConfigStore(); config.Load();
        var secrets = new SecretsStore();
        var logs = new LogStore();
        var destination = new Destination { Name = "Isolated destination", Kind = DestinationKind.LocalFolder, PathOrSubpath = workspace.Sub("repository") };
        var backup = new Backup
        {
            Name = "Unexpected failure review", AbortOnMeteredNetwork = true, UseVss = false,
            SourcePaths = { workspace.Sub("source") }, Targets = { new() { DestinationId = destination.Id } },
        };
        File.WriteAllText(Path.Combine(backup.SourcePaths[0], "fixture.txt"), "Backup retry must retain these bytes.");
        config.Update(c =>
        {
            c.Backups.Add(backup); c.Destinations.Add(destination);
            c.Notifications.OnSuccess = new(); c.Notifications.OnSkipped = new(); c.Notifications.OnFailure = new();
        });
        var engine = new InMemoryBackupEngine();
        var throwOnStart = true;
        var orchestrator = new BackupOrchestrator(config, secrets, engine, logs,
            new HealthcheckService(config, secrets), new MailService(config, secrets), new NotificationService(config), new BackupProgressService())
        {
            MeteredWatchdogFactory = () => throwOnStart
                ? throw new IOException("The network monitor could not start.")
                : new MeteredNetworkWatchdog((_, token) => Task.Delay(Timeout.Infinite, token)),
        };
        RunRecord? finished = null;
        orchestrator.RunEnded += (_, run) => finished = run;

        await Assert.ThrowsAsync<IOException>(() => orchestrator.RunBackupAsync(backup, default));
        Assert.NotNull(finished);
        Assert.Equal(BackupRunStatus.Failed, finished.Status);
        Assert.NotNull(finished.EndedUtc);
        Assert.Contains("The network monitor could not start.", finished.Errors);
        Assert.Empty(orchestrator.SnapshotRunningBackupIds());
        Assert.Empty(engine.Calls);
        var saved = Assert.Single(logs.LoadHistoryByBackupId(backup.Id));
        Assert.Equal(BackupRunStatus.Failed, saved.Status);
        Assert.Equal(BackupRunStatus.Failed, config.Current.Backups.Single().LastRunStatus);

        throwOnStart = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var retried = await orchestrator.RunBackupAsync(backup, deadline.Token);
        Assert.Equal(BackupRunStatus.Success, retried.Status);
        Assert.Equal(BackupRunStatus.Success, finished!.Status);
        Assert.Empty(orchestrator.SnapshotRunningBackupIds());
        Assert.Equal(2, logs.LoadHistoryByBackupId(backup.Id).Count);
        Assert.Equal(BackupRunStatus.Success, config.Current.Backups.Single().LastRunStatus);
    }
}
