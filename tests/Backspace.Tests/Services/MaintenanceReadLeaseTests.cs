using System;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class MaintenanceReadLeaseTests
{
    [Fact]
    public async Task MaintenanceWaitsForExistingReader_AndBlocksNewReaders()
    {
        var coordinator = new MaintenanceCoordinator();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reader = await coordinator.AcquireReadAsync(timeout.Token);
        var writerTask = coordinator.AcquireAsync("fixture maintenance", ct: timeout.Token);
        await Task.Delay(150, timeout.Token);
        Assert.True(coordinator.IsActive);
        Assert.False(writerTask.IsCompleted);
        var nextReader = coordinator.AcquireReadAsync(timeout.Token);
        Assert.False(nextReader.IsCompleted);
        reader.Dispose();
        var writer = await writerTask.WaitAsync(timeout.Token);
        Assert.False(nextReader.IsCompleted);
        writer.Dispose();
        using var releasedReader = await nextReader.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task CancellationWhileWaitingForReader_DoesNotReleaseItsOwnership()
    {
        var coordinator = new MaintenanceCoordinator();
        using var reader = await coordinator.AcquireReadAsync(default);
        using var cancellation = new CancellationTokenSource(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.AcquireAsync("cancelled maintenance", ct: cancellation.Token));
        Assert.False(coordinator.IsActive);
        Assert.True(coordinator.HasReaders);
    }
}
