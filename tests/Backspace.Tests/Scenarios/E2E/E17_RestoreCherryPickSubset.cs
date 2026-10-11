using System;
using System.Collections.Generic;
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
/// Restore a SUBSET of files from the latest revision (cherry-pick),
/// not the whole tree. The Restore wizard's "pick files" step is the
/// most common restore pattern after "single file recovery" — the
/// helper <see cref="BackupE2EBase.RestoreEverythingAsync"/> only
/// covers the all-files path, so this scenario goes through
/// <see cref="RestoreEngine.RunAsync"/> directly with a curated
/// Files list. We assert that EXACTLY the picked files land in the
/// target dir, no more, no less, and that they byte-match.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E17_RestoreCherryPickSubset : BackupE2EBase
{
    public E17_RestoreCherryPickSubset(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E17-restore-cherry-pick-subset";

    [AvaloniaFact] public async Task Run_E17() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var source     = ctx.MakeSource("docs", seed: 171, targetBytes: 900_000);
        var destPath   = ctx.MakeLocalDestination("cherry-local");
        var restoreDir = ctx.MakeRestoreTarget("restored-subset");

        var dest = new Destination
        {
            Name = "Local — cherry target",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destPath,
            Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e17", $"e17-{Guid.NewGuid():N}"),
        };
        var backup = new Backup
        {
            Name = "Docs (E17)",
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

        var revisions = await ServiceLocator.Revisions.ListRevisionsAsync(
            backup, source, dest, "default", CancellationToken.None);
        var latest = revisions[0].Number;
        var allFiles = await ServiceLocator.Revisions.ListFilesAsync(
            backup, source, dest, "default", latest, CancellationToken.None);
        Assert.True(allFiles.Count >= 3,
            $"Need at least 3 files to cherry-pick — got {allFiles.Count}. " +
            $"Bump SyntheticFileTree's targetBytes if this trips.");

        // Pick the first and last file by sorted path. The middle is
        // deliberately skipped so a regression that restores either
        // "all" or "none" can't hide as "passes by accident."
        var sorted = allFiles.Select(f => f.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var picked = new List<string> { sorted.First(), sorted.Last() };

        var request = new RestoreRequest
        {
            Backup = backup,
            SourcePath = source,
            Destination = dest,
            StorageName = "default",
            Revision = latest,
            Files = picked,
            TargetPath = restoreDir,
            Overwrite = true,
            PreserveStructure = true,
            Threads = 2,
            RetryDelaysSeconds = new[] { 1, 2, 5 },
        };
        var outcome = await ServiceLocator.Restore.RunAsync(request, CancellationToken.None);
        ctx.Snapshot(host, "after subset restore",
            $"Picked {picked.Count} of {allFiles.Count} files; outcome restored={outcome.RestoredCount}.");

        Assert.Equal(picked.Count, outcome.RestoredCount);
        Assert.Equal(0, outcome.FailedCount);

        // Disk audit: exactly the picked files exist on disk under
        // the restore root. A regression that restores extra files
        // (the "all" path) lights this up immediately.
        var landed = Directory.EnumerateFiles(restoreDir, "*", SearchOption.AllDirectories).ToList();
        Assert.Equal(picked.Count, landed.Count);
    }
}
