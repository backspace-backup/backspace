using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// Only a backup creates a repository (owner's logs, 28 November 2026:
/// local_dropbox's cleanups on 16, 17 and 24 November ran restic init on a
/// drive that was not connected, and failed only because no password was
/// given). With the real restic, a check and a cleanup of a destination
/// holding no repository fail with a message saying so, and leave it empty;
/// a backup then creates it as before.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E71_MaintenanceNeverCreatesRepository : BackupE2EBase
{
    public E71_MaintenanceNeverCreatesRepository(ITestOutputHelper output) : base(output) { }
    protected override string ScenarioName => "E71-maintenance-never-creates-repository";
    [AvaloniaFact] public async Task Run_E71() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var folder = ctx.MakeLocalDestination("empty-place");
        var destination = new Destination
        {
            Name = "Empty place", Kind = DestinationKind.LocalFolder, PathOrSubpath = folder, Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e71", Guid.NewGuid().ToString("N")),
        };
        var source = Path.Combine(ctx.SourcesDir, "documents"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "note.txt"), "kept");
        var backup = new Backup
        {
            Name = "Never created by a check", UseVss = false, VssDisabledAcknowledged = true, AbortOnMeteredNetwork = false,
            PruneSchedule = new(), CheckSchedule = new(),
            // Past the "waits for the first backup" gate (U257), as a backup
            // whose repository went missing would be.
            LastSuccessUtc = DateTime.UtcNow.AddDays(-1),
            SourcePaths = { source }, Targets = { new() { DestinationId = destination.Id } },
        };
        ServiceLocator.Config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(destination); });
        var orchestrator = new BackupOrchestrator(ServiceLocator.Config, ServiceLocator.Secrets, fixture.Engine, ServiceLocator.Logs,
            new HealthcheckService(ServiceLocator.Config, ServiceLocator.Secrets),
            new MailService(ServiceLocator.Config, ServiceLocator.Secrets),
            new NotificationService(ServiceLocator.Config), new BackupProgressService());

        var check = await orchestrator.RunCheckAsync(backup, deadline.Token);
        var cleanup = await orchestrator.RunPruneAsync(backup, deadline.Token);
        foreach (var (what, run) in new[] { ("check", check), ("cleanup", cleanup) })
        {
            Assert.Equal(BackupRunStatus.Failed, run.Status);
            Assert.Contains("There is no backup at this destination", Assert.Single(run.Cells).Summary);
            ctx.Report.RecordNote($"The {what} created nothing", Assert.Single(run.Cells).Summary ?? "");
        }
        Assert.False(File.Exists(Path.Combine(folder, "config")), "A check or cleanup created a repository.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));

        // The negative case: a backup still creates its repository.
        Assert.Equal(BackupRunStatus.Success,
            (await fixture.Engine.BackupPruneCheckAsync(backup, source, destination, "default", deadline.Token)).Status);
        Assert.True(File.Exists(Path.Combine(folder, "config")));
        ctx.Report.RecordNote("A backup still creates it", "After the refused check and cleanup, a backup created the repository and succeeded.");
    }
}
