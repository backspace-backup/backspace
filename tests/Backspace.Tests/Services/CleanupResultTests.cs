using System;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U142: what a cleanup removed, from restic's forget and prune output.</summary>
public sealed class CleanupResultTests
{
    // restic 0.19.1's output of "forget … --prune", as a run's transcript keeps it.
    private const string ForgetAndPrune = """
        Applying Policy: keep 1 latest snapshots
        keep 1 snapshots:
        ID        Time                 Host        Tags        Reasons        Paths
        ---------------------------------------------------------------------------
        4c5e7a1b  2026-11-06 01:00:00  pc                      last snapshot  C:\
        ---------------------------------------------------------------------------
        1 snapshots

        remove 2 snapshots:
        ID        Time                 Host        Tags        Paths
        ------------------------------------------------------------
        11111111  2026-11-05 01:00:00  pc                      C:\
        22222222  2026-11-05 13:00:00  pc                      C:\
        ------------------------------------------------------------
        2 snapshots

        [0:00] 100.00%  2 / 2 files deleted
        2 snapshots have been removed, running prune
        loading indexes...
        loading all snapshots...
        finding data that is still in use for 1 snapshots
        [0:00] 100.00%  1 / 1 snapshots
        searching used packs...
        collecting packs for deletion and repacking
        [0:00] 100.00%  5 / 5 packs processed

        to repack:            69 blobs / 1.078 MiB
        this removes:         67 blobs / 1.047 MiB
        to delete:          7012 blobs / 1.250 GiB
        total prune:        7079 blobs / 1.251 GiB
        remaining:         16033 blobs / 38.003 GiB
        unused size after prune: 0 B (0.00% of remaining size)

        """;

    [Fact]
    public void Forget_with_prune_gives_the_backups_removed_and_the_storage_freed()
    {
        var result = CleanupResult.Parse(ForgetAndPrune);
        Assert.Equal(2, result.SnapshotsRemoved);
        Assert.Equal((long)Math.Round(1.251 * (1L << 30)), result.BytesFreed);
        Assert.Equal("[cleanup] 2 old backups removed · 1.3 GB freed in the storage", result.Recap());
    }

    [Fact]
    public void Nothing_removed_reads_as_nothing_and_other_sizes_are_not_taken_for_the_freed_space()
    {
        // forget that keeps everything runs no prune.
        Assert.Equal(default, CleanupResult.Parse("Applying Policy: keep 10 latest snapshots\nkeep 3 snapshots:\n"));
        Assert.Equal("[cleanup] Nothing to remove: the keep rules keep every backup.", default(CleanupResult).Recap());
        // The "to repack" and "remaining" sizes of a prune are not what it freed.
        var prune = CleanupResult.Parse("to repack:   10 blobs / 5.000 GiB\nthis removes: 0 blobs / 0 B\nto delete:   0 blobs / 0 B\ntotal prune:  0 blobs / 0 B\nremaining:   9 blobs / 9.000 GiB\n");
        Assert.Equal(new CleanupResult(0, 0), prune);
        // A full prune after a backup cut short: data freed, no backup removed.
        var leftovers = CleanupResult.Parse("total prune:  3 blobs / 512.000 KiB\r\n");
        Assert.Equal(new CleanupResult(0, 512 * 1024), leftovers);
        Assert.Equal("[cleanup] 512 KB freed in the storage", leftovers.Recap());
        // A forget alone (before a full prune) lists what it removes.
        Assert.Equal(new CleanupResult(3, 0), CleanupResult.Parse("remove 3 snapshots:\nID  Time\n"));
    }

    [Fact]
    public void A_run_shows_what_its_cleanup_freed_only_when_something_was_freed()
    {
        var run = new RunRecord { Kind = RunKind.Backup, Status = BackupRunStatus.Success, FilesNew = 476, FilesModified = 157, BytesUploaded = (long)(1.6 * (1L << 30)) };
        Assert.Equal("476 new · 157 changed · 1.6 GB added", BackupHistoryViewModel.Added(run));
        run.SnapshotsRemoved = 2; run.BytesFreed = (long)(1.3 * (1L << 30));
        Assert.Equal("476 new · 157 changed · 1.6 GB added · 1.3 GB freed", BackupHistoryViewModel.Added(run));
        // Backups removed but nothing freed (what they held is still used): no figure.
        run.BytesFreed = 0;
        Assert.Equal("476 new · 157 changed · 1.6 GB added", BackupHistoryViewModel.Added(run));
        // A cleanup of its own (prune) shows what it freed too.
        var cleanup = new RunRecord { Kind = RunKind.Prune, Status = BackupRunStatus.Success, SnapshotsRemoved = 3, BytesFreed = (long)(2.5 * (1L << 30)) };
        Assert.Equal("3 old backups removed · 2.5 GB freed", BackupHistoryViewModel.Added(cleanup));
        Assert.Equal("", BackupHistoryViewModel.Added(new RunRecord { Kind = RunKind.Prune, Status = BackupRunStatus.Success }));
    }
}
