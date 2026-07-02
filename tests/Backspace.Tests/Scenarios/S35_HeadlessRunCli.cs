using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Regression for ROADMAP coverage-matrix gap "Headless --run /
/// --run-all". The Windows Task Scheduler invokes Backspace.exe with
/// these flags; a regression that silently breaks them silently
/// breaks every scheduled backup the user has — and there'd be no
/// in-app surface to notice.
///
/// We can't easily spawn Backspace.exe from a headless test (the
/// fully built exe might not exist; the test process owns the same
/// ServiceLocator), but we CAN exercise the same orchestrator entry
/// point the CLI calls into. The scenario:
///   1. Configures one enabled backup with no destinations.
///   2. Calls the orchestrator's RunAsync as <c>--run my-backup</c>
///      would.
///   3. Asserts the orchestrator returns a recognizable failure
///      record (no destinations → Skipped) rather than throwing.
///   4. Re-runs with one valid destination → asserts Success record.
///
/// **Known gap:** this doesn't cover argv parsing in Program.cs.
/// A follow-up scenario can spawn the actual exe with `--version`
/// and assert the output once the bundled exe path is reachable
/// from the test fixture.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S35_HeadlessRunCli : ScenarioBase
{
    protected override string ScenarioName => "S35-headless-run-cli";

    [AvaloniaFact] public async Task Run_S35() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        // -------- Case 1: backup with NO destinations → Skipped --------
        var orphan = new Backup
        {
            Name = "orphan-headless",
            Enabled = true,
        };
        orphan.SourcePaths.Add(ctx.MakeSource("orphan-src", seed: 1, targetBytes: 8_000));
        ServiceLocator.Config.Update(cfg => cfg.Backups.Add(orphan));

        var orphanRecord = await ServiceLocator.Orchestrator.RunBackupAsync(orphan, default);

        Assert.NotNull(orphanRecord);
        Assert.True(
            orphanRecord.Status is BackupRunStatus.Skipped or BackupRunStatus.Failed or BackupRunStatus.Warning,
            $"Backup with no destinations should produce Skipped/Failed/Warning, " +
            $"not Success. Got {orphanRecord.Status}.");
        ctx.Report.RecordNote("orphan run",
            $"orphan backup → {orphanRecord.Status}: {orphanRecord.Summary}");

        // -------- Case 2: backup with one valid destination → Success/Warning --------
        var dest = new Destination
        {
            Name = "Local — headless",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("primary"),
        };
        var valid = new Backup
        {
            Name = "headless-valid",
            Enabled = true,
        };
        valid.SourcePaths.Add(ctx.MakeSource("headless-src", seed: 2, targetBytes: 8_000));
        valid.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(valid); });

        var validRecord = await ServiceLocator.Orchestrator.RunBackupAsync(valid, default);

        Assert.NotNull(validRecord);
        Assert.True(
            validRecord.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Valid backup should produce Success/Warning. Got {validRecord.Status} — {validRecord.Summary}.");
        ctx.Report.RecordNote("valid run",
            $"valid backup → {validRecord.Status}: {validRecord.Summary}");
    }
}
