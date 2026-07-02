using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.WebDav;
using Backspace.Services.WinFsp;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;
using Xunit.Abstractions;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// Real-user E2E: mount a freshly-written revision as a Windows
/// drive letter via <see cref="WinFspMountCoordinator"/>, walk
/// the mount with Directory.EnumerateFiles, and assert the same
/// payload we just backed up is readable through the drive.
///
/// History: prior to 2026-06-12 this scenario used the WebDAV
/// redirector via net use. That path silently failed in production
/// (custom-port WebDAV is rejected by Windows 10/11) but the
/// scenario soft-skipped on failure, masking the regression for
/// months. We replaced WebDAV with WinFSP — a real user-mode
/// filesystem driver — which is the canonical Windows approach for
/// this problem.
///
/// Skip semantics:
///   • Non-Windows host → skip (Windows-only feature).
///   • Windows but no WinFSP runtime installed → skip with a clear
///     message + install URL. We can't fail here because a clean
///     dev box has no WinFSP MSI; that's an environmental setup
///     step the user takes once.
///   • Windows + WinFSP installed → hard-fail on any mount error.
///     This is the actual integration regression check.
/// </summary>
[Collection(ScenarioCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class E04_SnapshotMountWindows : BackupE2EBase
{
    public E04_SnapshotMountWindows(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E04-snapshot-mount-windows";

    [AvaloniaFact] public async Task Run_E04() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        if (!OperatingSystem.IsWindows())
        {
            ctx.Skip("Snapshot mount via WinFSP is Windows-only.");
            return;
        }

        if (!WinFspRuntimeDetector.IsInstalled())
        {
            ctx.Skip(
                $"WinFSP runtime not installed on this host. " +
                $"Install from {WinFspRuntimeDetector.InstallUrl} and re-run E04.");
            return;
        }

        var sourcePath = ctx.MakeSource("docs", seed: 7, targetBytes: 1_500_000);
        var destPath   = ctx.MakeLocalDestination("mount-dest");

        var dest = new Destination
        {
            Name = "Local — mount-test",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destPath,
            Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew(
                "e2e-mount", $"e2e-{Guid.NewGuid():N}"),
        };
        var backup = new Backup
        {
            Name = "Mount target (E04)",
            SourcePaths = { sourcePath },
            UseVss = false, Threads = 2,
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

        var record = await RunBackupViaUiAsync(ctx, backup, view);
        Assert.True(record.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Backup ended {record.Status}; errors={string.Join(" | ", record.Errors)}.");

        var revisions = await Task.Run(() => ServiceLocator.Revisions.ListRevisionsAsync(
            backup, sourcePath, dest, "default", CancellationToken.None));
        Assert.NotEmpty(revisions);
        var latest = revisions[0].Number;

        var cacheDir = Path.Combine(ctx.RootDir, "mount-cache");
        var provider = new EngineBackedSnapshotTreeProvider(
            ServiceLocator.Engine, backup, sourcePath, dest, "default", latest, cacheDir);
        await using var coord = new WinFspMountCoordinator(provider);

        var result = await coord.MountAsync(CancellationToken.None);
        Assert.True(result.Succeeded,
            $"WinFSP mount failed: {result.Message}. WinFSP runtime is installed " +
            $"({WinFspRuntimeDetector.GetInstallDir()}) so this is a real regression, not env.");

        Assert.NotNull(result.DriveLetter);
        ctx.Snapshot(host, $"mounted at {result.DriveLetter}",
            $"Snapshot rev {latest} mounted as {result.DriveLetter}.");

        // Walk the mount: every relative path in the source should
        // be visible through the drive letter.
        var mountRoot = result.DriveLetter + "\\";
        var observed = Directory.EnumerateFiles(mountRoot, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(mountRoot, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var expected = Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(sourcePath, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.Equal(expected, observed);
    }
}
