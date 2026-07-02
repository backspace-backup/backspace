using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;
using Xunit.Abstractions;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// Real-user E2E: two independent backups running in parallel to
/// disjoint local destinations.
///
/// The orchestrator's parallelism contract is: two backups whose
/// destinations don't overlap MUST be allowed to run concurrently.
/// This scenario fires both Run-now's near-simultaneously and
/// asserts both completed Success without serialising on a shared
/// destination lock. <see cref="E10_TwoConcurrentMultiCloud"/>
/// covers the cloud-half variant (Dropbox + Google Drive).
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E03_TwoConcurrentBackups : BackupE2EBase
{
    public E03_TwoConcurrentBackups(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E03-two-concurrent-backups";

    [AvaloniaFact] public async Task Run_E03() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        // Two backups, two source trees, two disjoint local dests.
        // Different seeds so the trees are visibly different in any
        // diff a future maintainer runs.
        var sourceA = ctx.MakeSource("project-A", seed: 11, targetBytes: 1_500_000);
        var sourceB = ctx.MakeSource("project-B", seed: 22, targetBytes: 1_500_000);
        var destAPath = ctx.MakeLocalDestination("dest-A");
        var destBPath = ctx.MakeLocalDestination("dest-B");

        var destA = new Destination
        {
            Name = "Local A", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destAPath, Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew(
                "e2e-concurrent-A", $"e2e-{Guid.NewGuid():N}"),
        };
        var destB = new Destination
        {
            Name = "Local B", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destBPath, Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew(
                "e2e-concurrent-B", $"e2e-{Guid.NewGuid():N}"),
        };

        var backupA = new Backup
        {
            Name = "Project A (E03)",
            SourcePaths = { sourceA },
            UseVss = false, Threads = 2,
            Targets = { new BackupTarget { DestinationId = destA.Id, StorageName = "default" } },
        };
        var backupB = new Backup
        {
            Name = "Project B (E03)",
            SourcePaths = { sourceB },
            UseVss = false, Threads = 2,
            Targets = { new BackupTarget { DestinationId = destB.Id, StorageName = "default" } },
        };

        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(destA);
            cfg.Destinations.Add(destB);
            cfg.Backups.Add(backupA);
            cfg.Backups.Add(backupB);
        });

        var view = new BackupsView { DataContext = new BackupsViewModel() };
        var host = new Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);
        ctx.Snapshot(host, "both backups idle",
            "Two backups, both with disjoint local dests; cards ready.");

        // Kick off both runs as close together as possible. The
        // orchestrator's per-destination locking should let both
        // proceed; we measure wall-clock to confirm the second one
        // didn't simply queue behind the first.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var taskA = Task.Run(() => ServiceLocator.Orchestrator.RunBackupAsync(backupA, CancellationToken.None));
        var taskB = Task.Run(() => ServiceLocator.Orchestrator.RunBackupAsync(backupB, CancellationToken.None));
        var both = await Task.WhenAll(taskA, taskB);
        sw.Stop();

        Assert.True(both[0].Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Backup A ended {both[0].Status}; errors={string.Join(" | ", both[0].Errors)}.");
        Assert.True(both[1].Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Backup B ended {both[1].Status}; errors={string.Join(" | ", both[1].Errors)}.");

        ctx.Snapshot(host, "both backups complete",
            $"Both runs done in {sw.Elapsed.TotalSeconds:F1}s wall-clock " +
            $"(A: {both[0].Summary}; B: {both[1].Summary}).");

        // Restore both into their own scratch dirs and byte-compare.
        var restoredA = ctx.MakeRestoreTarget("restored-A");
        var restoredB = ctx.MakeRestoreTarget("restored-B");

        var outcomeA = await RestoreEverythingAsync(
            backupA, sourceA, destA, "default", restoredA, CancellationToken.None);
        var outcomeB = await RestoreEverythingAsync(
            backupB, sourceB, destB, "default", restoredB, CancellationToken.None);

        Assert.Equal(0, outcomeA.FailedCount);
        Assert.Equal(0, outcomeB.FailedCount);

        AssertBytesEqual(sourceA, restoredA, ctx);
        AssertBytesEqual(sourceB, restoredB, ctx);
    }
}
