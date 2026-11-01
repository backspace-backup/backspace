using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Microsoft.Win32.TaskScheduler;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Backspace.Tests.Services;

public sealed class ServiceNotificationTests
{
    private static BackgroundNotification Notice() => new(Guid.NewGuid().ToString("N"), DateTime.UtcNow,
        "Backspace: Documents failed", "Open the saved run for the reason.", NotificationSeverity.Failure,
        true, false, "backup-one", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Pending_alerts_survive_restart_and_stale_receipts_cannot_remove_a_new_delivery()
    {
        using var ws = new TempWorkspace(); var now = DateTime.UtcNow;
        var queue = new ServiceNotificationQueue(ws.Root, () => now); var first = Notice(); var second = Notice();
        queue.Add(first); queue.Add(first); queue.Add(second);
        var claim = queue.Take()!; Assert.Equal(first, claim.Notice);
        queue = new(ws.Root, () => now); Assert.Null(queue.Take());
        Assert.False(queue.Acknowledge(second.Id, claim.Receipt));
        now = now.AddMinutes(2); var retry = queue.Take()!;
        Assert.Equal(first, retry.Notice); Assert.NotEqual(claim.Receipt, retry.Receipt);
        Assert.False(queue.Acknowledge(first.Id, claim.Receipt));
        Assert.True(queue.Acknowledge(first.Id, retry.Receipt));
        var next = queue.Take()!; Assert.Equal(second, next.Notice);
        Assert.True(queue.Acknowledge(second.Id, next.Receipt)); Assert.Null(queue.Take());
    }

    [Fact]
    public void Publication_retries_temporary_Windows_access_errors_but_never_deletes_retained_state()
    {
        var attempts = 0;
        ServiceNotificationQueue.Publish("pending", "saved", (_, _) =>
        { if (++attempts < 3) throw new UnauthorizedAccessException(); }, _ => { });
        Assert.Equal(3, attempts);
        attempts = 0;
        Assert.Throws<UnauthorizedAccessException>(() => ServiceNotificationQueue.Publish("pending", "saved", (_, _) =>
        { attempts++; throw new UnauthorizedAccessException(); }, _ => { }));
        Assert.Equal(6, attempts);
    }

    [Fact]
    public async Task Concurrent_producers_preserve_every_pending_alert()
    {
        using var ws = new TempWorkspace();
        await Task.WhenAll(System.Linq.Enumerable.Range(0, 12).Select(_ => Task.Run(() => new ServiceNotificationQueue(ws.Root).Add(Notice()))));
        var queue = new ServiceNotificationQueue(ws.Root); var count = 0;
        while (queue.Take() is { } claim) { Assert.True(queue.Acknowledge(claim.Notice.Id, claim.Receipt)); count++; }
        Assert.Equal(12, count);
    }

    [Fact]
    public void Corrupt_state_is_preserved_instead_of_resetting_pending_alerts()
    {
        using var ws = new TempWorkspace(); var path = Path.Combine(ws.Root, "desktop-notifications.json");
        File.WriteAllText(path, "broken original");
        Assert.Throws<JsonException>(() => new ServiceNotificationQueue(ws.Root).Add(Notice()));
        Assert.Equal("broken original", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("{\"version\":1,\"action\":\"take\",\"path\":\"C:/secret\"}")]
    [InlineData("{\"version\":1,\"action\":\"take\",\"action\":\"ack\"}")]
    [InlineData("{\"version\":1,\"action\":\"ack\",\"id\":\"../private\"}")]
    [InlineData("{\"version\":1,\"action\":\"delete-all\"}")]
    public void Protocol_rejects_paths_ambiguous_requests_and_broad_deletions(string text)
        => Assert.ThrowsAny<Exception>(() => ServiceNotificationProtocol.Parse(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public async Task Locked_desktop_and_failed_presentation_never_acknowledge_an_alert()
    {
        using var ws = new TempWorkspace(); var now = DateTime.UtcNow;
        var queue = new ServiceNotificationQueue(ws.Root, () => now); var notice = Notice(); queue.Add(notice);
        var protocol = new ServiceNotificationProtocol(queue); var ready = false; var calls = 0; var shown = 0; var fail = true;
        using var monitor = new BackgroundServiceNotifications((request, _) =>
        {
            calls++;
            return Task.FromResult(JsonSerializer.Deserialize<ServiceNotificationProtocol.Response>(
                protocol.Dispatch(JsonSerializer.SerializeToUtf8Bytes(request, ServiceRunAuthorization.Json)), ServiceRunAuthorization.Json)!);
        }, (received, _) =>
        {
            Assert.Equal(notice, received);
            if (fail) throw new IOException("Window was not shown");
            shown++; return Task.CompletedTask;
        }, () => ready);
        Assert.False(await monitor.DeliverOneAsync(default)); Assert.Equal(0, calls);
        ready = true; await Assert.ThrowsAsync<IOException>(() => monitor.DeliverOneAsync(default));
        Assert.Equal(1, calls); Assert.Equal(0, shown);
        now = now.AddMinutes(2); fail = false;
        Assert.True(await monitor.DeliverOneAsync(default)); Assert.Equal(1, shown);
        Assert.False(await monitor.DeliverOneAsync(default)); Assert.Null(queue.Take());
    }

    [Fact]
    public async Task Worker_notification_waits_for_durable_write_and_never_plays_or_shows_it_locally()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifier = new NotificationService(new ConfigStore()); BackgroundNotification? saved = null;
        notifier.NotificationObserved = (_, _, _, _, _) => throw new Exception("Worker attempted desktop delivery");
        notifier.DurableDelivery = n => { saved = n; return pending.Task; };
        var run = Guid.NewGuid().ToString("N");
        var task = notifier.NotifyAsync("failed", "details", NotificationSeverity.Failure, null, null,
            new NotificationChannels { Toast = true, Sound = true, Email = true }, "backup", run);
        Assert.False(task.IsCompleted); Assert.Equal(run, saved!.RunId); Assert.Equal("backup", saved.BackupId);
        pending.SetResult(); await task;
        saved = null;
        await notifier.NotifyAsync("muted", "details", NotificationSeverity.Failure, null, null,
            new NotificationChannels { Toast = false, Sound = false, Email = true });
        Assert.Null(saved);
    }
}
