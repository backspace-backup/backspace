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

/// <summary>U174 through the orchestrator and the real engine: the scheduled
/// check finds the drive missing and waits; the next backup that reaches the
/// drive runs it, once; a backup that doesn't leaves it waiting.</summary>
public sealed class CheckOverdueRunTests
{
    [Fact]
    public async Task A_check_that_found_the_drive_missing_runs_after_the_next_backup_there_once()
    {
        if (!OperatingSystem.IsWindows()) return; // Drive letters are Windows-only.
        if (!ServiceLocator.Initialized) ServiceLocator.InitializeCore();
        ServiceLocator.Config.Update(cfg => { cfg.Backups.Clear(); cfg.Destinations.Clear(); });
        using var ws = new TempWorkspace("check-overdue");
        var src = ws.Sub("src");
        SyntheticFileTree.Generate(src, seed: 21, targetBytes: 50_000);
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var absent = Enumerable.Range('G', 20).Select(c => (char)c).Last(c => !used.Contains(c)).ToString();
        var dest = new Destination { Name = "Passport", Kind = DestinationKind.LocalFolder, PathOrSubpath = ws.Sub("storage"), Encrypted = false };
        var backup = new Backup
        {
            Name = "OverdueTest", SourcePaths = { src }, PruneSchedule = new BackupSchedule(),
            Checks = { new BackupCheck { Kind = CheckKind.Structure, Schedule = new BackupSchedule() } },
            UseVss = false, VssDisabledAcknowledged = true, AbortOnMeteredNetwork = false, Threads = 1,
        };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(backup); });
        var orchestrator = ServiceLocator.Orchestrator;
        int Checks() => ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id).Count(r => r.Kind == RunKind.Check);

        // A first backup so the storage exists.
        Assert.Equal(BackupRunStatus.Success, (await orchestrator.RunBackupAsync(backup, CancellationToken.None)).Status);
        Assert.Equal(0, Checks());

        // The drive is "unplugged": it is expected at a letter no drive has.
        ServiceLocator.Config.Update(cfg => cfg.Destinations.Single(d => d.Id == dest.Id).ExpectedDriveLetter = absent);
        var missed = await orchestrator.RunCheckAsync(ServiceLocator.Config.Current.Backups.Single(), CancellationToken.None);
        Assert.Equal(BackupRunStatus.Skipped, missed.Status);
        Assert.True(Assert.Single(missed.Cells).DestinationUnavailable);
        Assert.Contains("right after the next backup", missed.Summary);

        // A backup while it is still unplugged doesn't reach it: no check.
        var skipped = await orchestrator.RunBackupAsync(ServiceLocator.Config.Current.Backups.Single(), CancellationToken.None);
        Assert.NotEqual(BackupRunStatus.Success, skipped.Status);
        Assert.Equal(1, Checks());

        // Plugged in again: the backup succeeds and the check follows it.
        ServiceLocator.Config.Update(cfg => cfg.Destinations.Single(d => d.Id == dest.Id).ExpectedDriveLetter = null);
        Assert.Equal(BackupRunStatus.Success, (await orchestrator.RunBackupAsync(ServiceLocator.Config.Current.Backups.Single(), CancellationToken.None)).Status);
        var after = ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id).Where(r => r.Kind == RunKind.Check).OrderBy(r => r.StartedUtc).ToList();
        Assert.Equal(2, after.Count);
        Assert.Equal(BackupRunStatus.Success, after[^1].Status);
        Assert.False(after[^1].Cells.Single().DestinationUnavailable);

        // Once: the next backup adds no check.
        Assert.Equal(BackupRunStatus.Success, (await orchestrator.RunBackupAsync(ServiceLocator.Config.Current.Backups.Single(), CancellationToken.None)).Status);
        Assert.Equal(2, Checks());
    }
}
