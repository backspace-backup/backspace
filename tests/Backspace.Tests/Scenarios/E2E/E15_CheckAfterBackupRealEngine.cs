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
/// Inline check lifecycle against the real Backrest sidecar.
/// BackupLifecycleTests.CheckAfterBackup_Runs exercises this against
/// the in-memory fake which can't actually run restic check; this
/// scenario hits the real DoRepoTask(TaskCheck) RPC and asserts the
/// backup still reaches Success — the check's exit status is part
/// of the run record.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E15_CheckAfterBackupRealEngine : BackupE2EBase
{
    public E15_CheckAfterBackupRealEngine(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E15-check-after-backup-real-engine";

    [AvaloniaFact] public async Task Run_E15() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var source     = ctx.MakeSource("docs", seed: 151, targetBytes: 500_000);
        var destPath   = ctx.MakeLocalDestination("check-local");
        var restoreDir = ctx.MakeRestoreTarget("restored");

        var dest = new Destination
        {
            Name = "Local — check target",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destPath,
            Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e15", $"e15-{Guid.NewGuid():N}"),
        };
        var backup = new Backup
        {
            Name = "Docs (E15)",
            SourcePaths = { source },
            UseVss = false,
            // PruneSchedule sentinel suppresses inline prune;
            // CheckSchedule null = inline check (the focus of this scenario).
            PruneSchedule = new BackupSchedule(),
            Threads = 2,
            Targets = { new BackupTarget { DestinationId = dest.Id, StorageName = "default" } },
        };
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(dest);
            cfg.Backups.Add(backup);
        });

        var view = new BackupsView { DataContext = new BackupsViewModel() };
        var host = new Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);

        var record = await RunBackupViaUiAsync(ctx, backup, view, CancellationToken.None);
        ctx.Snapshot(host, $"after run {record.Status}",
            $"Backup ran with inline check; status={record.Status}.");

        Assert.True(record.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Backup + inline check ended {record.Status}; errors={string.Join(" | ", record.Errors)}.");

        // Round-trip the latest revision. A check that incorrectly
        // ate the repo would surface here, not just as a wrong
        // status.
        var outcome = await RestoreEverythingAsync(
            backup, source, dest, "default", restoreDir, CancellationToken.None);
        Assert.Equal(0, outcome.FailedCount);
        AssertBytesEqual(source, restoreDir, ctx);
    }
}
