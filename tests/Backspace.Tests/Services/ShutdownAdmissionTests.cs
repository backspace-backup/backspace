using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ShutdownAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_engine_keeps_OS_gate_after_logical_reader_or_writer_drains(bool exclusive)
    {
        using var workspace = new TempWorkspace();
        var coordinator = new MaintenanceCoordinator();
        var lease = exclusive ? await coordinator.AcquireAsync("owned maintenance")
            : await coordinator.AcquireReadAsync(default);
        try
        {
            coordinator.BlockUntilRestart("Fixture engine did not acknowledge stop.");
            lease.Dispose();
            Assert.False(coordinator.HasReaders);
            Assert.False(coordinator.IsActive);
            var gate = Path.Combine(workspace.ConfigRoot, "operations.lock");
            Assert.Throws<IOException>(() => new FileStream(gate, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            var independent = new MaintenanceCoordinator();
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => independent.AcquireAsync("other process maintenance", ct: timeout.Token));
            coordinator.ReleaseEngineOwnershipAfterStop();
            using var released = new FileStream(gate, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(coordinator.IsBlocked);
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.AcquireReadAsync(default));
        }
        finally
        {
            lease.Dispose();
            coordinator.ReleaseEngineOwnershipAfterStop();
        }
    }

    [Fact]
    public async Task Confirmed_shutdown_rejects_new_work_but_keeps_existing_ownership_until_drain()
    {
        using var workspace = new TempWorkspace();
        var coordinator = new MaintenanceCoordinator();
        var reader = await coordinator.AcquireReadAsync(default);
        Assert.True(coordinator.TryBeginShutdown(out var shutdown));
        using (shutdown)
        {
            Assert.True(coordinator.IsShuttingDown);
            Assert.False(coordinator.TryBeginShutdown(out var duplicate));
            Assert.Null(duplicate);
            Assert.True(coordinator.HasReaders);
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.AcquireReadAsync(default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.AcquireAsync("must not start"));
            Assert.False(coordinator.IsActive);
            Assert.True(coordinator.HasReaders);
            reader.Dispose();
            Assert.False(coordinator.HasReaders);
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.AcquireReadAsync(default));
        }
        Assert.False(coordinator.IsShuttingDown);
        using var resumed = await coordinator.AcquireReadAsync(default);
    }

    [Fact]
    public async Task Queued_reader_and_writer_cannot_start_after_shutdown_wins_admission()
    {
        using var workspace = new TempWorkspace();
        var coordinator = new MaintenanceCoordinator();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var existingWriter = await coordinator.AcquireAsync("already running", ct: timeout.Token);
        var queuedReader = coordinator.AcquireReadAsync(timeout.Token);
        var queuedWriter = coordinator.AcquireAsync("queued maintenance", ct: timeout.Token);
        Assert.False(queuedReader.IsCompleted);
        Assert.False(queuedWriter.IsCompleted);
        Assert.True(coordinator.TryBeginShutdown(out var shutdown));
        using (shutdown)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => queuedReader.WaitAsync(timeout.Token));
            Assert.True(coordinator.IsActive);
            Assert.False(queuedWriter.IsCompleted);
            existingWriter.Dispose();
            await Assert.ThrowsAsync<InvalidOperationException>(() => queuedWriter.WaitAsync(timeout.Token));
            Assert.False(coordinator.IsActive);
        }
        using var resumedWriter = await coordinator.AcquireAsync("quit cancelled", ct: timeout.Token);
        Assert.True(coordinator.IsActive);
    }

    [Fact]
    public async Task Shutdown_admission_is_local_and_cancelled_quit_restores_admission_once()
    {
        using var workspace = new TempWorkspace();
        var quitting = new MaintenanceCoordinator();
        var independent = new MaintenanceCoordinator();
        Assert.True(quitting.TryBeginShutdown(out var shutdown));
        using (var otherReader = await independent.AcquireReadAsync(default))
        {
            Assert.True(independent.HasReaders);
            Assert.False(independent.IsShuttingDown);
        }
        using (var otherWriter = await independent.AcquireAsync("independent scheduler maintenance"))
            Assert.True(independent.IsActive);
        shutdown!.Dispose();
        shutdown.Dispose();
        Assert.False(quitting.IsShuttingDown);
        using var allowed = await quitting.AcquireReadAsync(default);
        Assert.True(quitting.HasReaders);
    }
}
