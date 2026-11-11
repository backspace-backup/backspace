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
/// A check that runs after each backup, against the real engine. Checks are
/// entries of their own (U179): the backup (restic run directly, U175) ends,
/// then the orchestrator runs the structure check as its own run, with restic
/// run directly too (step 2). The backup runs no check itself.
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
            UseVss = false, VssDisabledAcknowledged = true,
            // PruneSchedule sentinel suppresses inline prune; the structure
            // check runs after each backup (the focus of this scenario).
            PruneSchedule = new BackupSchedule(),
            Checks = { new BackupCheck { Kind = CheckKind.Structure, Schedule = null } },
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

        // The backup runs no check itself; its check runs right after it.
        var direct = new System.Collections.Concurrent.ConcurrentQueue<(ResticCommand Command, ResticResult Result)>();
        fixture.Engine.ResticCommandFinished += (command, result) => direct.Enqueue((command, result));
        var record = await RunBackupViaUiAsync(ctx, backup, view, CancellationToken.None);
        ctx.Snapshot(host, $"after run {record.Status}",
            $"Backup ran, then its check after each backup; status={record.Status}.");

        Assert.Equal(BackupRunStatus.Success, record.Status);
        Assert.Empty(record.Checks);
        Assert.Contains(direct, c => c.Command.Arguments[0] == "backup");
        var check = Assert.Single(direct, c => c.Command.Arguments.SequenceEqual(["check"]));
        Assert.Equal(0, check.Result.ExitCode);
        // The check's own run, right after the backup.
        var checkRun = Assert.Single(ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id), r => r.Kind == RunKind.Check);
        Assert.Equal(backup.Checks[0].Id, checkRun.CheckId);
        Assert.True(checkRun.Status == BackupRunStatus.Success, checkRun.Summary);
        Assert.True(checkRun.StartedUtc >= record.EndedUtc!.Value.AddSeconds(-1), "The check ran after the backup.");
        var saved = Assert.Single(checkRun.Checks);
        Assert.Equal(BackupRunStatus.Success, saved.Status);
        Assert.Equal(CheckDepth.RepositoryStructure, saved.Depth);
        Assert.True(direct.ToArray().ToList().FindIndex(c => c.Command.Arguments[0] == "check")
            > direct.ToArray().ToList().FindIndex(c => c.Command.Arguments[0] == "backup"), "The check ran after the backup.");
        ctx.Report.RecordNote("Real integrity check", $"The backup ran restic directly and no check; then its structure check ran as its own run ({checkRun.Summary}): restic check, exit code 0 in {check.Result.Elapsed.TotalSeconds:0.0} s.");

        // Round-trip the latest revision. A check that incorrectly
        // ate the repo would surface here, not just as a wrong
        // status.
        var outcome = await RestoreEverythingAsync(
            backup, source, dest, "default", restoreDir, CancellationToken.None);
        Assert.Equal(0, outcome.FailedCount);
        AssertBytesEqual(source, restoreDir, ctx);
    }
}
