using System;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services.Platform.Windows;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U224 (owner: "the app is not supposed to use any CPU when not in
/// use"): with the window hidden, the app makes one request that the service
/// answers only when something changes; it asks every second only while the
/// window is shown.</summary>
public sealed class ServiceActivityWaitTests
{
    private static ServiceActivity.Snapshot Idle(long sequence) => new(1, null, "idle", null, null, null, null, Sequence: sequence);

    [Fact]
    public async Task Hidden_the_app_waits_for_news_instead_of_asking()
    {
        var previous = BackgroundServiceActivity.WindowShown;
        BackgroundServiceActivity.WindowShown = false;
        int reads = 0, waits = 0;
        var news = new TaskCompletionSource<ServiceActivity.Snapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var changes = 0;
        void Changed() => Interlocked.Increment(ref changes);
        BackgroundServiceActivity.ActivityChanged += Changed;
        using var monitor = new BackgroundServiceActivity(
            _ => { Interlocked.Increment(ref reads); return Task.FromResult<ServiceActivity.Snapshot?>(Idle(1)); },
            (_, _) => Task.CompletedTask,
            async (known, token) =>
            {
                var n = Interlocked.Increment(ref waits);
                if (n == 1) return Idle(1);                       // the first answer: what is
                if (n == 2) return await news.Task.WaitAsync(token); // then: only when something changes
                await Task.Delay(Timeout.Infinite, token); return null;
            });
        try
        {
            monitor.StartPolling();
            await Task.Delay(3000);
            // Three seconds hidden and idle: no request answered since the second
            // was sent, none repeated, nothing read.
            Assert.Equal(0, reads);
            Assert.Equal(2, waits);
            Assert.Equal(1, monitor.Current?.Sequence);
            // Something changes: heard at once.
            news.SetResult(Idle(2));
            for (var i = 0; i < 50 && monitor.Current?.Sequence != 2; i++) await Task.Delay(20);
            Assert.Equal(2, monitor.Current?.Sequence);
            Assert.True(changes >= 2);
            // Shown: asked every second again.
            BackgroundServiceActivity.WindowShown = true;
            await Task.Delay(2500);
            Assert.InRange(reads, 2, 4);
        }
        finally
        {
            BackgroundServiceActivity.ActivityChanged -= Changed;
            BackgroundServiceActivity.WindowShown = previous;
        }
    }

    [Fact]
    public void The_service_accepts_a_wait_for_news_and_nothing_else_with_it()
    {
        var ok = ServiceHostRequest.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new ServiceHostRequest(1, "activity-wait", Revision: "42"), ServiceRunAuthorization.Json));
        Assert.Equal("42", ok.Revision);
        foreach (var bad in new[] { new ServiceHostRequest(1, "activity-wait", Revision: "x1"), new ServiceHostRequest(1, "activity-wait"),
                     new ServiceHostRequest(1, "activity-wait", Revision: "1", BackupId: "b") })
            Assert.ThrowsAny<Exception>(() => ServiceHostRequest.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(bad, ServiceRunAuthorization.Json)));
    }
}
