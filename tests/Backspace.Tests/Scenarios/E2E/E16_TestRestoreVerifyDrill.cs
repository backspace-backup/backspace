using System;
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
/// Test-restore (verify drill) end-to-end against the real engine.
/// The drill samples files from the latest revision, restores them
/// to a scratch dir, and byte-compares against the source. This is
/// the action behind the BackupCard's "Test restore" button — its
/// failure shape (e.g. "all 7 sampled files matched" vs "3/7
/// mismatched") is what tells the user whether their backup is
/// actually intact, separate from "did restic say it was fine."
///
/// The drill bypasses the orchestrator's per-cell loop and uses
/// <see cref="BackupVerifier.VerifyAsync"/> directly — same path
/// <see cref="BackupsViewModel.TestRestoreCommand"/> triggers.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E16_TestRestoreVerifyDrill : BackupE2EBase
{
    public E16_TestRestoreVerifyDrill(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E16-test-restore-verify-drill";

    [AvaloniaFact] public async Task Run_E16() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var source   = ctx.MakeSource("docs", seed: 161, targetBytes: 700_000);
        var destPath = ctx.MakeLocalDestination("verify-local");

        var dest = new Destination
        {
            Name = "Local — verify target",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destPath,
            Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e16", $"e16-{Guid.NewGuid():N}"),
        };
        var backup = new Backup
        {
            Name = "Docs (E16)",
            SourcePaths = { source },
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

        var record = await RunBackupViaUiAsync(ctx, backup, view, CancellationToken.None);
        Assert.True(record.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Backup ended {record.Status}; errors={string.Join(" | ", record.Errors)}.");

        // Now drive the verify drill — the exact path the "Test restore"
        // button kicks off. Source hasn't changed since the backup, so
        // every sampled file must byte-compare clean.
        var verify = await ServiceLocator.Verifier.VerifyAsync(backup, CancellationToken.None);
        ctx.Snapshot(host, $"after verify {(verify.OverallPass ? "pass" : "fail")}",
            $"Verify drill — {verify.FilesOk}/{verify.FilesPicked} ok, " +
            $"summary={verify.Summary}");

        Assert.True(verify.OverallPass,
            $"Verify drill returned {verify.Summary}; per-file: " +
            string.Join(" | ", verify.Files.Select(f => $"{f.Path}:{f.Status}{(f.Detail is null ? "" : "/" + f.Detail)}")));
        Assert.True(verify.FilesPicked > 0,
            "Verifier picked zero files — the drill is supposed to sample at least one.");
        Assert.Equal(0, verify.FilesFailed);
    }
}
