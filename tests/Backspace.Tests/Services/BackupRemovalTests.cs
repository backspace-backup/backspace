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

/// <summary>Removing a backup deletes what it stored; an unreachable
/// destination is remembered and finished later (U029, 29 October 2026).</summary>
public class BackupRemovalTests
{
    private static (BackupRemoval Removal, InMemoryBackupEngine Engine, string PendingFile) Create(TempWorkspace tmp, LogStore? logs = null)
    {
        var engine = new InMemoryBackupEngine();
        var cleaner = new StorageCleaner(ServiceLocator.Config, new SecretsStore(), engine);
        var pending = Path.Combine(tmp.Sub("state"), "pending-removals.json");
        return (new BackupRemoval(ServiceLocator.Config, cleaner, new MaintenanceCoordinator(), pending, logs), engine, pending);
    }

    private static RunRecord History(LogStore logs, Backup backup)
    {
        var record = new RunRecord { BackupId = backup.Id, BackupName = backup.Name, StartedUtc = DateTime.UtcNow, EndedUtc = DateTime.UtcNow, Status = BackupRunStatus.Success };
        logs.AppendRun(record);
        return record;
    }

    private static string UnusedDrive() => Enumerable.Range('D', 23).Select(c => (char)c).First(c => !Directory.Exists(c + ":\\")) + ":";

    [Fact]
    public async Task Removing_a_backup_erases_its_copies_and_keeps_the_other_backups()
    {
        using var tmp = new TempWorkspace("removal");
        var logs = new LogStore(tmp.Sub("engine-logs"));
        var (removal, engine, pendingFile) = Create(tmp, logs);
        var source = tmp.Sub("source"); File.WriteAllText(Path.Combine(source, "a.txt"), "contents");
        var dest = new Destination { Name = "Drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = tmp.Sub("destination") };
        var mine = new Backup { Name = "Mine", SourcePaths = { source }, Targets = { new BackupTarget { DestinationId = dest.Id } } };
        var other = new Backup { Name = "Other", SourcePaths = { source }, Targets = { new BackupTarget { DestinationId = dest.Id } } };
        ServiceLocator.Config.Update(c => { c.Destinations.Add(dest); c.Backups.Add(mine); c.Backups.Add(other); });
        try
        {
            await engine.BackupPruneCheckAsync(mine, source, dest, "default", CancellationToken.None);
            await engine.BackupPruneCheckAsync(mine, source, dest, "default", CancellationToken.None);
            await engine.BackupPruneCheckAsync(other, source, dest, "default", CancellationToken.None);
            History(logs, mine); History(logs, other);

            var outcome = await removal.RemoveAsync(mine, null, null, CancellationToken.None);

            Assert.Equal(new[] { "Drive" }, outcome.Erased);
            Assert.Empty(outcome.Deferred);
            Assert.DoesNotContain(ServiceLocator.Config.Current.Backups, b => b.Id == mine.Id);
            Assert.Empty(await engine.ListSnapshotsAsync(mine, source, dest, "default", CancellationToken.None));
            Assert.Single(await engine.ListSnapshotsAsync(other, source, dest, "default", CancellationToken.None));
            Assert.Empty(removal.ReadPending());
            Assert.False(File.Exists(pendingFile));
            // Its run history goes too (the person's decision); the other backup keeps its own.
            Assert.Empty(logs.LoadTimelineByBackupId(mine.Id));
            Assert.Single(logs.LoadTimelineByBackupId(other.Id));
        }
        finally { ServiceLocator.Config.Update(c => { c.Backups.RemoveAll(b => b.Id == other.Id || b.Id == mine.Id); c.Destinations.RemoveAll(d => d.Id == dest.Id); }); }
    }

    [Fact]
    public async Task An_unplugged_drive_is_cleaned_up_once_it_is_connected_again()
    {
        using var tmp = new TempWorkspace("removal-later");
        var logs = new LogStore(tmp.Sub("engine-logs"));
        var (removal, engine, pendingFile) = Create(tmp, logs);
        var source = tmp.Sub("source"); File.WriteAllText(Path.Combine(source, "a.txt"), "contents");
        var path = tmp.Sub("destination");
        var dest = new Destination { Name = "USB drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = path, ExpectedDriveLetter = UnusedDrive() };
        var mine = new Backup { Name = "Mine", SourcePaths = { source }, Targets = { new BackupTarget { DestinationId = dest.Id } } };
        ServiceLocator.Config.Update(c => { c.Destinations.Add(dest); c.Backups.Add(mine); });
        try
        {
            await engine.BackupPruneCheckAsync(mine, source, dest, "default", CancellationToken.None);
            History(logs, mine);

            var outcome = await removal.RemoveAsync(mine, null, null, CancellationToken.None);

            // Removed from Backspace now; the copy waits for the drive, and
            // its history stays until the deletion is done.
            Assert.Equal(new[] { "USB drive" }, outcome.Deferred);
            Assert.DoesNotContain(ServiceLocator.Config.Current.Backups, b => b.Id == mine.Id);
            Assert.Single(await engine.ListSnapshotsAsync(mine, source, dest, "default", CancellationToken.None));
            var queued = Assert.Single(removal.ReadPending());
            Assert.Equal(dest.Id, queued.DestinationId);
            Assert.Contains("not connected", queued.LastError);
            Assert.True(File.Exists(pendingFile));
            Assert.NotEmpty(logs.LoadTimelineByBackupId(mine.Id));

            // Still unplugged: a retry changes nothing.
            await removal.RetryPendingAsync(CancellationToken.None);
            Assert.Single(removal.ReadPending());
            Assert.Single(await engine.ListSnapshotsAsync(mine, source, dest, "default", CancellationToken.None));

            // The drive is back (the expected volume now resolves).
            ServiceLocator.Config.Update(c => c.Destinations.Single(d => d.Id == dest.Id).ExpectedDriveLetter = null);
            await removal.RetryPendingAsync(CancellationToken.None);
            Assert.Empty(removal.ReadPending());
            Assert.Empty(await engine.ListSnapshotsAsync(mine, source, dest, "default", CancellationToken.None));
            Assert.False(File.Exists(pendingFile));
            Assert.Empty(logs.LoadTimelineByBackupId(mine.Id));
        }
        finally { ServiceLocator.Config.Update(c => { c.Backups.RemoveAll(b => b.Id == mine.Id); c.Destinations.RemoveAll(d => d.Id == dest.Id); }); }
    }

    [Fact]
    public void Forgetting_a_backup_deletes_its_history_under_every_name_and_keeps_other_backups()
    {
        using var tmp = new TempWorkspace("forget-history");
        var root = tmp.Sub("engine");
        var logs = new LogStore(root);
        RunRecord Run(string id, string name)
        {
            var dir = Path.Combine(root, AppPaths.Sanitize(name));
            Directory.CreateDirectory(dir);
            var log = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".log");
            File.WriteAllText(log, "transcript");
            return new RunRecord { BackupId = id, BackupName = name, StartedUtc = DateTime.UtcNow, EndedUtc = DateTime.UtcNow, Status = BackupRunStatus.Success, LogPath = log };
        }
        // "a" ran as Docs, then was renamed; "b" reused the old name later.
        var before = Run("a", "Docs"); var after = Run("a", "Docs renamed");
        var reused = Run("b", "Docs"); var other = Run("b", "Photos");
        foreach (var r in new[] { before, after, reused, other }) logs.AppendRun(r);

        logs.ForgetBackup("a");

        Assert.Empty(logs.LoadTimelineByBackupId("a"));
        Assert.False(File.Exists(before.LogPath));
        Assert.False(File.Exists(after.LogPath));
        Assert.False(Directory.Exists(Path.Combine(root, AppPaths.Sanitize("Docs renamed"))));
        // The other backup keeps its records and transcripts, even in the shared folder.
        Assert.Equal(2, logs.LoadTimelineByBackupId("b").Count);
        Assert.True(File.Exists(reused.LogPath));
        Assert.True(File.Exists(other.LogPath));
    }

    [Fact]
    public async Task A_removed_destination_drops_its_queued_cleanup()
    {
        using var tmp = new TempWorkspace("removal-forget");
        var (removal, _, _) = Create(tmp);
        var source = tmp.Sub("source");
        var dest = new Destination { Name = "Old drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = tmp.Sub("destination"), ExpectedDriveLetter = UnusedDrive() };
        var mine = new Backup { Name = "Mine", SourcePaths = { source }, Targets = { new BackupTarget { DestinationId = dest.Id } } };
        ServiceLocator.Config.Update(c => { c.Destinations.Add(dest); c.Backups.Add(mine); });
        await removal.RemoveAsync(mine, null, null, CancellationToken.None);
        Assert.Single(removal.PendingFor(dest.Id));

        ServiceLocator.Config.Update(c => c.Destinations.RemoveAll(d => d.Id == dest.Id));
        await removal.RetryPendingAsync(CancellationToken.None);
        Assert.Empty(removal.ReadPending());
    }
}
