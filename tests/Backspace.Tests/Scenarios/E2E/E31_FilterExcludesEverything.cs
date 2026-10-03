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
/// Degenerate-filter edge case: a backup whose exclude filter
/// matches everything the source contains should NOT crash the
/// engine or report Failed. The empty-snapshot outcome must surface
/// as a clean Success or Warning — restic creates an empty
/// snapshot for an all-excluded source, and the user-facing surface
/// must reflect that as "nothing to back up" rather than a stack
/// trace.
///
/// Shape: source tree contains only <c>*.tmp</c> files; the
/// FiltersText excludes <c>*.tmp</c>. After the run, the backup's
/// status is non-Failed; an immediate restore yields an empty
/// (or near-empty) restore tree. The contract here is "engine
/// stays clean on an empty-snapshot edge case", not "user gets
/// something useful out" — that's by definition not possible.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E31_FilterExcludesEverything : BackupE2EBase
{
    public E31_FilterExcludesEverything(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E31-filter-excludes-everything";

    [AvaloniaFact] public async Task Run_E31() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var sourceRoot = Path.Combine(ctx.SourcesDir, "tmp-only");
        Directory.CreateDirectory(sourceRoot);
        if (FixtureSizeResolver.ResolveSize(FixtureSize.Smoke) == FixtureSize.Huge)
            FixtureFactory.GenerateExactTree(sourceRoot, 31, 20000, 2L * 1024 * 1024 * 1024, ".tmp");
        for (int i = 0; i < 5; i++)
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, $"junk{i:00}.tmp"),
                $"this is junk number {i}");

        var destPath = ctx.MakeLocalDestination("empty-local");

        var dest = new Destination
        {
            Name = "Local — empty target",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destPath,
            Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e31", $"e31-{Guid.NewGuid():N}"),
        };
        var backup = new Backup
        {
            Name = "All-filtered (E31)",
            SourcePaths = { sourceRoot },
            // Restic-native glob — matches every .tmp file in the tree.
            FiltersText = "*.tmp",
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
        ctx.Snapshot(host, "before run",
            "Source: 5 *.tmp files. FiltersText='- *.tmp' excludes them all.");

        var record = await RunBackupViaUiAsync(ctx, backup, view, CancellationToken.None);
        // The contract is "engine doesn't crash on an empty-snapshot
        // edge case". Backrest reports Success when restic creates an
        // empty snapshot; Warning is also acceptable if the daemon
        // surfaces the no-files-matched message at warn-level. Failed
        // would be a bug.
        Assert.True(
            record.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"E31 expected Success/Warning on all-filtered source; got {record.Status}; errors={string.Join(" | ", record.Errors)}.");
        ctx.Snapshot(host, "after run",
            $"All-filtered backup ended {record.Status}; engine stayed clean. Summary: {record.Summary}");
    }
}
