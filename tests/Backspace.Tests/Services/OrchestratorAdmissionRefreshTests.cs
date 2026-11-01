using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class OrchestratorAdmissionRefreshTests
{
    [Theory]
    [InlineData(RunKind.Backup, false, false)]
    [InlineData(RunKind.Prune, false, false)]
    [InlineData(RunKind.Check, false, false)]
    [InlineData(RunKind.Backup, true, false)]
    [InlineData(RunKind.Prune, true, false)]
    [InlineData(RunKind.Check, true, false)]
    [InlineData(RunKind.Backup, false, true)]
    [InlineData(RunKind.Prune, false, true)]
    [InlineData(RunKind.Check, false, true)]
    [InlineData(RunKind.Backup, true, true)]
    [InlineData(RunKind.Prune, true, true)]
    [InlineData(RunKind.Check, true, true)]
    public async Task Waiting_process_reloads_removal_before_it_can_touch_an_old_repository(RunKind kind, bool removeWholeBackup, bool sameStore)
    {
        using var workspace = new TempWorkspace();
        var scheduledConfig = new ConfigStore(); scheduledConfig.Load();
        var secrets = new SecretsStore(); var logs = new LogStore();
        var destination = new Destination { Name = "Removed repository", Kind = DestinationKind.LocalFolder, PathOrSubpath = workspace.Sub("repository") };
        var backup = new Backup { Name = "Waiting scheduled run", UseVss = false, VssDisabledAcknowledged = true, AbortOnMeteredNetwork = false,
            SourcePaths = { workspace.Sub("source") }, Targets = { new() { DestinationId = destination.Id } } };
        scheduledConfig.Update(c => { c.Backups.Add(backup); c.Destinations.Add(destination);
            c.Notifications.OnSuccess = new(); c.Notifications.OnFailure = new(); c.Notifications.OnSkipped = new(); });
        // Independent store instances reproduce a GUI and a scheduled process
        // without letting either instance's memory update the other.
        var removingConfig = sameStore ? scheduledConfig : new ConfigStore();
        if (!sameStore) removingConfig.Load();
        var engine = new InMemoryBackupEngine();
        var orchestrator = new BackupOrchestrator(scheduledConfig, secrets, engine, logs,
            new HealthcheckService(scheduledConfig, secrets), new MailService(scheduledConfig, secrets),
            new NotificationService(scheduledConfig), new BackupProgressService());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var removalLease = await ServiceLocator.Maintenance.AcquireAsync("isolated removal transaction", ct: deadline.Token);
        var waiting = kind switch
        {
            RunKind.Prune => orchestrator.RunPruneAsync(backup, deadline.Token),
            RunKind.Check => orchestrator.RunCheckAsync(backup, deadline.Token),
            _ => orchestrator.RunBackupAsync(backup, deadline.Token),
        };
        Assert.False(waiting.IsCompleted);
        removingConfig.Update(c =>
        {
            if (removeWholeBackup) c.Backups.RemoveAll(b => b.Id == backup.Id);
            else
            {
                c.Destinations.RemoveAll(d => d.Id == destination.Id);
                c.Backups.ForEach(b => b.Targets.RemoveAll(t => t.DestinationId == destination.Id));
            }
        });
        if (!sameStore) Assert.Single(scheduledConfig.Current.Destinations);
        removalLease.Dispose();
        var result = await waiting.WaitAsync(deadline.Token);

        Assert.Empty(engine.Calls);
        Assert.NotEqual(BackupRunStatus.Success, result.Status);
        if (removeWholeBackup)
        {
            Assert.Equal(BackupRunStatus.Skipped, result.Status);
            Assert.Contains("removed", result.Summary);
            Assert.Empty(scheduledConfig.Current.Backups);
            Assert.Single(scheduledConfig.Current.Destinations);
        }
        else Assert.Empty(scheduledConfig.Current.Destinations);
        Assert.Empty(orchestrator.SnapshotRunningBackupIds());
    }

    [Theory]
    [InlineData(RunKind.Backup)]
    [InlineData(RunKind.Prune)]
    [InlineData(RunKind.Check)]
    public async Task Corrupt_settings_after_initial_load_report_failure_without_running_or_overwriting_them(RunKind kind)
    {
        using var workspace = new TempWorkspace();
        var config = new ConfigStore(); config.Load();
        var secrets = new SecretsStore(); var logs = new LogStore();
        var destination = new Destination { Name = "Unchanged repository", Kind = DestinationKind.LocalFolder, PathOrSubpath = workspace.Sub("repository") };
        var backup = new Backup { Name = "Unreadable settings", UseVss = false, VssDisabledAcknowledged = true, AbortOnMeteredNetwork = false,
            SourcePaths = { workspace.Sub("source") }, Targets = { new() { DestinationId = destination.Id } } };
        config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(destination);
            c.Notifications.OnSuccess = new(); c.Notifications.OnFailure = new(); c.Notifications.OnSkipped = new(); });
        var engine = new InMemoryBackupEngine();
        var orchestrator = new BackupOrchestrator(config, secrets, engine, logs,
            new HealthcheckService(config, secrets), new MailService(config, secrets), new NotificationService(config), new BackupProgressService());
        const string damaged = "{broken settings preserved for recovery";
        File.WriteAllText(AppPaths.ConfigFile, damaged);
        var result = kind switch
        {
            RunKind.Prune => await orchestrator.RunPruneAsync(backup, default),
            RunKind.Check => await orchestrator.RunCheckAsync(backup, default),
            _ => await orchestrator.RunBackupAsync(backup, default),
        };
        Assert.Equal(BackupRunStatus.Failed, result.Status);
        Assert.Contains("No repository work", result.Summary);
        Assert.Empty(engine.Calls);
        var saved = Assert.Single(logs.LoadHistoryByBackupId(backup.Id));
        Assert.Equal(BackupRunStatus.Failed, saved.Status);
        Assert.Equal(kind, saved.Kind);
        Assert.Equal(damaged, File.ReadAllText(AppPaths.ConfigFile));
        Assert.Empty(orchestrator.SnapshotRunningBackupIds());
    }
}
