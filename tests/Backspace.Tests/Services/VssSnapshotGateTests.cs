using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U156: with two backups at once, two background workers start together on
/// C:. Windows makes one snapshot set at a time; the second used to fall back
/// to no snapshot (open files missed). Backspace now lets one snapshot be
/// made at a time, and gives restic's own retry a long deadline.
/// </summary>
public sealed class VssSnapshotGateTests
{
    /// <summary>Windows' rule, as VSS reports it: a second snapshot set while
    /// one is being made fails with VSS_E_SNAPSHOT_SET_IN_PROGRESS.</summary>
    private sealed class FakeVss
    {
        private int _making;
        internal readonly ConcurrentQueue<string> Results = new();
        internal async Task CreateAsync(string who)
        {
            if (Interlocked.CompareExchange(ref _making, 1, 0) != 0)
            {
                Results.Enqueue(who + ": VSS_E_SNAPSHOT_SET_IN_PROGRESS, backed up without a snapshot");
                return;
            }
            try { await Task.Delay(300); Results.Enqueue(who + ": snapshot"); }
            finally { Volatile.Write(ref _making, 0); }
        }
    }

    private static string Name() => @"Local\Backspace-VssSnapshot-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Without_the_gate_the_second_backup_loses_its_snapshot()
    {
        // The fake really reports "in progress" to an overlapping second set.
        var vss = new FakeVss();
        await Task.WhenAll(vss.CreateAsync("local_c"), vss.CreateAsync("local_dropbox"));
        Assert.Contains(vss.Results, r => r.Contains("VSS_E_SNAPSHOT_SET_IN_PROGRESS"));
    }

    [Fact]
    public async Task With_the_gate_each_backup_that_wants_a_snapshot_gets_one_the_second_waiting_its_turn()
    {
        var vss = new FakeVss(); var name = Name(); var waiting = new ConcurrentQueue<string>();
        async Task Backup(string who)
        {
            using var turn = await VssSnapshotGate.AcquireAsync(who, r => waiting.Enqueue(who + ": " + r), CancellationToken.None, name);
            await vss.CreateAsync(who);
            // Restic reads its first files: the snapshot exists, the next may make one.
            var progress = new BackupLiveProgress { Reading = turn.Dispose };
            progress.NextStatus(Status(filesDone: 0), DateTime.UtcNow);
            Assert.True(turn.Held, "No file read yet: the gate is still held.");
            progress.NextStatus(Status(filesDone: 3), DateTime.UtcNow);
            Assert.False(turn.Held);
        }
        await Task.WhenAll(Backup("local_c"), Backup("local_dropbox"), Backup("photos")).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(3, vss.Results.Count);
        Assert.All(vss.Results, r => Assert.EndsWith(": snapshot", r));
        Assert.Contains(waiting, w => w.EndsWith(": " + RepositoryAccess.SnapshotReason, StringComparison.Ordinal));
        Assert.Equal("waiting for another backup's Windows snapshot", RepositoryAccess.SnapshotReason);
    }

    [Fact]
    public async Task The_gate_is_let_go_when_the_engine_holds_the_backup_back_or_after_its_limit()
    {
        var name = Name();
        var first = await VssSnapshotGate.AcquireAsync("A", null, CancellationToken.None, name);
        var progress = new BackupLiveProgress { HeldBack = first.Dispose };
        progress.HeldBack!();
        using var second = await VssSnapshotGate.AcquireAsync("B", null, CancellationToken.None, name).WaitAsync(TimeSpan.FromSeconds(5));
        var hold = VssSnapshotGate.Hold;
        VssSnapshotGate.Hold = TimeSpan.FromMilliseconds(300);
        try
        {
            using var limited = await VssSnapshotGate.AcquireAsync("C", null, CancellationToken.None, Name());
            await Task.Delay(1000);
            Assert.False(limited.Held);
        }
        finally { VssSnapshotGate.Hold = hold; }
    }

    [Fact]
    public void Restic_keeps_retrying_while_another_snapshot_is_made_for_thirty_minutes()
    {
        var flags = ResticBackrestEngine.BuildBackupFlags(new Backup { UseVss = true });
        if (!Backspace.Services.Platform.PlatformInfo.IsWindows) { Assert.DoesNotContain("-o", flags); return; }
        var at = flags.IndexOf("--use-fs-snapshot");
        Assert.True(at >= 0);
        Assert.Equal(["-o", "vss.timeout=30m"], flags.Skip(at + 1).Take(2));
        Assert.DoesNotContain("vss.timeout=30m", ResticBackrestEngine.BuildBackupFlags(new Backup { UseVss = false }));
    }

    private static BackupLiveProgress.Sample? Status(long filesDone) => BackupLiveProgress.FromResticStatus(JsonDocument.Parse(
        $"{{\"message_type\":\"status\",\"percent_done\":0.1,\"total_bytes\":0,\"bytes_done\":0,\"total_files\":0,\"files_done\":{filesDone}}}").RootElement);
}
