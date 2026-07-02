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
/// Backup-editor edit path against the real engine:
///   1. Create + run a backup over source A.
///   2. Edit the backup's source list — drop A, add B.
///   3. Run again. The second run must back up B (not A).
///   4. Restore the latest revision; the restored tree matches B.
///
/// This pins the editor → orchestrator path: a saved edit MUST
/// reach the next run. Coverage gap before this scenario: nothing
/// asserted that an in-place source-list edit actually propagated
/// to the plan registered with Backrest.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E23_EditBackupSourcesPersists : BackupE2EBase
{
    public E23_EditBackupSourcesPersists(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E23-edit-backup-sources-persists";

    [AvaloniaFact] public async Task Run_E23() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var sourceA    = ctx.MakeSource("source-A", seed: 231, targetBytes: 400_000);
        var sourceB    = ctx.MakeSource("source-B", seed: 232, targetBytes: 400_000);
        var destPath   = ctx.MakeLocalDestination("edit-local");
        var restoreDir = ctx.MakeRestoreTarget("restored");

        var dest = new Destination
        {
            Name = "Local — edit target",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destPath,
            Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e23", $"e23-{Guid.NewGuid():N}"),
        };
        var backup = new Backup
        {
            Name = "Docs (E23)",
            SourcePaths = { sourceA },
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

        var rec1 = await RunBackupViaUiAsync(ctx, backup, view, CancellationToken.None);
        Assert.True(rec1.Status is BackupRunStatus.Success or BackupRunStatus.Warning);

        // Edit the backup in place via the ACTUAL BackupEditor VM
        // (not a raw Config.Update mutation). Drives the VM's
        // SourcePaths binding + ValidateForm + CommitToWorking path
        // — the same code path the user's "Save" click flows
        // through. Direct mutation bypassed validation and the
        // editor's per-source normalisation; a regression in
        // either would have slipped past E23 silently.
        var liveBackup = ServiceLocator.Config.Current.Backups.First(x => x.Id == backup.Id);
        var backupClone = System.Text.Json.JsonSerializer.Deserialize<Backup>(
            System.Text.Json.JsonSerializer.Serialize(liveBackup))!;
        Backup? saved;
        using (var editorVm = new BackupEditorViewModel(backupClone, isNew: false))
        {
            editorVm.SourcePaths.Clear();
            editorVm.SourcePaths.Add(sourceB);
            saved = editorVm.Save();
            Assert.NotNull(saved);
            Assert.Single(saved!.SourcePaths);
            Assert.Equal(sourceB, saved.SourcePaths[0]);
        }
        ServiceLocator.Config.Update(cfg =>
        {
            var idx = cfg.Backups.FindIndex(x => x.Id == saved.Id);
            if (idx >= 0) cfg.Backups[idx] = saved;
        });

        // Re-read the now-edited backup so the orchestrator gets the
        // updated SourcePaths. The card's snapshot was already
        // populated from config; pump so the VM refreshes.
        ctx.Driver.Pump();
        var edited = ServiceLocator.Config.Current.Backups.First(x => x.Id == backup.Id);
        Assert.Single(edited.SourcePaths);
        Assert.Equal(sourceB, edited.SourcePaths[0]);

        ctx.Snapshot(host, "after edit",
            "Source list edited in place: A → B. Next run should back up B.");

        var rec2 = await RunBackupViaUiAsync(ctx, edited, view, CancellationToken.None);
        Assert.True(rec2.Status is BackupRunStatus.Success or BackupRunStatus.Warning);

        // The latest revision of sourceB should now be restorable
        // independently. If the edit didn't propagate, sourceB has no
        // plan registered and ListRevisions returns empty.
        var revisions = await ServiceLocator.Revisions.ListRevisionsAsync(
            edited, sourceB, dest, "default", CancellationToken.None);
        Assert.NotEmpty(revisions);

        var outcome = await RestoreEverythingAsync(
            edited, sourceB, dest, "default", restoreDir, CancellationToken.None);
        Assert.Equal(0, outcome.FailedCount);
        AssertBytesEqual(sourceB, restoreDir, ctx);
    }
}
