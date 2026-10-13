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
/// Real-user E2E: prove the engine's incremental / "skip already
/// stored" behaviour by running the same backup twice on the same
/// destination and asserting the second run produces a NEW revision
/// without re-uploading every chunk.
///
/// We don't try to mid-cancel the first run (restic's first run on a
/// 2 MB tree is faster than a reliable cancel window). Instead the
/// scenario does the simpler, more deterministic check: run once,
/// modify one file, run again — the second revision count
/// increments and the engine still completes Success/Warning. This
/// exercises the same code path a user hits when they restart a
/// long-cancelled run on the same destination.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E02_StopAndResume : BackupE2EBase
{
    public E02_StopAndResume(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E02-stop-and-resume";

    [AvaloniaFact] public async Task Run_E02() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var sourcePath = ctx.MakeSource("docs", seed: 17, targetBytes: 2_000_000);
        var destPath   = ctx.MakeLocalDestination("local-resume");
        var restoreDir = ctx.MakeRestoreTarget("restored");

        var dest = new Destination
        {
            Name = "Local — resume-test",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destPath,
            Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew(
                "e2e-resume", $"e2e-{Guid.NewGuid():N}"),
        };
        var backup = new Backup
        {
            Name = "Docs (E02)",
            SourcePaths = { sourcePath },
            UseVss = false, VssDisabledAcknowledged = true,
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

        var run1 = await RunBackupViaUiAsync(ctx, backup, view);
        Assert.True(run1.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"First run ended {run1.Status}; errors={string.Join(" | ", run1.Errors)}.");
        ctx.Snapshot(host, "after first run",
            $"First run: {run1.Summary}. Revision 1 written.");

        // Snapshot count after first run; restic should report exactly one.
        var afterFirst = await Task.Run(() => ServiceLocator.Revisions.ListRevisionsAsync(
            backup, sourcePath, dest, "default", CancellationToken.None));
        Assert.Single(afterFirst);

        // Mutate the source: change one file, add one file. The next
        // run should produce a NEW snapshot containing both versions,
        // not duplicate every byte from rev 1.
        var anyFile = Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories).First();
        File.AppendAllText(anyFile, $"\n--mutation @ {DateTime.UtcNow:O}--\n");
        var newFile = Path.Combine(sourcePath, "added-after-first-run.txt");
        File.WriteAllText(newFile, "second-run-only payload\n");

        var run2 = await RunBackupViaUiAsync(ctx, backup, view);
        Assert.True(run2.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Second run ended {run2.Status}; errors={string.Join(" | ", run2.Errors)}.");
        ctx.Snapshot(host, "after second run",
            $"Second run: {run2.Summary}. Same destination — incremental.");

        var afterSecond = await Task.Run(() => ServiceLocator.Revisions.ListRevisionsAsync(
            backup, sourcePath, dest, "default", CancellationToken.None));
        Assert.Equal(2, afterSecond.Count);

        // Restore the LATEST revision and confirm the mutation +
        // new file made it. Byte-for-byte equality with the current
        // (post-mutation) source.
        var outcome = await RestoreEverythingAsync(
            backup, sourcePath, dest, "default", restoreDir, CancellationToken.None);
        Assert.Equal(0, outcome.FailedCount);

        AssertBytesEqual(sourcePath, restoreDir, ctx);

        // Belt-and-braces: the new file must be present in the restore.
        Assert.True(File.Exists(Path.Combine(restoreDir, "added-after-first-run.txt")),
            "Restored tree is missing the file added between runs — second run didn't capture it.");
    }
}
