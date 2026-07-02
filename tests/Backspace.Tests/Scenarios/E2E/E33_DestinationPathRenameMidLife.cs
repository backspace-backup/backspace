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
/// Real-world drift scenario: the user backs up to <c>D:\Backups\old</c>,
/// then later renames / moves that folder to <c>D:\Backups\new</c>
/// from the destination editor. The next backup should pick up the
/// new path — Backrest's repo URI must be rewritten via SetConfig
/// drift detection so the daemon points restic at the right tree,
/// and old snapshots stored there should still be enumerable from
/// the new path.
///
/// Shape:
///   1. Create destination D pointing at folderOld; back up + assert.
///   2. Move folderOld → folderNew on disk; update Destination.PathOrSubpath.
///   3. Back up again; assert success.
///   4. ListRevisions should now show TWO snapshots (old + new), both
///      restorable from the new location.
///
/// This pins the SetConfig drift path that
/// <c>EnsurePlanAndRepoCoreAsync</c> implements for destination URI
/// changes — without it, the second backup would either fail (Backrest
/// pointing at the missing old path) or silently create a second repo
/// under the new path, orphaning the first snapshot.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E33_DestinationPathRenameMidLife : BackupE2EBase
{
    public E33_DestinationPathRenameMidLife(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E33-destination-path-rename-mid-life";

    [AvaloniaFact] public async Task Run_E33() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var source     = ctx.MakeSource("docs", seed: 331, targetBytes: 400_000);
        var folderOld  = ctx.MakeLocalDestination("rename-old");
        // Sibling path we'll move the dest folder to mid-test. We
        // don't pre-create it — Directory.Move (used below) creates
        // the destination directory atomically.
        var folderNew  = Path.Combine(Path.GetDirectoryName(folderOld)!, "rename-new");
        var restoreDir = ctx.MakeRestoreTarget("restored");

        var dest = new Destination
        {
            Name = "Local — drift target",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = folderOld,
            Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e33", $"e33-{Guid.NewGuid():N}"),
        };
        var backup = new Backup
        {
            Name = "Docs (E33)",
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

        // 1. First backup against folderOld.
        var rec1 = await RunBackupViaUiAsync(ctx, backup, view, CancellationToken.None);
        Assert.True(rec1.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"E33 first backup ended {rec1.Status}; errors={string.Join(" | ", rec1.Errors)}.");
        ctx.Snapshot(host, "after first backup",
            $"First backup against {folderOld}: {rec1.Summary}");

        // 2. Rename: move the restic repo dir on disk AND drive the
        // ACTUAL DestinationEditor UX path (not a direct
        // Config.Update mutation). The previous shape bypassed the
        // VM's per-kind name-generator logic, path normalisation,
        // and form-validation — so a regression in any of those
        // would have passed E33 silently. Mirroring what the user
        // does (open the editor → type the new path → click Save)
        // exercises the same code that runs in production. The
        // repo's blob layout (snapshots/, data/, etc.) is
        // path-independent, so Directory.Move preserves the
        // underlying restic data; the editor commits the rename
        // intent to config.
        Directory.Move(folderOld, folderNew);
        var liveDest = ServiceLocator.Config.Current.Destinations.First(x => x.Id == dest.Id);
        // Mirror DestinationsViewModel.Edit: VM operates on a clone,
        // production then writes the saved Destination back at the
        // matching index via Config.Update so the on-disk JSON gets
        // an atomic rewrite. Aliasing liveDest directly would short-
        // circuit the rewrite-and-rollback semantic of Config.Update.
        var clone = System.Text.Json.JsonSerializer.Deserialize<Destination>(
            System.Text.Json.JsonSerializer.Serialize(liveDest))!;
        using (var editorVm = new DestinationEditorViewModel(clone, isNew: false))
        {
            editorVm.PathOrSubpath = folderNew;
            var saved = editorVm.Save();
            Assert.NotNull(saved);
            Assert.Equal(folderNew, saved!.PathOrSubpath);
            ServiceLocator.Config.Update(cfg =>
            {
                var idx = cfg.Destinations.FindIndex(x => x.Id == saved.Id);
                if (idx >= 0) cfg.Destinations[idx] = saved;
            });
        }
        ctx.Driver.Pump();
        ctx.Snapshot(host, "after rename",
            $"Destination path edited via DestinationEditorViewModel.Save: {folderOld} → {folderNew}.");

        // 3. Mutate source so the second run is a real new revision.
        var anyFile = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).First();
        await File.AppendAllTextAsync(anyFile, "post-rename change\n");
        var refreshed = ServiceLocator.Config.Current.Backups.First(x => x.Id == backup.Id);
        var refreshedDest = ServiceLocator.Config.Current.Destinations.First(x => x.Id == dest.Id);

        var rec2 = await RunBackupViaUiAsync(ctx, refreshed, view, CancellationToken.None);
        Assert.True(rec2.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"E33 second backup (post-rename) ended {rec2.Status}; errors={string.Join(" | ", rec2.Errors)}.");
        ctx.Snapshot(host, "after second backup",
            $"Second backup against {folderNew}: {rec2.Summary}");

        // 4. Both revisions should be enumerable from the new path.
        // Two backups → two snapshots — the path rename must NOT have
        // forked a second repo or lost the original snapshot.
        var revisions = await ServiceLocator.Revisions.ListRevisionsAsync(
            refreshed, source, refreshedDest, "default", CancellationToken.None);
        Assert.True(revisions.Count >= 2,
            $"Expected at least 2 revisions after rename round-trip; got {revisions.Count}. " +
            "If only 1 is present, the drift detection forked a fresh repo at the new path " +
            "and orphaned the first snapshot.");

        // Latest revision should restore byte-equivalently.
        var outcome = await RestoreEverythingAsync(
            refreshed, source, refreshedDest, "default", restoreDir, CancellationToken.None);
        Assert.Equal(0, outcome.FailedCount);
        AssertBytesEqual(source, restoreDir, ctx);
    }
}
