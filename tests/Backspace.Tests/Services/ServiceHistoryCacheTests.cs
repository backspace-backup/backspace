using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceHistoryCacheTests
{
    [Fact]
    public async Task Failed_second_page_keeps_the_complete_prior_cache()
    {
        using var workspace = new TempWorkspace();
        var directory = Path.Combine(workspace.Root, "cache");
        var old = new RunRecord { BackupId = "backup", Summary = "Old result" };
        var initial = new ServiceHistoryCache(directory, (_, _) => Task.FromResult(new ServiceHistoryProtocol.Response(1, new[] { old })));
        await initial.RefreshAsync("backup", CancellationToken.None);
        var recent = new RunRecord { BackupId = "backup", Summary = "New result" };
        var failed = new ServiceHistoryCache(directory, (request, _) => Task.FromResult(request.AfterRunId is null
            ? new ServiceHistoryProtocol.Response(1, new[] { recent }, recent.Id)
            : new ServiceHistoryProtocol.Response(1, Array.Empty<RunRecord>(), Error: "history-unavailable")));
        await Assert.ThrowsAsync<IOException>(() => failed.RefreshAsync("backup", CancellationToken.None));
        Assert.Equal(new[] { old.Id }, failed.ReadCached("backup").Select(r => r.Id));
    }

    [Fact]
    public async Task An_older_network_reply_cannot_overwrite_a_concurrent_desktop_save()
    {
        using var workspace = new TempWorkspace();
        var directory = Path.Combine(workspace.Root, "cache");
        var original = new RunRecord { BackupId = "backup", Summary = "Original" };
        var initial = new ServiceHistoryCache(directory, (_, _) => Task.FromResult(new ServiceHistoryProtocol.Response(1, new[] { original })));
        await initial.RefreshAsync("backup", CancellationToken.None);
        var pending = new TaskCompletionSource<ServiceHistoryProtocol.Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new ServiceHistoryCache(directory, (_, _) => pending.Task);
        var waiting = slow.RefreshAsync("backup", CancellationToken.None);
        var newer = new RunRecord { Id = original.Id, BackupId = "backup", Summary = "Newer saved result" };
        var fast = new ServiceHistoryCache(directory, (_, _) => Task.FromResult(new ServiceHistoryProtocol.Response(1, new[] { newer })));
        await fast.RefreshAsync("backup", CancellationToken.None);
        pending.SetResult(new(1, new[] { original }));
        await waiting;
        Assert.Equal("Newer saved result", slow.ReadCached("backup").Single().Summary);
    }

    [Fact]
    public async Task Log_store_receives_finished_runs_without_waiting_for_the_service_on_the_calling_thread()
    {
        using var workspace = new TempWorkspace();
        var reply = new TaskCompletionSource<ServiceHistoryProtocol.Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new ServiceHistoryCache(Path.Combine(workspace.Root, "cache"), (_, _) => reply.Task);
        var logs = new LogStore(cache);
        var update = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var appended = 0;
        logs.RunAppended += _ => appended++;
        logs.HistoryRefreshed += id => update.TrySetResult(id);
        Assert.Empty(logs.LoadTimelineByBackupId("background-only"));
        Assert.False(update.Task.IsCompleted);
        var finished = new RunRecord { BackupId = "background-only", Summary = "Service backup finished", Status = BackupRunStatus.Success };
        reply.SetResult(new(1, new[] { finished }));
        Assert.Equal("background-only", await update.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, appended);
        Assert.Equal(finished.Id, logs.LoadHistoryByBackupId("background-only").Single().Id);
        Assert.Equal(finished.Id, logs.LoadTimelineByBackupId("background-only").Single().Id);
        Assert.Empty(cache.ReadCached("unrelated"));
    }

    [Fact]
    public async Task Known_history_avoids_downloading_older_pages_and_unchanged_results_do_not_fire_updates()
    {
        using var workspace = new TempWorkspace();
        var directory = Path.Combine(workspace.Root, "cache");
        var record = new RunRecord { BackupId = "backup" };
        var older = new RunRecord { BackupId = "backup", StartedUtc = new DateTime(2020, 1, 1) };
        var calls = 0;
        var cache = new ServiceHistoryCache(directory, (request, _) =>
        {
            calls++; return Task.FromResult(request.AfterRunId is null
                ? new ServiceHistoryProtocol.Response(1, new[] { record }, record.Id)
                : new ServiceHistoryProtocol.Response(1, new[] { older }));
        });
        await cache.RefreshAsync("backup", CancellationToken.None);
        var updates = 0;
        cache.Refreshed += _ => updates++;
        await cache.RefreshAsync("backup", CancellationToken.None);
        Assert.Equal(3, calls);
        Assert.Equal(0, updates);
        Assert.Equal(2, cache.ReadCached("backup").Count);
    }

    [Fact]
    public void Explicit_review_configuration_does_not_connect_to_an_adjacent_service()
    {
        using var workspace = new TempWorkspace();
        Assert.Null(ServiceHistoryCache.ForDesktop(Path.Combine(workspace.Root, "application"), Path.Combine(workspace.Root, "isolated")));
    }
}
