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
/// Regression: a real-user restore of ~2400 files crashed with Windows'
/// "The filename or extension is too long" error because <see
/// cref="ResticBackrestEngine"/> was emitting one <c>--include /path</c>
/// flag per requested file, blowing past CreateProcess's ~32 KB
/// command-line ceiling. The fix switched to a single
/// <c>--include-file &lt;tempfile&gt;</c>.
///
/// This scenario seeds a fixture with ≥1500 small files in a single
/// source, runs the full backup + restore loop end-to-end through the
/// production code paths, and asserts every file lands on the restore
/// side. Without the <c>--include-file</c> fix the restic launch fails
/// with <c>fork/exec restic.exe: The filename or extension is too
/// long</c> before any byte hits disk.
///
/// File COUNT is the load-bearing variable here — not size. Files are
/// kept tiny (~4 KB each) so the scenario stays fast: ~8 MB total,
/// runs in seconds, exercises the exact pathological case without
/// paying scale-test cost.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E34_RestoreLongIncludeList : BackupE2EBase
{
    public E34_RestoreLongIncludeList(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E34-restore-long-include-list";

    /// <summary>
    /// Target file count. Each <c>--include /path/to/file</c> flag is
    /// ~40 bytes once anchored and escaped; 800 files is the rough
    /// Windows 32 KB threshold. 1500 puts us comfortably past it so
    /// the test cannot be silently weakened by future fixture tweaks.
    /// </summary>
    private const int TargetFileCount = 1500;

    [AvaloniaFact]
    public async Task Run_E34()
    {
        await Run();
    }

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var src = Path.Combine(ctx.SourcesDir, "source-1");
        Directory.CreateDirectory(src);

        // Emit ${TargetFileCount} small files inline. Bypassing
        // SyntheticFileTree because its file-size profiles target media
        // shapes and a small targetBytes budget exhausts after a couple
        // of dozen files. The intent here is many tiny files, period.
        // 50 dirs × 30 files = 1500 files; per-file ~1 KB; ~1.5 MB total.
        const int dirCount = 50;
        const int filesPerDir = 30;
        int emitted = 0;
        if (FixtureSizeResolver.ResolveSize(FixtureSize.Smoke) == FixtureSize.Huge)
        {
            FixtureFactory.GenerateScaleSource(src, 34_1500);
            emitted = Directory.GetFiles(src, "*", SearchOption.AllDirectories).Length;
        }
        else
        {
        var rng = new Random(34_1500);
        var buf = new byte[1024];
        for (int d = 0; d < dirCount; d++)
        {
            var dir = Path.Combine(src, $"dir-{d:D3}");
            Directory.CreateDirectory(dir);
            for (int f = 0; f < filesPerDir; f++)
            {
                rng.NextBytes(buf);
                File.WriteAllBytes(Path.Combine(dir, $"file-{f:D3}.bin"), buf);
                emitted++;
            }
        }
        }

        Assert.True(emitted >= TargetFileCount,
            $"Inline emit produced {emitted} files; need ≥{TargetFileCount} " +
            "to exercise the 32 KB command-line threshold.");

        var destPath = ctx.MakeLocalDestination("local-backup-dir");
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
            Name = "Many files (E34)",
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
        ctx.Snapshot(host, "before run",
            $"Seeded fixture with {emitted} files (target ≥{TargetFileCount}); never-run card visible.");

        var record = await RunBackupViaUiAsync(ctx, backup, view, CancellationToken.None);
        ctx.Snapshot(host, $"after run {record.Status}",
            $"RunRecord status={record.Status}, summary={record.Summary}");

        Assert.True(record.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Backup ended {record.Status}; errors={string.Join(" | ", record.Errors)}.");

        // The restore is the actual repro path — RestoreEverythingAsync
        // builds a RestoreRequest containing every file path from the
        // snapshot. Without --include-file, restic launch fails before
        // any file is written; the per-file outcome map would show
        // every entry RestoreFailed. With the fix, every file lands.
        var perSourceRestore = Path.Combine(restoreDir, "source-1");
        Directory.CreateDirectory(perSourceRestore);
        var outcome = await RestoreEverythingAsync(
            backup, src, dest, "default", perSourceRestore, CancellationToken.None);

        Assert.Equal(0, outcome.FailedCount);
        AssertBytesEqual(src, perSourceRestore, ctx);

        ctx.Snapshot(host, "after restore",
            $"Restored {outcome.RestoredCount} of {outcome.TotalFiles} files via --include-file; status={outcome.TerminalStatus}.");
    }
}
