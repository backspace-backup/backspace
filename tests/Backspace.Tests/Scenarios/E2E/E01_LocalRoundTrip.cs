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
/// Real-user E2E: easy-mode, local-folder destination, full round-trip.
/// Drives the same UX a user follows:
///   1. Open BackupsView with seeded easy-mode backup + local dest.
///   2. Click "Run now" via the bound command (production code path).
///   3. Wait for success, capture the post-run card.
///   4. List the revision, restore EVERY file into a scratch dir.
///   5. Assert byte-equality (SHA-256 manifest hash) source vs. restored.
///
/// Runs against three fixture sizes (<see cref="FixtureSize"/>):
///   • Smoke  — 1 source,  ~30 files, &lt;20 MB. Fastest path; catches
///              ordinary regressions in seconds, not minutes.
///   • Many   — 3 sources, ~500 files, &lt;20 MB. Per-file scaling
///              (rclone pagination, index churn, progress accounting).
///   • Large  — 3 sources, ~50 files, ~200 MB with a 180 MB blob.
///              Streaming behaviour (chunker windows, long-stall
///              progress, restore buffers). Large blob is session-
///              cached so the sweep doesn't re-roll 180 MB per run.
///
/// All three sizes always include the full peculiarity set from
/// <see cref="SyntheticFileTreePeculiarities"/> (Unicode, NTFS shapes,
/// edge attributes), so the matrix is "scale × all weird shapes."
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E01_LocalRoundTrip : BackupE2EBase
{
    public E01_LocalRoundTrip(ITestOutputHelper output) : base(output) { }

    // The Theory sets this before delegating to the base Run(); the
    // ScenarioName accessor reads it so each parametrisation lands
    // in its own artifact directory ("E01-…-smoke" etc).
    private FixtureSize _size = FixtureSize.Smoke;

    protected override string ScenarioName =>
        $"E01-local-round-trip-{_size.ToString().ToLowerInvariant()}";

    [AvaloniaTheory]
    [InlineData(FixtureSize.Smoke)]
    [InlineData(FixtureSize.Many)]
    [InlineData(FixtureSize.Large)]
    public async Task Run_E01(FixtureSize size)
    {
        _size = size;
        await Run();
    }

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var sources = FixtureFactory.Generate(ctx.SourcesDir, _size, seed: 91);
        var destPath   = ctx.MakeLocalDestination("local-backup-dir");
        var restoreDir = ctx.MakeRestoreTarget("restored");

        var dest = new Destination
        {
            Name = "Local — backup-dir",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destPath,
            Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew(
                "e2e-local", $"e2e-{Guid.NewGuid():N}"),
        };
        var backup = new Backup
        {
            Name = $"Documents (E01 / {_size})",
            UseVss = false,
            // Non-null sentinels suppress inline prune/check.
            PruneSchedule = new BackupSchedule(),
            CheckSchedule = new BackupSchedule(),
            Threads = 2,
            Targets = { new BackupTarget { DestinationId = dest.Id, StorageName = "default" } },
        };
        foreach (var s in sources) backup.SourcePaths.Add(s);
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(dest);
            cfg.Backups.Add(backup);
        });

        var view = new BackupsView { DataContext = new BackupsViewModel() };
        var host = new Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);
        ctx.Snapshot(host, "before run",
            $"Seeded easy-mode backup ({_size}, {sources.Length} source(s)); never-run card visible.");

        var record = await RunBackupViaUiAsync(ctx, backup, view, CancellationToken.None);
        ctx.Snapshot(host, $"after run {record.Status}",
            $"RunRecord status={record.Status}, summary={record.Summary}");

        Assert.True(record.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Backup ended {record.Status}; errors={string.Join(" | ", record.Errors)}.");

        // Restore + byte-compare each source independently. The
        // peculiarity set always lives in sources[0] so its
        // Unicode/NTFS/edge files get exercised in every run.
        foreach (var (src, idx) in sources.Select((s, i) => (s, i)))
        {
            var perSourceRestore = Path.Combine(restoreDir, $"source-{idx + 1}");
            Directory.CreateDirectory(perSourceRestore);
            var outcome = await RestoreEverythingAsync(
                backup, src, dest, "default", perSourceRestore, CancellationToken.None);
            Assert.Equal(0, outcome.FailedCount);
            AssertBytesEqual(src, perSourceRestore, ctx);
        }
    }
}
