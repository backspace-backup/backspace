using System;
using System.IO;
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
/// Erase-storage path: back up, then wipe the destination via
/// <see cref="StorageCleaner.WipeEntireDestinationAsync"/>. Two
/// invariants:
///
///   1. The restic-owned artefacts (data/, index/, keys/,
///      snapshots/, config) under the destination folder are gone.
///   2. Foreign files the user happens to keep in the same folder
///      stay put — the wipe is precise, not a `Directory.Delete`
///      of the whole tree.
///
/// The dialog's confirm copy explicitly promises (2); a regression
/// that nuked the whole folder would silently destroy the user's
/// own data alongside the backups. This is the high-blast-radius
/// scenario where being precise actually matters.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E20_EraseStorageWipesDataPreservesForeign : BackupE2EBase
{
    public E20_EraseStorageWipesDataPreservesForeign(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E20-erase-storage-wipes-data-preserves-foreign";

    [AvaloniaFact] public async Task Run_E20() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var source   = ctx.MakeSource("docs", seed: 201, targetBytes: 500_000);
        var destPath = ctx.MakeLocalDestination("erase-local");

        // Foreign file the user keeps next to the backup data.
        // Plausible shape: a README or a personal scratch file the
        // user dropped into the same folder.
        var foreignPath = Path.Combine(destPath, "MY-OWN-NOTES.txt");
        var foreignContent = "I am not a restic file. Do not delete me.\n";
        Directory.CreateDirectory(destPath);
        await File.WriteAllTextAsync(foreignPath, foreignContent, CancellationToken.None);

        var dest = new Destination
        {
            Name = "Local — erase target",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destPath,
            Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e20", $"e20-{Guid.NewGuid():N}"),
        };
        var backup = new Backup
        {
            Name = "Docs (E20)",
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
        Assert.True(record.Status is BackupRunStatus.Success or BackupRunStatus.Warning);

        // Sanity: restic artefacts exist before the wipe.
        var configExists = File.Exists(Path.Combine(destPath, "config"));
        var dataDirExists = Directory.Exists(Path.Combine(destPath, "data"));
        Assert.True(configExists,
            "Pre-condition violated: expected restic's `config` blob to exist after a successful backup.");
        Assert.True(dataDirExists,
            "Pre-condition violated: expected restic's `data/` dir to exist after a successful backup.");

        // Wipe.
        var report = await ServiceLocator.Cleaner.WipeEntireDestinationAsync(
            dest, CancellationToken.None);
        ctx.Snapshot(host, "after wipe",
            $"Wipe report: errors={report.Errors.Count}, " +
            $"wipedPath={report.WipedPath}, notes={report.Notes.Count}.");
        Assert.Empty(report.Errors);

        // Post-condition 1: restic artefacts are gone.
        Assert.False(File.Exists(Path.Combine(destPath, "config")),
            "Wipe left restic's `config` blob behind — destination not actually cleaned.");
        Assert.False(Directory.Exists(Path.Combine(destPath, "data")),
            "Wipe left restic's `data/` dir behind — destination not actually cleaned.");

        // Post-condition 2: the foreign file is still there with
        // unchanged contents. A regression that did a wholesale
        // `Directory.Delete` would have nuked this.
        Assert.True(File.Exists(foreignPath),
            "Wipe destroyed the user's own file alongside the restic data. " +
            "The dialog promised foreign files would be left alone.");
        var stillForeign = await File.ReadAllTextAsync(foreignPath, CancellationToken.None);
        Assert.Equal(foreignContent, stillForeign);
    }
}
