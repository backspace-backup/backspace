using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services.Platform.Windows;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform;
using Task = System.Threading.Tasks.Task;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceScheduledRunTests
{
    private const string RunId = "0123456789abcdef0123456789abcdef";
    private static readonly ServiceHostRequest Start = new(1, "start-cli", RunId, "backup", "backup");
    private static ServiceScheduledRun.Response Running => new(1, RunId, "running", null, null);

    [Fact]
    public async Task Paused_schedule_does_not_ask_for_or_cancel_the_previous_run()
    {
        var calls = new List<string>();
        var code = await ServiceScheduledRun.RunAsync(Start, (request, _) =>
        {
            calls.Add(request.Command);
            return Task.FromResult(new ServiceScheduledRun.Response(1, null, "paused", 0, null));
        }, _ => throw new InvalidOperationException("A paused run must not poll."), TestContext.Current.CancellationToken);
        Assert.Equal(0, code);
        Assert.Equal(new[] { "start-cli" }, calls);
    }

    [Fact]
    public void The_desktop_asks_the_service_every_second_only_while_its_window_is_shown()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), BackgroundServiceActivity.IdleOrBusyDelay(windowShown: true));
        Assert.Equal(TimeSpan.FromSeconds(5), BackgroundServiceActivity.IdleOrBusyDelay(windowShown: false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task Dispatcher_waits_for_its_worker_and_preserves_the_real_exit_code(int exitCode)
    {
        var calls = new List<string>();
        var code = await ServiceScheduledRun.RunAsync(Start, (request, _) =>
        {
            calls.Add(request.Command);
            if (request.Command == "start-cli") return Task.FromResult(Running);
            Assert.Equal(RunId, request.RunId);
            return Task.FromResult(calls.Count < 3 ? Running :
                new ServiceScheduledRun.Response(1, RunId, exitCode == 0 ? "completed" : "failed", exitCode, "not-running"));
        }, _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        Assert.Equal(exitCode, code);
        Assert.Equal(new[] { "start-cli", "run-status", "run-status" }, calls);
    }

    [Theory]
    [InlineData("lost-response")]
    [InlineData("different-run")]
    [InlineData("rejected")]
    [InlineData("interrupted")]
    [InlineData("missing-exit")]
    [InlineData("failed-zero")]
    [InlineData("completed-error")]
    public async Task Uncertain_results_fail_and_cancel_only_the_original_run(string failure)
    {
        var calls = new List<ServiceHostRequest>();
        await Assert.ThrowsAsync<IOException>(() => ServiceScheduledRun.RunAsync(Start, (request, _) =>
        {
            calls.Add(request);
            if (request.Command is "start-cli" or "cancel") return Task.FromResult(Running);
            return failure switch
            {
                "lost-response" => throw new IOException("Connection closed"),
                "different-run" => Task.FromResult(Running with { RunId = Guid.NewGuid().ToString("N") }),
                "rejected" => Task.FromResult(Running with { Error = "request-rejected" }),
                "interrupted" => Task.FromResult(Running with { State = "interrupted" }),
                "failed-zero" => Task.FromResult(Running with { State = "failed", ExitCode = 0 }),
                "completed-error" => Task.FromResult(Running with { State = "completed", ExitCode = 0, Error = "request-rejected" }),
                _ => Task.FromResult(Running with { State = "completed" }),
            };
        }, _ => Task.CompletedTask, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "start-cli", "run-status", "cancel" }, calls.ConvertAll(x => x.Command));
        Assert.Equal(RunId, calls[^1].RunId);
    }

    /// <summary>U224 (owner, 13 November 2026: "The less we make the service
    /// and window tightly coupled, the better"): the run belongs to the
    /// service. Its state is asked only after each change the service
    /// announces, never on a timer, and nothing keeps it alive: no heartbeat
    /// request exists any more.</summary>
    [Fact]
    public async Task A_run_is_followed_by_its_changes_and_needs_no_heartbeat()
    {
        var log = new List<string>();
        var code = await ServiceScheduledRun.RunAsync(Start, (request, _) =>
        {
            log.Add(request.Command);
            if (request.Command == "start-cli") return Task.FromResult(Running);
            return Task.FromResult(log.Count < 6 ? Running : Running with { State = "completed", ExitCode = 0, Error = "not-running" });
        }, _ => { log.Add("change"); return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        Assert.Equal(0, code);
        Assert.Equal(new[] { "start-cli", "change", "run-status", "change", "run-status", "change", "run-status" }, log);
        Assert.ThrowsAny<Exception>(() => ServiceHostRequest.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new ServiceHostRequest(1, "heartbeat", RunId: RunId), ServiceRunAuthorization.Json)));
        Assert.Equal("run-status", ServiceHostRequest.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new ServiceHostRequest(1, "run-status", RunId: RunId), ServiceRunAuthorization.Json)).Command);
    }

    [Fact]
    public async Task Lost_start_reply_is_not_replayed_or_cancelled_using_a_guessed_identity()
    {
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => ServiceScheduledRun.RunAsync(Start, (_, _) =>
        { calls++; throw new IOException("Start response lost"); },
            _ => Task.CompletedTask, TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Explicit_busy_refusal_waits_then_starts_exactly_one_accepted_run()
    {
        var calls = new List<string>();
        var result = await ServiceScheduledRun.RunAsync(Start, (request, _) =>
        {
            calls.Add(request.Command);
            return Task.FromResult(calls.Count switch
            {
                1 => Running with { Error = "busy" },
                2 => Running,
                _ => Running with { State = "completed", ExitCode = 0 },
            });
        }, _ => Task.CompletedTask, TestContext.Current.CancellationToken);
        Assert.Equal(0, result);
        Assert.Equal(new[] { "start-cli", "start-cli", "run-status" }, calls);
    }

    /// <summary>U044, found offline in the VM on 30 October 2026: every task
    /// waited for a network, so a backup to a local or external disk never ran
    /// on schedule without one. The network condition applies only when a
    /// destination needs it.</summary>
    [Theory]
    [InlineData(DestinationKind.LocalFolder, @"D:\Backups", true, false)]
    [InlineData(DestinationKind.ExternalDrive, @"E:\Backups", true, false)]
    [InlineData(DestinationKind.LocalFolder, @"\\nas\backups", true, true)]
    [InlineData(DestinationKind.NetworkShare, @"\\nas\backups", true, true)]
    [InlineData(DestinationKind.DropboxAppScoped, "backups", true, true)]
    // U226: no internet setting any more: a backup that only reaches the
    // network always waits for one, whatever the old setting said.
    [InlineData(DestinationKind.DropboxAppScoped, "backups", false, true)]
    public void Only_backups_that_reach_the_network_wait_for_one(DestinationKind kind, string path, bool requireNetwork, bool expected)
    {
        var destination = new Destination { Name = "Target", Kind = kind, PathOrSubpath = path };
        var backup = new Backup { Name = "Photos", Schedule = new BackupSchedule { RequireNetwork = requireNetwork, SkipOnBattery = false } };
        backup.Targets.Add(new BackupTarget { DestinationId = destination.Id, StorageName = "default" });
        var plan = new SchedulePlan(backup, ScheduledTaskKind.Backup, backup.Schedule);
        // Offline: only a backup that reaches the network waits. The tray app
        // and the background service share this condition.
        Assert.Equal(expected, ScheduleConditions.Blocked(plan, new[] { destination }, () => false, () => false) is not null);
        Assert.Null(ScheduleConditions.Blocked(plan, new[] { destination }, () => false, () => true));
        // A destination that can't be found keeps the safe side: it waits (U226: always).
        Assert.NotNull(ScheduleConditions.Blocked(plan, Array.Empty<Destination>(), () => false, () => false));
    }
}
