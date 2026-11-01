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
/// Custom exclude-filter against the real engine: a backup with a
/// non-default <c>FiltersText</c> must propagate the negative globs
/// into the Backrest plan's <c>Excludes</c> field AND the resulting
/// restic invocation must actually skip the excluded files. Both
/// halves of the contract matter: the filter could round-trip into
/// config but be applied at the wrong layer; or the filter could
/// apply but not survive the SetConfig drift check, replicating on
/// every run.
///
/// Shape: build a source tree with a deliberately-mixed payload —
/// a <c>keep/</c> subdir (should land in the snapshot) and a
/// <c>skip/</c> subdir (should be filtered out). Backup → Restore
/// → assert <c>keep/</c> round-trips byte-for-byte and <c>skip/</c>
/// is absent from the restore tree.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E30_CustomFilter : BackupE2EBase
{
    public E30_CustomFilter(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E30-custom-filter";

    [AvaloniaFact] public async Task Run_E30() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        // The excluded name is deliberately also an ancestor. Only matching
        // children of this selected source may be excluded; brackets are literal.
        var sourceRoot = Path.Combine(ctx.SourcesDir, "skip", "tree[1]");
        var keepDir    = Path.Combine(sourceRoot, "keep");
        var skipDir    = Path.Combine(sourceRoot, "skip");
        Directory.CreateDirectory(keepDir);
        Directory.CreateDirectory(skipDir);
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "root-only.txt"), "exclude only at source root");
        await File.WriteAllTextAsync(Path.Combine(keepDir, "root-only.txt"), "keep this nested namesake");
        if (FixtureSizeResolver.ResolveSize(FixtureSize.Smoke) == FixtureSize.Huge)
        {
            FixtureFactory.GenerateExactTree(keepDir, 30, 10000, 1024L * 1024 * 1024);
            FixtureFactory.GenerateExactTree(skipDir, 31, 10000, 1024L * 1024 * 1024);
        }

        // 5 files per subdir; bytes intentionally distinct so a
        // restore-side equality check is meaningful.
        for (int i = 0; i < 5; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(keepDir, $"k{i:00}.txt"),
                $"keep payload {i} {Guid.NewGuid():N}");
            await File.WriteAllTextAsync(Path.Combine(skipDir, $"s{i:00}.txt"),
                $"skip payload {i} {Guid.NewGuid():N}");
        }

        var destPath   = ctx.MakeLocalDestination("filter-local");
        var restoreDir = ctx.MakeRestoreTarget("restored");

        var dest = new Destination
        {
            Name = "Local — filter target",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destPath,
            Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e30", $"e30-{Guid.NewGuid():N}"),
        };
        var backup = new Backup
        {
            Name = "Filtered docs (E30)",
            SourcePaths = { sourceRoot },
            // Restic-native filter: one bare glob per line. "skip" is
            // unanchored so restic --exclude treats it as "directory
            // or file named skip, anywhere in the tree".
            FiltersText = "skip\n/root-only.txt",
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

        var preview = new System.Collections.Generic.List<SimulatedEntry>();
        await new FilterSimulator().SimulateAsync(backup, preview.Add, CancellationToken.None);
        Assert.Contains(preview, e => e.Included && e.Path.EndsWith("k00.txt"));
        Assert.Contains(preview, e => !e.Included && e.MatchingRule == "/root-only.txt");

        var view = new BackupsView { DataContext = new BackupsViewModel() };
        var host = new Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);
        ctx.Snapshot(host, "before run",
            "The source itself is beneath skip/. Exclude its skip/ child and root-only.txt at its root; retain the nested namesake.");

        var record = await RunBackupViaUiAsync(ctx, backup, view, CancellationToken.None);
        Assert.True(record.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"E30 backup ended {record.Status}; errors={string.Join(" | ", record.Errors)}.");

        ctx.Snapshot(host, "after run",
            $"Backup ran. Summary: {record.Summary}");

        var outcome = await RestoreEverythingAsync(
            backup, sourceRoot, dest, "default", restoreDir, CancellationToken.None);
        Assert.Equal(0, outcome.FailedCount);

        // keep/ files must all be present and byte-identical.
        for (int i = 0; i < 5; i++)
        {
            var rel = Path.Combine("keep", $"k{i:00}.txt");
            var restored = Path.Combine(restoreDir, rel);
            Assert.True(File.Exists(restored), $"keep file missing after restore: {rel}");
            Assert.Equal(
                await File.ReadAllTextAsync(Path.Combine(keepDir, $"k{i:00}.txt")),
                await File.ReadAllTextAsync(restored));
        }

        // skip/ subtree must be absent from the restore tree. The
        // filter is the only thing keeping it out — without it the
        // restic backup would have grabbed everything.
        var restoredSkipDir = Path.Combine(restoreDir, "skip");
        Assert.False(Directory.Exists(restoredSkipDir),
            $"skip/ subtree leaked into restore at '{restoredSkipDir}' — the filter didn't apply.");
        Assert.False(File.Exists(Path.Combine(restoreDir, "root-only.txt")));
        AssertBytesEqual(keepDir, Path.Combine(restoreDir, "keep"), ctx);
        ctx.Report.RecordNote("Source-relative exclusion proof", "Preview and real backup both retained files beneath a parent named skip and a source with literal brackets. Recovery retained the nested root-only.txt, omitted its root-level namesake, and omitted the excluded skip subtree.");
    }
}
