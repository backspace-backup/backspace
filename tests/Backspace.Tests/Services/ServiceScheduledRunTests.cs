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
    public async Task Paused_schedule_does_not_heartbeat_or_cancel_the_previous_run()
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
    public void Short_runs_are_observed_promptly_without_polling_long_runs_at_the_same_rate()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(250), ServiceScheduledRun.PollInterval(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromMilliseconds(250), ServiceScheduledRun.PollInterval(TimeSpan.FromSeconds(14)));
        Assert.Equal(TimeSpan.FromSeconds(5), ServiceScheduledRun.PollInterval(TimeSpan.FromSeconds(15)));
        Assert.Equal(TimeSpan.FromSeconds(5), ServiceScheduledRun.PollInterval(TimeSpan.FromHours(6)));
    }

    [Fact]
    public void Expired_lease_cannot_be_revived_by_a_late_heartbeat_or_clock_change()
    {
        var clock = new Clock();
        var lease = new ServiceRunLease(clock);
        lease.Begin(RunId);
        clock.Ticks += TimeSpan.FromSeconds(29).Ticks;
        Assert.False(lease.IsExpired);
        Assert.False(lease.Renew(Guid.NewGuid().ToString("N")));
        clock.Ticks += TimeSpan.FromSeconds(1).Ticks;
        Assert.True(lease.IsExpired);
        Assert.False(lease.Renew(RunId));
        clock.Ticks = 0;
        Assert.True(lease.IsExpired);
        lease.Clear();
        Assert.False(lease.IsExpired);
        Assert.False(lease.Renew(RunId));
    }

    [Fact]
    public void Valid_heartbeats_extend_only_the_matching_run()
    {
        var clock = new Clock();
        var lease = new ServiceRunLease(clock);
        lease.Begin(RunId);
        clock.Ticks += TimeSpan.FromSeconds(20).Ticks;
        Assert.True(lease.Renew(RunId));
        clock.Ticks += TimeSpan.FromSeconds(20).Ticks;
        Assert.False(lease.IsExpired);
        lease.Begin(Guid.NewGuid().ToString("N"));
        Assert.False(lease.Renew(RunId));
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
        Assert.Equal(new[] { "start-cli", "heartbeat", "heartbeat" }, calls);
    }

    [Theory]
    [InlineData("lost-response")]
    [InlineData("different-run")]
    [InlineData("lease-expired")]
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
                "lease-expired" => Task.FromResult(Running with { Error = "lease-expired" }),
                "interrupted" => Task.FromResult(Running with { State = "interrupted" }),
                "failed-zero" => Task.FromResult(Running with { State = "failed", ExitCode = 0 }),
                "completed-error" => Task.FromResult(Running with { State = "completed", ExitCode = 0, Error = "request-rejected" }),
                _ => Task.FromResult(Running with { State = "completed" }),
            };
        }, _ => Task.CompletedTask, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "start-cli", "heartbeat", "cancel" }, calls.ConvertAll(x => x.Command));
        Assert.Equal(RunId, calls[^1].RunId);
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
        Assert.Equal(new[] { "start-cli", "start-cli", "heartbeat" }, calls);
    }

    private sealed class Clock : TimeProvider
    {
        internal long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
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
    [InlineData(DestinationKind.DropboxAppScoped, "backups", false, false)]
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
        // A destination that can't be found keeps the safe side.
        Assert.Equal(requireNetwork, ScheduleConditions.Blocked(plan, Array.Empty<Destination>(), () => false, () => false) is not null);
    }
}
