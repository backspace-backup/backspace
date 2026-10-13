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
/// Restore-to-original-source path: back up, mutate the live
/// source, restore on top of it. This is the "I just lost my
/// files" recovery flow — the destructive-confirm dialog is the
/// gate the UI puts in front of it, and the byte-equality
/// assertion afterward proves the rollback was complete.
///
/// We bypass the UI confirm dialog here (the headless test driver
/// can't synthesise the modal's typing gate cleanly) but exercise
/// the same <see cref="RestoreEngine.RunAsync"/> path with
/// <c>Overwrite=true</c> and <c>TargetPath = source</c>, which is
/// what the wizard finally hands the engine.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E18_RestoreToOriginalSource : BackupE2EBase
{
    public E18_RestoreToOriginalSource(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E18-restore-to-original-source";

    [AvaloniaFact] public async Task Run_E18() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var source   = ctx.MakeSource("docs", seed: 181, targetBytes: 600_000);
        var destPath = ctx.MakeLocalDestination("orig-local");

        var dest = new Destination
        {
            Name = "Local — orig target",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destPath,
            Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e18", $"e18-{Guid.NewGuid():N}"),
        };
        var backup = new Backup
        {
            Name = "Docs (E18)",
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

        var record = await RunBackupViaUiAsync(ctx, backup, view, CancellationToken.None);
        Assert.True(record.Status is BackupRunStatus.Success or BackupRunStatus.Warning);

        // Capture the pristine source hash for the post-restore check.
        var pristineRoot = ctx.MakeRestoreTarget("pristine-copy");
        CopyDir(source, pristineRoot);

        // Disaster: clobber the source with garbage. A full file
        // gets corrupted and a different one is removed entirely.
        var sourceFiles = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).OrderBy(p => p).ToList();
        Assert.True(sourceFiles.Count >= 2);
        await File.WriteAllTextAsync(sourceFiles[0], "RUINED\n", CancellationToken.None);
        File.Delete(sourceFiles[1]);

        // Restore back ON TOP OF the source.
        var revisions = await ServiceLocator.Revisions.ListRevisionsAsync(
            backup, source, dest, "default", CancellationToken.None);
        var latest = revisions[0].Number;
        var allFiles = await ServiceLocator.Revisions.ListFilesAsync(
            backup, source, dest, "default", latest, CancellationToken.None);
        var request = new RestoreRequest
        {
            Backup = backup,
            SourcePath = source,
            Destination = dest,
            StorageName = "default",
            Revision = latest,
            Files = allFiles.Select(f => f.Path).ToList(),
            TargetPath = source, // <-- the whole point of this scenario
            Overwrite = true,
            PreserveStructure = true,
            Threads = 2,
            RetryDelaysSeconds = new[] { 1, 2, 5 },
        };
        var outcome = await ServiceLocator.Restore.RunAsync(request, CancellationToken.None);
        ctx.Snapshot(host, $"after overwrite restore",
            $"Restored ON TOP of source; restored={outcome.RestoredCount}, failed={outcome.FailedCount}.");

        Assert.Equal(0, outcome.FailedCount);
        AssertBytesEqual(pristineRoot, source, ctx);
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
