using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U159: while a snapshot is mounted or restored, nothing may remove
/// or rewrite the destination's stored data, in this process or another.</summary>
public sealed class SnapshotReadersTests
{
    private static Destination NewDestination() => new() { Id = "dst-" + Guid.NewGuid().ToString("N")[..8], Name = "Drive" };

    [Fact]
    public async Task A_mount_blocks_cleanup_of_its_destination_only_until_unmounted()
    {
        var mounted = NewDestination();
        var other = NewDestination();
        var mount = await SnapshotReaders.HoldAsync(mounted, "default", SnapshotReaders.Kind.Mount, null, CancellationToken.None);
        Assert.Null(SnapshotReaders.TryBeginChange(mounted, "default", out var reason));
        Assert.Equal("a snapshot of this backup is mounted as a drive", reason);
        Assert.Equal("Cleanup waits: a snapshot of this backup is mounted as a drive. It runs after a later backup, once the drive is unmounted and restores have finished.",
            SnapshotReaders.CleanupSkipped(reason));
        // Another destination, and another storage name, are not held.
        using (var free = SnapshotReaders.TryBeginChange(other, "default", out _)) Assert.NotNull(free);
        using (var free = SnapshotReaders.TryBeginChange(mounted, "second", out _)) Assert.NotNull(free);
        // Any mount at all holds back changes that are not tied to one destination.
        Assert.Null(SnapshotReaders.TryBeginChangeAnywhere(out var anywhere));
        Assert.Contains("mounted", anywhere);
        mount.Dispose();
        using (var change = SnapshotReaders.TryBeginChange(mounted, "default", out _)) Assert.NotNull(change);
        using (var change = SnapshotReaders.TryBeginChangeAnywhere(out _)) Assert.NotNull(change);
    }

    [Fact]
    public async Task A_restore_blocks_cleanup_and_two_readers_share_the_destination()
    {
        var destination = NewDestination();
        using var first = await SnapshotReaders.HoldAsync(destination, "default", SnapshotReaders.Kind.Restore, null, CancellationToken.None);
        using var second = await SnapshotReaders.HoldAsync(destination, "default", SnapshotReaders.Kind.Restore, null, CancellationToken.None);
        using var mount = await SnapshotReaders.HoldAsync(destination, "default", SnapshotReaders.Kind.Mount, null, CancellationToken.None);
        mount.Dispose();
        Assert.Null(SnapshotReaders.TryBeginChange(destination, "default", out var reason));
        Assert.Equal("files are being restored from this backup", reason);
        // A restore alone does not refuse destination-wide changes: those
        // already wait for running restores through the maintenance lease.
        using (var anywhere = SnapshotReaders.TryBeginChangeAnywhere(out _)) Assert.NotNull(anywhere);
    }

    [Fact]
    public async Task A_mount_waits_while_a_cleanup_runs_and_says_why()
    {
        var destination = NewDestination();
        var change = SnapshotReaders.TryBeginChange(destination, "default", out _);
        Assert.NotNull(change);
        var said = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var mount = SnapshotReaders.HoldAsync(destination, "default", SnapshotReaders.Kind.Mount, said.Enqueue, CancellationToken.None);
        await Task.Delay(500);
        Assert.False(mount.IsCompleted);
        Assert.Contains("Waiting for the cleanup of this backup's storage to finish", said);
        change!.Dispose();
        using var held = await mount.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("", said.ToArray()[^1]);
    }

    [Fact]
    public async Task A_waiting_mount_can_be_cancelled()
    {
        var destination = NewDestination();
        using var change = SnapshotReaders.TryBeginChange(destination, "default", out _);
        using var cancel = new CancellationTokenSource(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SnapshotReaders.HoldAsync(destination, "default", SnapshotReaders.Kind.Mount, null, cancel.Token));
    }

    [Fact]
    public async Task Another_process_holding_a_mount_blocks_cleanup_here_until_it_ends()
    {
        // The background service and the window are separate processes:
        // the lock is the file, not anything in memory. A process that ends
        // (even killed) releases it.
        var destination = NewDestination();
        var path = SnapshotReaders.LockPath(SnapshotReaders.Kind.Mount, SnapshotReaders.Key(destination, "default"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var ready = path + ".ready";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
            $"$f=[IO.File]::Open('{path}','OpenOrCreate','Read','Read'); New-Item -ItemType File '{ready}' | Out-Null; Start-Sleep 60" })
            start.ArgumentList.Add(arg);
        using var holder = Process.Start(start)!;
        try
        {
            for (var i = 0; i < 300 && !File.Exists(ready); i++) await Task.Delay(50);
            Assert.True(File.Exists(ready), "The other process did not take the lock.");
            Assert.Null(SnapshotReaders.TryBeginChange(destination, "default", out var reason));
            Assert.Equal("a snapshot of this backup is mounted as a drive", reason);
            // A reader here can still share it.
            using (var reader = await SnapshotReaders.HoldAsync(destination, "default", SnapshotReaders.Kind.Restore, null, CancellationToken.None)) { }
            holder.Kill();
            await holder.WaitForExitAsync();
            // Windows closes a killed process's handles a moment after it exits.
            IDisposable? change = null;
            for (var i = 0; i < 100 && change is null; i++)
            {
                change = SnapshotReaders.TryBeginChange(destination, "default", out _);
                if (change is null) await Task.Delay(50);
            }
            Assert.NotNull(change);
            change!.Dispose();
        }
        finally
        {
            if (!holder.HasExited) holder.Kill();
            File.Delete(ready);
        }
    }

    [Fact]
    public async Task Exclusive_maintenance_is_refused_while_a_snapshot_is_mounted()
    {
        var destination = NewDestination();
        using (var mount = await SnapshotReaders.HoldAsync(destination, "default", SnapshotReaders.Kind.Mount, null, CancellationToken.None))
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ServiceLocator.Maintenance.AcquireAsync("Deleting «Photos» from «Drive»"));
            Assert.Equal("Deleting «Photos» from «Drive» has to wait: a snapshot of a backup is mounted as a drive. Unmount the drive or let the restore finish, then try again. Nothing was changed.",
                refused.Message);
            Assert.False(ServiceLocator.Maintenance.IsActive);
        }
        using var lease = await ServiceLocator.Maintenance.AcquireAsync("Deleting «Photos» from «Drive»");
        Assert.True(ServiceLocator.Maintenance.IsActive);
    }
}
