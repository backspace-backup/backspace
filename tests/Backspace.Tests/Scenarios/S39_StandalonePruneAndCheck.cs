using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Regression for ROADMAP coverage-matrix gap "Standalone prune /
/// check (cadence)". The inline form (run after each backup) is
/// covered by E14 + E15; the STANDALONE form — independent cadences
/// fired by the OS scheduler — has its own code path through
/// IBackupEngine.RunPruneOnlyAsync / RunCheckOnlyAsync that hasn't
/// been exercised end-to-end.
///
/// This scenario calls each path on a backup with a snapshot, then
/// asserts the RunRecord surfaces a non-error status (Success or
/// Warning — depending on whether the engine had anything to do).
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S39_StandalonePruneAndCheck : ScenarioBase
{
    protected override string ScenarioName => "S39-standalone-prune-and-check";

    [AvaloniaFact] public async Task Run_S39() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var src = ctx.MakeSource("standalone-src", seed: 47, targetBytes: 8_000);
        var dest = new Destination
        {
            Name = "Local — standalone",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("primary"),
        };
        var backup = new Backup { Name = "standalone-backup", Enabled = true };
        backup.SourcePaths.Add(src);
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(backup); });

        // Create a snapshot first so prune / check have something to inspect.
        var backupRecord = await ServiceLocator.Orchestrator.RunBackupAsync(backup, default);
        Assert.True(backupRecord.Status is BackupRunStatus.Success or BackupRunStatus.Warning);

        // -------- Standalone prune --------
        var pruneRecord = await ServiceLocator.Engine.RunPruneOnlyAsync(
            backup, src, dest, "default", default);
        Assert.NotNull(pruneRecord);
        Assert.NotEqual(BackupRunStatus.Failed, pruneRecord.Status);
        ctx.Report.RecordNote("standalone prune",
            $"status={pruneRecord.Status} summary={pruneRecord.Summary}");

        // -------- Standalone check --------
        var checkRecord = await ServiceLocator.Engine.RunCheckOnlyAsync(
            backup, src, dest, "default", default);
        Assert.NotNull(checkRecord);
        Assert.NotEqual(BackupRunStatus.Failed, checkRecord.Status);
        ctx.Report.RecordNote("standalone check",
            $"status={checkRecord.Status} summary={checkRecord.Summary}");
    }
}
