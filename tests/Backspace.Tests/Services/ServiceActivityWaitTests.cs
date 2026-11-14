using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services.Platform.Windows;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U224 (owner, 13 November 2026: "I want this to be purely event
/// based! So no polling"): the app keeps one request open that the service
/// answers when something changes. Hidden, only when a run starts, stops or
/// ends; shown, every change. Nothing is asked on a timer, except once by the
/// watchdog while a run is going and nothing has been heard for twelve
/// heartbeats.</summary>
[Collection("BackgroundServiceActivity")]
public sealed class ServiceActivityWaitTests
{
    private static ServiceActivity.Snapshot Idle(long sequence, long state = 1) =>
        new(1, null, "idle", null, null, null, null, Sequence: sequence, StateSequence: state);
    private static ServiceActivity.Snapshot Running(long sequence, long state = 1) =>
        new(1, "0123456789abcdef0123456789abcdef", "running", "b", "backup", DateTime.UtcNow, null, Sequence: sequence, StateSequence: state);

    [Fact]
    public async Task Hidden_or_shown_the_app_waits_for_news_and_never_asks_on_a_timer()
    {
        var previous = BackgroundServiceActivity.WindowShown;
        BackgroundServiceActivity.WindowShown = false;
        int reads = 0;
        var waits = new ConcurrentQueue<(long Known, bool StateOnly)>();
        var answers = new BlockingCollection<ServiceActivity.Snapshot>();
        answers.Add(Idle(1));
        var changes = 0;
        void Changed() => Interlocked.Increment(ref changes);
        BackgroundServiceActivity.ActivityChanged += Changed;
        using var monitor = new BackgroundServiceActivity(
            _ => { Interlocked.Increment(ref reads); return Task.FromResult<ServiceActivity.Snapshot?>(Idle(1)); },
            (_, _) => Task.CompletedTask,
            async (known, stateOnly, token) =>
            {
                waits.Enqueue((known, stateOnly));
                return await Task.Run(() => answers.Take(token), token);
            });
        try
        {
            monitor.StartPolling();
            await Task.Delay(3000);
            // Three seconds hidden and idle: one read (nothing heard yet), then
            // waits from what it holds; nothing repeated. U246: the first wait
            // used to send -1, which the service refuses, so the window never
            // saw the service's runs: every wait now names a real sequence
            // the service accepts.
            Assert.Equal(1, reads);
            Assert.Equal(2, waits.Count);
            Assert.All(waits, w => Assert.True(w.StateOnly));
            Assert.All(waits, w => ServiceHostRequest.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new ServiceHostRequest(1, "state-wait", Revision: w.Known.ToString(System.Globalization.CultureInfo.InvariantCulture)), ServiceRunAuthorization.Json)));
            Assert.Equal(1, monitor.Current?.Sequence);

            // A run's progress alone doesn't reach a hidden app (the service
            // answers a state wait only on a start, stop or end); a start does, at once.
            answers.Add(Running(2, state: 2));
            for (var i = 0; i < 50 && monitor.Current?.Sequence != 2; i++) await Task.Delay(20);
            Assert.Equal(2, monitor.Current?.Sequence);
            Assert.True(changes >= 2);

            // Shown: it waits for every change, still without asking on a timer.
            var before = waits.Count;
            BackgroundServiceActivity.WindowShown = true;
            await Task.Delay(2500);
            Assert.Equal(1, reads);
            Assert.Equal(before + 1, waits.Count);
            Assert.False(waits.ToArray()[^1].StateOnly);
            answers.Add(Running(3, state: 2));
            for (var i = 0; i < 50 && monitor.Current?.Sequence != 3; i++) await Task.Delay(20);
            Assert.Equal(3, monitor.Current?.Sequence);
        }
        finally
        {
            BackgroundServiceActivity.ActivityChanged -= Changed;
            BackgroundServiceActivity.WindowShown = previous;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task While_a_run_goes_silent_the_watchdog_asks_once(bool stillMoving)
    {
        var (previous, watchdog) = (BackgroundServiceActivity.WindowShown, BackgroundServiceActivity.Watchdog);
        BackgroundServiceActivity.WindowShown = false;
        BackgroundServiceActivity.Watchdog = TimeSpan.FromMilliseconds(400);
        int reads = 0, waits = 0;
        using var monitor = new BackgroundServiceActivity(
            _ => { Interlocked.Increment(ref reads); return Task.FromResult<ServiceActivity.Snapshot?>(Running(stillMoving ? 50 : 1)); },
            (_, _) => Task.CompletedTask,
            async (_, _, token) =>
            {
                if (Interlocked.Increment(ref waits) == 1) return Running(1);
                await Task.Delay(Timeout.Infinite, token); return null; // the service says nothing
            });
        try
        {
            monitor.StartPolling();
            await Task.Delay(650);
            // The first read (nothing heard yet), then silent past the
            // watchdog: asked once, then waiting again.
            Assert.Equal(2, reads);
            Assert.NotNull(monitor.LastWatchdogUtc);
            // A run whose progress moved is fine; one that didn't is said to be unconfirmed.
            if (stillMoving) Assert.Null(monitor.Error);
            else Assert.Contains("has not reported any progress", monitor.Error);
        }
        finally
        {
            BackgroundServiceActivity.WindowShown = previous;
            BackgroundServiceActivity.Watchdog = watchdog;
        }
    }

    [Fact]
    public async Task Idle_and_hidden_nothing_wakes_the_watchdog()
    {
        var (previous, watchdog) = (BackgroundServiceActivity.WindowShown, BackgroundServiceActivity.Watchdog);
        BackgroundServiceActivity.WindowShown = false;
        BackgroundServiceActivity.Watchdog = TimeSpan.FromMilliseconds(200);
        int reads = 0, waits = 0;
        using var monitor = new BackgroundServiceActivity(
            _ => { Interlocked.Increment(ref reads); return Task.FromResult<ServiceActivity.Snapshot?>(Idle(1)); },
            (_, _) => Task.CompletedTask,
            async (_, _, token) =>
            {
                if (Interlocked.Increment(ref waits) == 1) return Idle(1);
                await Task.Delay(Timeout.Infinite, token); return null;
            });
        try
        {
            monitor.StartPolling();
            await Task.Delay(1200);
            Assert.Equal(1, reads); // the first look, nothing since
            Assert.Equal(2, waits);
            Assert.Null(monitor.LastWatchdogUtc);
        }
        finally
        {
            BackgroundServiceActivity.WindowShown = previous;
            BackgroundServiceActivity.Watchdog = watchdog;
        }
    }

    /// <summary>The service side, on a real pipe: a change answers at once
    /// and the pipe still carries the answer and its receipt; a desktop that
    /// goes away ends the wait, so it never holds one of the service's connections.</summary>
    [Fact]
    public async Task The_service_answers_on_change_and_lets_go_of_a_desktop_that_left()
    {
        var name = "backspace-test-" + Guid.NewGuid().ToString("N");
        long sequence = 7;
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // 1. A change: answered, then the reply and its receipt go through.
        using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await Task.WhenAll(server.WaitForConnectionAsync(timeout.Token), client.ConnectAsync(timeout.Token));
            var wait = SystemBackupService.WaitUntilChangedAsync(() => Interlocked.Read(ref sequence), 7, () => changed.Task, server, timeout.Token);
            await Task.Delay(300);
            Assert.False(wait.IsCompleted);
            Interlocked.Increment(ref sequence); changed.TrySetResult();
            await wait.WaitAsync(timeout.Token);
            var reply = BackupServicePipe.SendResponseAsync(server, new byte[] { 1, 2, 3 }, TimeSpan.FromSeconds(5), timeout.Token);
            Assert.Equal(new byte[] { 1, 2, 3 }, await BackupServicePipe.ReadResponseAsync(client, TimeSpan.FromSeconds(5), timeout.Token));
            await reply;
        }

        // 2. The desktop leaves: the wait ends at once.
        using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        {
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.WhenAll(server.WaitForConnectionAsync(timeout.Token), client.ConnectAsync(timeout.Token));
            var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var wait = SystemBackupService.WaitUntilChangedAsync(() => 1, 1, () => never.Task, server, timeout.Token);
            await Task.Delay(300);
            Assert.False(wait.IsCompleted);
            client.Dispose();
            await Assert.ThrowsAsync<IOException>(() => wait.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void The_service_accepts_a_wait_for_news_and_nothing_else_with_it()
    {
        foreach (var command in new[] { "activity-wait", "state-wait" })
        {
            var ok = ServiceHostRequest.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new ServiceHostRequest(1, command, Revision: "42"), ServiceRunAuthorization.Json));
            Assert.Equal("42", ok.Revision);
            // U246: a desktop that has heard nothing yet ("-1") is answered, never refused.
            Assert.Equal("-1", ServiceHostRequest.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new ServiceHostRequest(1, command, Revision: "-1"), ServiceRunAuthorization.Json)).Revision);
            foreach (var bad in new[] { new ServiceHostRequest(1, command, Revision: "x1"), new ServiceHostRequest(1, command),
                         new ServiceHostRequest(1, command, Revision: "-"), new ServiceHostRequest(1, command, Revision: "1-2"),
                         new ServiceHostRequest(1, command, Revision: "1", BackupId: "b") })
                Assert.ThrowsAny<Exception>(() => ServiceHostRequest.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(bad, ServiceRunAuthorization.Json)));
        }
    }
}
