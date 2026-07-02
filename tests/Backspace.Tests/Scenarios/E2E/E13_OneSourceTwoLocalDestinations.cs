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
/// One source, TWO local destinations on one backup. Two cells per
/// run; each writes to its own restic repo at its own folder. We
/// assert both cells reach Success and that the data can be
/// restored independently from EITHER destination. Covers the
/// "redundant copy" pattern (one fast local drive + one slower
/// archive drive) that is the most common multi-destination shape
/// after onboarding — and the path E10 doesn't cover at the local
/// layer (E10 is cloud-heavy, local + cloud).
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E13_OneSourceTwoLocalDestinations : BackupE2EBase
{
    public E13_OneSourceTwoLocalDestinations(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E13-one-source-two-local-destinations";

    [AvaloniaFact] public async Task Run_E13() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var source       = ctx.MakeSource("documents", seed: 131, targetBytes: 1_000_000);
        var destAPath    = ctx.MakeLocalDestination("local-a");
        var destBPath    = ctx.MakeLocalDestination("local-b");
        var restoreFromA = ctx.MakeRestoreTarget("restored-from-a");
        var restoreFromB = ctx.MakeRestoreTarget("restored-from-b");

        var destA = new Destination
        {
            Name = "Local A",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destAPath,
            Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e13-a", $"e13-a-{Guid.NewGuid():N}"),
        };
        var destB = new Destination
        {
            Name = "Local B",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destBPath,
            Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e13-b", $"e13-b-{Guid.NewGuid():N}"),
        };
        var backup = new Backup
        {
            Name = "Documents (E13)",
            SourcePaths = { source },
            UseVss = false,
            // Non-null sentinels suppress inline prune/check.
            PruneSchedule = new BackupSchedule(),
            CheckSchedule = new BackupSchedule(),
            Threads = 2,
            Targets =
            {
                new BackupTarget { DestinationId = destA.Id, StorageName = "default" },
                new BackupTarget { DestinationId = destB.Id, StorageName = "default" },
            },
        };
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(destA);
            cfg.Destinations.Add(destB);
            cfg.Backups.Add(backup);
        });

        var view = new BackupsView { DataContext = new BackupsViewModel() };
        var host = new Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);
        ctx.Snapshot(host, "before run", "One source × two local destinations.");

        var record = await RunBackupViaUiAsync(ctx, backup, view, CancellationToken.None);
        ctx.Snapshot(host, $"after run {record.Status}",
            $"RunRecord status={record.Status}, summary={record.Summary}");

        Assert.True(record.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Multi-dest backup ended {record.Status}; errors={string.Join(" | ", record.Errors)}.");

        // Two cells = (1 source × 2 destinations). Both must succeed —
        // the whole point of the redundant-copy pattern is that one
        // destination's failure shouldn't cost you the other's data.
        Assert.Equal(2, record.Cells.Count);
        Assert.All(record.Cells, c => Assert.Equal(BackupRunStatus.Success, c.Status));

        // Restore from EACH destination independently. If a cell's
        // plan/repo was mis-registered (the E12 regression class),
        // one of these restores would fail with "plan not found"
        // even though Status was already green.
        var outcomeA = await RestoreEverythingAsync(
            backup, source, destA, "default", restoreFromA, CancellationToken.None);
        Assert.Equal(0, outcomeA.FailedCount);
        AssertBytesEqual(source, restoreFromA, ctx);

        var outcomeB = await RestoreEverythingAsync(
            backup, source, destB, "default", restoreFromB, CancellationToken.None);
        Assert.Equal(0, outcomeB.FailedCount);
        AssertBytesEqual(source, restoreFromB, ctx);
    }
}
