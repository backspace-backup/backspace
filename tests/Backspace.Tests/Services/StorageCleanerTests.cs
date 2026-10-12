using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public class StorageCleanerTests
{
    [Theory]
    [InlineData(DestinationKind.LocalFolder)]
    [InlineData(DestinationKind.DropboxAppScoped)]
    public async Task Erase_UsesListedSnapshotIdentities_AndPreservesSiblingHistory(DestinationKind kind)
    {
        using var tmp = new TempWorkspace("cleanup");
        var config = new ConfigStore(); config.Load();
        var engine = new InMemoryBackupEngine();
        var source = tmp.Sub("source"); File.WriteAllText(Path.Combine(source, "a.txt"), "contents");
        var dest = new Destination { Name = "Test", Kind = kind, PathOrSubpath = tmp.Sub("destination") };
        var backup = new Backup { Name = "Mine", SourcePaths = { source } };
        var sibling = new Backup { Name = "Sibling", SourcePaths = { source } };
        await engine.BackupPruneCheckAsync(backup, source, dest, "default", CancellationToken.None);
        await engine.BackupPruneCheckAsync(backup, source, dest, "default", CancellationToken.None);
        await engine.BackupPruneCheckAsync(sibling, source, dest, "default", CancellationToken.None);
        var cleaner = new StorageCleaner(config, new SecretsStore(), engine);
        var report = await cleaner.EraseBackupFromDestinationAsync(backup, dest, "default", CancellationToken.None);
        Assert.Empty(report.Errors); Assert.Equal(2, report.DeletedSnapshotIds.Count);
        Assert.Empty(await engine.ListSnapshotsAsync(backup, source, dest, "default", CancellationToken.None));
        Assert.Single(await engine.ListSnapshotsAsync(sibling, source, dest, "default", CancellationToken.None));
        var again = await cleaner.EraseBackupFromDestinationAsync(backup, dest, "default", CancellationToken.None);
        Assert.Empty(again.Errors); Assert.Empty(again.DeletedSnapshotIds);
        Assert.Contains(again.Notes, n => n.Contains("nothing to remove"));
    }

    [Theory]
    [InlineData(DestinationKind.LocalFolder)]
    [InlineData(DestinationKind.DropboxAppScoped)]
    public async Task RemoveDestination_NeverDeletesGenericFolderNames(DestinationKind kind)
    {
        using var tmp = new TempWorkspace("cleanup-unrelated");
        var config = new ConfigStore(); config.Load();
        var root = tmp.Sub("user-folder");
        foreach (var folder in new[] { "data", "index", "snapshots", "keys", "locks", "chunks", ".duplicacy" })
        {
            Directory.CreateDirectory(Path.Combine(root, folder));
            File.WriteAllText(Path.Combine(root, folder, "my-file"), "keep");
        }
        File.WriteAllText(Path.Combine(root, "config"), "my settings");
        var cleaner = new StorageCleaner(config, new SecretsStore(), new InMemoryBackupEngine());
        var report = await cleaner.WipeEntireDestinationAsync(new Destination { Name="Unrelated", Kind=kind, PathOrSubpath=root }, CancellationToken.None);
        Assert.Empty(report.Errors); Assert.Empty(report.DeletedSnapshotIds);
        Assert.Equal(8, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length);
        Assert.Equal("my settings", File.ReadAllText(Path.Combine(root, "config")));
        Assert.Contains(report.Notes, n => n.Contains("left untouched"));
    }
}
