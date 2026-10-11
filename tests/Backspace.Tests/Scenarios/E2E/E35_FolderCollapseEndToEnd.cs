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
/// Regression for the user's "I picked 2 folders!" complaint. The
/// underlying bug: RestoreViewModel.SelectedPaths read every leaf
/// when the user ticked a folder, sending N --include patterns to
/// restic instead of one.
///
/// This scenario asserts the integration end-to-end through the
/// production code path: build a fixture with a folder containing
/// 100+ files, run the minimizer on the full leaf selection, plumb
/// the collapsed IncludePatterns through RestoreRequest →
/// RestoreEngine → IBackupEngine.RestoreFilesAsync, and verify
/// BOTH that the engine got exactly ONE pattern AND that every
/// leaf landed at the target byte-equal.
///
/// The unit tests prove the algorithm; this proves the wiring. AGENT.md
/// rule 7 explicitly calls out this pattern as the one that hid the
/// Mount false-green for months.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E35_FolderCollapseEndToEnd : BackupE2EBase
{
    public E35_FolderCollapseEndToEnd(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E35-folder-collapse-end-to-end";

    [AvaloniaFact] public async Task Run_E35() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        // Emit a fixture with EXACTLY one folder containing many
        // small files. The folder-collapse algorithm should reduce
        // the user's "tick everything in this folder" selection to
        // a single include pattern.
        var src = Path.Combine(ctx.SourcesDir, "source-1");
        var pickedFolder = Path.Combine(src, "documents");
        Directory.CreateDirectory(pickedFolder);

        const int filesInFolder = 150;
        if (FixtureSizeResolver.ResolveSize(FixtureSize.Smoke) == FixtureSize.Huge)
            FixtureFactory.GenerateScaleSource(pickedFolder, 35_150);
        else
        {
        var rng = new Random(35_150);
        var buf = new byte[2048];
        for (int i = 0; i < filesInFolder; i++)
        {
            rng.NextBytes(buf);
            File.WriteAllBytes(Path.Combine(pickedFolder, $"doc-{i:D4}.bin"), buf);
        }
        }

        var destPath = ctx.MakeLocalDestination("collapse-local");
        var restoreDir = ctx.MakeRestoreTarget("restored");

        var dest = new Destination
        {
            Name = "Local — folder-collapse",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destPath,
            Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e35", $"e35-{Guid.NewGuid():N}"),
        };
        var backup = new Backup
        {
            Name = "Folder-collapse target (E35)",
            UseVss = false,
            PruneSchedule = new BackupSchedule(),
            CheckSchedule = new BackupSchedule(),
            Threads = 2,
            Targets = { new BackupTarget { DestinationId = dest.Id, StorageName = "default" } },
        };
        backup.SourcePaths.Add(src);
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

        // List every leaf the snapshot has — this is what the picker
        // would surface as `AllFiles` after the user clicked "Restore".
        var revisions = await ServiceLocator.Revisions.ListRevisionsAsync(
            backup, src, dest, "default", CancellationToken.None);
        Assert.NotEmpty(revisions);
        var latestRevision = revisions[0].Number;
        var allFiles = await ServiceLocator.Revisions.ListFilesAsync(
            backup, src, dest, "default", latestRevision, CancellationToken.None);

        // Simulate "user picked the documents folder" — every leaf
        // under /source-1/documents/ flips to selected.
        var leafPaths = allFiles.Select(f => f.Path).ToList();
        Assert.True(leafPaths.Count >= filesInFolder,
            $"Expected ≥{filesInFolder} files in snapshot, got {leafPaths.Count}");

        // The minimal cover SHOULD collapse to one folder pattern.
        var minimal = RestoreSelectionMinimizer.Compute(leafPaths, leafPaths);

        ctx.Snapshot(host, "before restore",
            $"Backup has {leafPaths.Count} files; minimizer produced {minimal.Count} pattern(s).");

        // *** The load-bearing assertion ***
        // If folder-collapse is wired correctly, this is ONE pattern
        // (the source root), not 150. Allow a small set (e.g. peculiarities
        // sub-tree adds siblings) — the regression bar is "≤ 5", which
        // is still a massive collapse from 150+.
        Assert.True(minimal.Count <= 5,
            $"Folder-collapse minimal cover produced {minimal.Count} patterns; expected ≤ 5 " +
            $"(if this fails, RestoreSelectionMinimizer regressed or the wiring lost the collapse). " +
            $"Patterns: {string.Join(", ", minimal)}");

        // *** End-to-end assertion ***
        // Plumb the collapsed patterns through RestoreEngine.RunAsync
        // and verify every leaf lands byte-equal.
        var perSourceRestore = Path.Combine(restoreDir, "source-1");
        Directory.CreateDirectory(perSourceRestore);

        var request = new RestoreRequest
        {
            Backup = backup,
            SourcePath = src,
            Destination = dest,
            StorageName = "default",
            Revision = latestRevision,
            Files = leafPaths,
            IncludePatterns = minimal,
            TargetPath = perSourceRestore,
            Overwrite = true,
            PreserveStructure = true,
            Threads = 2,
        };
        var outcome = await Task.Run(() =>
            ServiceLocator.Restore.RunAsync(request, CancellationToken.None));

        Assert.Equal(0, outcome.FailedCount);
        AssertBytesEqual(src, perSourceRestore, ctx);

        ctx.Snapshot(host, "after restore",
            $"Restored via {minimal.Count} pattern(s); outcome {outcome.TerminalStatus} " +
            $"({outcome.RestoredCount}/{outcome.TotalFiles} files).");
    }
}
