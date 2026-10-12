using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class RepositoryQueueAliasTests
{
    [Theory]
    [InlineData("Default", "default")]
    [InlineData("", "default")]
    [InlineData("old.photos", "old-photos")]
    public async Task Alias_waits_for_repository_gate_and_duplicate_aliases_do_not_wait_on_themselves(string first, string alias)
    {
        using var workspace = new TempWorkspace();
        var config = new ConfigStore(); config.Load();
        var secrets = new SecretsStore();
        var destination = new Destination { Name = "Alias queue review", Kind = DestinationKind.LocalFolder, PathOrSubpath = workspace.Sub("repository") };
        Assert.Equal(ResticBackrestEngine.MakeRepoId(destination, first), ResticBackrestEngine.MakeRepoId(destination, alias));
        var backup = new Backup { Name = "Alias queue", UseVss = false, AbortOnMeteredNetwork = false,
            SourcePaths = { workspace.Sub("source") }, Targets = {
                new() { DestinationId = destination.Id, StorageName = first } } };
        File.WriteAllText(Path.Combine(backup.SourcePaths[0], "fixture.txt"), "isolated contents");
        config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(destination);
            c.Notifications.OnSuccess = new(); c.Notifications.OnFailure = new(); c.Notifications.OnSkipped = new(); });
        var engine = new InMemoryBackupEngine();
        var orchestrator = new BackupOrchestrator(config, secrets, engine, new LogStore(),
            new HealthcheckService(config, secrets), new MailService(config, secrets), new NotificationService(config), new BackupProgressService());
        using var gate = new Semaphore(1, 1, BackupOrchestrator.MutexNameForDestination(destination.Id, alias));
        Assert.True(gate.WaitOne(0));
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        orchestrator.RunQueuedReasonChanged += (_, reason) => { if (reason.Length > 0) queued.TrySetResult(); };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = orchestrator.RunBackupAsync(backup, deadline.Token);
        try
        {
            await queued.Task.WaitAsync(deadline.Token);
            Assert.False(run.IsCompleted);
            Assert.Empty(engine.Calls);
        }
        finally { gate.Release(); }
        var result = await run.WaitAsync(deadline.Token);
        Assert.Equal(BackupRunStatus.Success, result.Status);
        Assert.Empty(orchestrator.SnapshotRunningBackupIds());
        config.Update(c => c.Backups[0].Targets.Add(new() { DestinationId = destination.Id, StorageName = alias }));
        var duplicated = await orchestrator.RunBackupAsync(backup, deadline.Token).WaitAsync(deadline.Token);
        Assert.Equal(BackupRunStatus.Success, duplicated.Status);
        Assert.Empty(orchestrator.SnapshotRunningBackupIds());
        Assert.NotEqual(BackupOrchestrator.MutexNameForDestination(destination.Id, alias),
            BackupOrchestrator.MutexNameForDestination(destination.Id, "another-repository"));
    }
}
