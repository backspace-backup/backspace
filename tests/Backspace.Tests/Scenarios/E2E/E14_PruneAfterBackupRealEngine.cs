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

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// Inline prune lifecycle against the real Backrest sidecar.
/// <see cref="Backspace.Tests.E2E.BackupLifecycleTests.PruneAfterBackup_Runs_AndLeavesRevisions"/>
/// covers the same shape against <c>InMemoryBackupEngine</c>, which
/// has no concept of a restic-managed forget/prune so it can't catch
/// the surface the user actually hits — the per-repo Flags drift
/// rewrite in <c>EnsurePlanAndRepoCoreAsync</c>'s Phase 2 that fires
/// the moment a backup with PruneSchedule=null (inline) first runs
/// against a destination, plus the Forget RPC the orchestrator
/// invokes inline.
///
/// Shape: run the backup three times against the same destination
/// (each run = one new revision), with PruneSchedule=null (inline
/// prune) and a keep-last-1 retention. The first run creates the
/// repo; the second and third produce additional revisions and
/// inline-prune each time. After the third run we assert that the
/// latest revision is still restorable byte-for-byte — the prune did
/// its job (older revisions discarded per policy) without nuking the
/// live one.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E14_PruneAfterBackupRealEngine : BackupE2EBase
{
    public E14_PruneAfterBackupRealEngine(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E14-prune-after-backup-real-engine";

    [AvaloniaFact] public async Task Run_E14() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var source     = ctx.MakeSource("docs", seed: 141, targetBytes: 400_000);
        var destPath   = ctx.MakeLocalDestination("prune-local");
        var restoreDir = ctx.MakeRestoreTarget("restored");

        var dest = new Destination
        {
            Name = "Local — prune target",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destPath,
            Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e14", $"e14-{Guid.NewGuid():N}"),
        };
        var backup = new Backup
        {
            Name = "Docs (E14)",
            SourcePaths = { source },
            UseVss = false,
            // keep-last-1: every prune pass discards every revision
            // except the freshest. Three runs with this policy land
            // on a single live revision.
            KeepPolicy = "1",
            // PruneSchedule null = inline prune after each backup (focus
            // of this scenario). CheckSchedule sentinel suppresses check.
            CheckSchedule = new BackupSchedule(),
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

        // Three runs; mutate source between them so each run is a
        // real new revision rather than a noop "nothing changed".
        for (int i = 0; i < 3; i++)
        {
            if (i > 0)
            {
                // Append a few bytes to a file that's known to exist —
                // SyntheticFileTree drops at least one `file000.bin`.
                var anyFile = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).First();
                await File.AppendAllTextAsync(anyFile, $"E14-run-{i}\n", CancellationToken.None);
            }
            var record = await RunBackupViaUiAsync(ctx, backup, view, CancellationToken.None);
            Assert.True(record.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
                $"E14 run {i} ended {record.Status}; errors={string.Join(" | ", record.Errors)}.");
        }

        ctx.Snapshot(host, "after 3 runs with prune",
            "Three runs × keep-last-1 → exactly one live revision.");

        // The latest revision must still restore — prune is allowed
        // to discard old revisions, NOT to break the live one.
        var revisions = await ServiceLocator.Revisions.ListRevisionsAsync(
            backup, source, dest, "default", CancellationToken.None);
        Assert.Single(revisions);
        var client = fixture.Sidecar.Client!;
        var config = await client.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>(
            "/v1.Backrest/GetConfig", new BackrestSidecar.EmptyRequest(), CancellationToken.None);
        var repo = Assert.Single(config.Repos);
        var operations = await client.CallAsync<GetOperationsRequest, BackrestOperationList>(
            "/v1.Backrest/GetOperations", new GetOperationsRequest { Selector = new OpSelector { RepoGuid = repo.Guid } }, CancellationToken.None);
        var forgets = operations.Operations!.Where(op => op.OperationForget is not null).ToArray();
        Assert.True(forgets.Length >= 3, "Each of three backup runs must record a real forget operation.");
        Assert.All(forgets, op => Assert.Equal("STATUS_SUCCESS", op.Status));
        ctx.Report.RecordNote("Real retention operations", $"{forgets.Length} successful forget operations; exactly one revision remains and is byte-compared below.");

        var outcome = await RestoreEverythingAsync(
            backup, source, dest, "default", restoreDir, CancellationToken.None);
        Assert.Equal(0, outcome.FailedCount);
        AssertBytesEqual(source, restoreDir, ctx);
    }
}
