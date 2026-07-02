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
/// Regression coverage for the "brand-new config, two sources, one
/// local folder, password OFF" path the user hit on first launch:
///
///   Backup: Backrest's stored config doesn't have the plan / repo
///   Backspace just registered. /v1.Backrest/Backup failed: unknown:
///   get plan "...": plan not found.
///
/// Root cause was a Backrest 1.13 contract mismatch — sending
/// SetConfig with Guid="" + AutoInitialize=true passes SetConfig's
/// per-repo validator but is rejected by the orchestrator-apply
/// step ("guid is required for all repos"), so the daemon's in-
/// memory state never carries our plan and the very next Backup
/// RPC fails. Fix moved new-repo registration to /v1.Backrest/AddRepo
/// which runs `restic init` and stamps the canonical 64-char Guid in
/// a single atomic step (see ResticBackrestEngine.EnsurePlanAndRepoCoreAsync).
///
/// E01 already covers single-source local round-trip with a password,
/// E03 covers two concurrent backups. Neither hit this code path
/// because they each set <c>Encrypted = true</c> with a generated
/// password (which steps around the empty-password restic 0.17+
/// behaviour) and use a single source per backup. This scenario
/// pins down all three differences a fresh-install user actually
/// hits — no password, two sources, fresh config dir — and asserts
/// the run reaches Success.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E12_UnencryptedMultiSourceLocal : BackupE2EBase
{
    public E12_UnencryptedMultiSourceLocal(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E12-unencrypted-multi-source-local";

    [AvaloniaFact] public async Task Run_E12() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var sourceA = ctx.MakeSource("notes",     seed: 71, targetBytes: 600_000);
        var sourceB = ctx.MakeSource("photos",    seed: 72, targetBytes: 800_000);
        var destPath = ctx.MakeLocalDestination("local-no-password");
        var restoreA = ctx.MakeRestoreTarget("restored-notes");
        var restoreB = ctx.MakeRestoreTarget("restored-photos");

        // Encrypted=false: no StoragePasswordRef. The engine routes
        // this as RESTIC_PASSWORD="" + --insecure-no-password on every
        // restic invocation, so init/backup/restore all work on
        // restic 0.17+.
        var dest = new Destination
        {
            Name = "Local — no password",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destPath,
            Encrypted = false,
        };
        var backup = new Backup
        {
            Name = "Two sources (E12)",
            SourcePaths = { sourceA, sourceB },
            UseVss = false,
            // Non-null sentinels suppress inline prune/check.
            PruneSchedule = new BackupSchedule(),
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
        ctx.Snapshot(host, "before run",
            "Brand-new config: two sources, one local destination, password off.");

        var record = await RunBackupViaUiAsync(ctx, backup, view, CancellationToken.None);
        ctx.Snapshot(host, $"after run {record.Status}",
            $"RunRecord status={record.Status}, summary={record.Summary}");

        Assert.True(record.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Backup ended {record.Status}; errors={string.Join(" | ", record.Errors)}. " +
            $"This is the exact failure mode (Backrest 1.13 'plan not found') that " +
            $"the AddRepo fix is meant to prevent — assert NOT regressed.");

        // Both cells should have run independently. Two sources × one
        // destination = 2 cells in RunRecord.Cells; both Success.
        Assert.Equal(2, record.Cells.Count);
        Assert.All(record.Cells, c => Assert.Equal(BackupRunStatus.Success, c.Status));

        // Round-trip both sources individually so a regression in
        // multi-source plan registration surfaces as a restore failure
        // here, not just an orchestrator log smell.
        var outcomeA = await RestoreEverythingAsync(
            backup, sourceA, dest, "default", restoreA, CancellationToken.None);
        Assert.Equal(0, outcomeA.FailedCount);
        AssertBytesEqual(sourceA, restoreA, ctx);

        var outcomeB = await RestoreEverythingAsync(
            backup, sourceB, dest, "default", restoreB, CancellationToken.None);
        Assert.Equal(0, outcomeB.FailedCount);
        AssertBytesEqual(sourceB, restoreB, ctx);
    }
}
