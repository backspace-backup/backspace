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
/// Multi-revision restore: back up twice with the source mutated
/// between runs, then restore the OLDER revision specifically and
/// assert the content matches the pre-mutation state. This is the
/// "I want to go back to yesterday's copy" path the Restore wizard's
/// revision picker drives. E02 covers two runs end-to-end but only
/// ever restores the latest; nothing currently exercises an
/// explicit older-revision restore against the real engine.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E22_RestoreFromOlderRevision : BackupE2EBase
{
    public E22_RestoreFromOlderRevision(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E22-restore-from-older-revision";

    [AvaloniaFact] public async Task Run_E22() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var source     = ctx.MakeSource("docs", seed: 221, targetBytes: 500_000);
        var destPath   = ctx.MakeLocalDestination("older-rev-local");
        var restoreDir = ctx.MakeRestoreTarget("restored-from-old");

        var dest = new Destination
        {
            Name = "Local — older-rev target",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destPath,
            Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e22", $"e22-{Guid.NewGuid():N}"),
        };
        var backup = new Backup
        {
            Name = "Docs (E22)",
            SourcePaths = { source },
            UseVss = false, VssDisabledAcknowledged = true,
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

        // Snapshot the pristine source so we can compare against the
        // older-revision restore later.
        var pristineCopy = ctx.MakeRestoreTarget("pristine-source-copy");
        CopyDir(source, pristineCopy);

        // Run 1 — this is the revision we'll restore later.
        var rec1 = await RunBackupViaUiAsync(ctx, backup, view, CancellationToken.None);
        Assert.True(rec1.Status is BackupRunStatus.Success or BackupRunStatus.Warning);

        // Mutate the source: change every file's contents.
        foreach (var f in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            await File.AppendAllTextAsync(f, "E22-mutation\n", CancellationToken.None);

        // Run 2 — new revision.
        var rec2 = await RunBackupViaUiAsync(ctx, backup, view, CancellationToken.None);
        Assert.True(rec2.Status is BackupRunStatus.Success or BackupRunStatus.Warning);

        // List revisions — newest first per the existing contract.
        var revisions = await ServiceLocator.Revisions.ListRevisionsAsync(
            backup, source, dest, "default", CancellationToken.None);
        Assert.True(revisions.Count >= 2,
            $"Expected at least 2 revisions, found {revisions.Count}. " +
            $"Synthetic source content was supposed to change between runs.");

        // Pick the OLDER revision: highest-index in newest-first ordering.
        var olderRev = revisions.Last().Number;
        var olderFiles = await ServiceLocator.Revisions.ListFilesAsync(
            backup, source, dest, "default", olderRev, CancellationToken.None);

        var request = new RestoreRequest
        {
            Backup = backup,
            SourcePath = source,
            Destination = dest,
            StorageName = "default",
            Revision = olderRev,
            Files = olderFiles.Select(f => f.Path).ToList(),
            TargetPath = restoreDir,
            Overwrite = true,
            PreserveStructure = true,
            Threads = 2,
            RetryDelaysSeconds = new[] { 1, 2, 5 },
        };
        var outcome = await ServiceLocator.Restore.RunAsync(request, CancellationToken.None);
        ctx.Snapshot(host, $"after restore rev #{olderRev}",
            $"Restored {outcome.RestoredCount} files from older revision #{olderRev}.");
        Assert.Equal(0, outcome.FailedCount);

        // The restored tree should match the PRISTINE source, NOT the
        // mutated current state. If a revision-picker bug routed us
        // through the latest revision instead, this fails because the
        // E22-mutation line would be present.
        AssertBytesEqual(pristineCopy, restoreDir, ctx);
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            var dest = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, overwrite: true);
        }
    }
}
