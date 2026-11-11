using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Backspace.Tests.Services;

/// <summary>
/// U156: the background service ran one worker at a time, so local_d waited
/// for local_c unreported while its card said "Running…". The service now
/// runs several workers and reports each run; a start it can't take yet is
/// reported as queued; due schedules start together.
/// </summary>
public sealed class BackgroundQueueTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "backspace-u156-schedule-" + Guid.NewGuid().ToString("N") + ".json");
    public void Dispose() { try { File.Delete(_state); } catch { } }

    private const string RunId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task A_start_the_service_cannot_take_yet_is_reported_as_queued_then_cleared()
    {
        var replies = new Queue<ServiceScheduledRun.Response>([
            new(1, null, "running", null, "busy"), new(1, null, "running", null, "busy"),
            new(1, RunId, "running", null, null), new(1, RunId, "completed", 0, null)]);
        var queued = new List<string>();
        var code = await ServiceScheduledRun.RunAsync(new(1, "start-cli", RunId, "backup", "backup"),
            (_, _) => Task.FromResult(replies.Dequeue()), _ => Task.CompletedTask, CancellationToken.None, queued: queued.Add);
        Assert.Equal(0, code);
        Assert.Equal([ServiceScheduledRun.BusyReason, ""], queued);
        Assert.Equal("waiting for the background service to start it", ServiceScheduledRun.BusyReason);

        // Not busy: nothing is said.
        replies = new([new(1, RunId, "running", null, null), new(1, RunId, "completed", 0, null)]);
        queued.Clear();
        await ServiceScheduledRun.RunAsync(new(1, "start-cli", RunId, "backup", "backup"),
            (_, _) => Task.FromResult(replies.Dequeue()), _ => Task.CompletedTask, CancellationToken.None, queued: queued.Add);
        Assert.Empty(queued);
    }

    private static ServiceActivity.Snapshot Run(string backup, string state, DateTime started, string queued = "") =>
        new(1, Guid.NewGuid().ToString("N"), state, backup, "backup", started, new(backup, 10, queued, [], []));

    [Fact]
    public void The_activity_reports_each_run_and_a_card_finds_its_own()
    {
        var now = DateTime.UtcNow;
        var c = Run("local_c", "running", now.AddMinutes(-16));
        var d = Run("local_d", "running", now.AddMinutes(-15), "waiting for local_c (same destination)");
        var oldD = Run("local_d", "completed", now.AddDays(-1));
        var activity = ServiceActivity.Parse(ServiceActivity.Encode(d with { Runs = [oldD, c, d] }));
        Assert.Equal(c.RunId, ServiceActivity.ForBackup(activity, "local_c")!.RunId);
        // The running one, not yesterday's.
        var mine = ServiceActivity.ForBackup(activity, "local_d")!;
        Assert.Equal(d.RunId, mine.RunId);
        Assert.Equal("waiting for local_c (same destination)", mine.Progress!.Queued);
        // A backup with no run gets the report itself: a card that followed
        // a run sees that its run has gone.
        Assert.Equal(d.RunId, ServiceActivity.ForBackup(activity, "photos")!.RunId);
        Assert.True(ServiceActivity.AnyRunning(activity));
        Assert.False(ServiceActivity.AnyRunning(oldD with { Runs = [oldD] }));
        // An older service's report, with no list: the report itself.
        Assert.Same(c, ServiceActivity.ForBackup(c, "local_d"));

        // A list inside a listed run, or a malformed run, is refused.
        Assert.Throws<InvalidDataException>(() => ServiceActivity.Parse(ServiceActivity.Encode(c with { Runs = [c with { Runs = [c] }] })));
        Assert.Throws<InvalidDataException>(() => ServiceActivity.Parse(ServiceActivity.Encode(c with { Runs = [c with { State = "lost" }] })));
        Assert.Throws<InvalidDataException>(() => ServiceActivity.ParseProgress(ServiceActivity.Encode(c.Progress! with { QueuedSeconds = -1 }), "local_c"));
    }

    [Fact]
    public async Task Due_backups_start_together_when_runs_may_overlap_and_each_saves_its_own_outcome()
    {
        var now = new DateTime(2026, 11, 4, 19, 0, 0, DateTimeKind.Local).ToUniversalTime();
        Backup Daily(string name) => new()
        {
            Name = name,
            Schedule = new BackupSchedule { Frequency = ScheduleFrequency.Daily, TimeOfDay = TimeSpan.FromHours(20), RequireNetwork = false },
        };
        var a = Daily("A"); var b = Daily("B");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var loop = new ScheduleLoop(new(_state, () => SchedulePlan.From(new AppConfig { Backups = [a, b] }),
            async (_, ct) => { Interlocked.Increment(ref started); await release.Task.WaitAsync(ct); return true; },
            _ => null, () => false, () => now, TimeSpan.FromMinutes(1), MaxParallel: () => 8));
        await loop.TickAsync(default); // first sight: next is 20:00
        now = new DateTime(2026, 11, 4, 20, 0, 0, DateTimeKind.Local).ToUniversalTime();
        await loop.TickAsync(default).WaitAsync(TimeSpan.FromSeconds(10)); // returns while both run
        for (var i = 0; i < 100 && Volatile.Read(ref started) < 2; i++) await Task.Delay(50);
        Assert.Equal(2, started);
        Assert.All(ScheduleState.Load(_state).Entries.Values, e => Assert.NotNull(e.RunningSlotUtc));
        await loop.TickAsync(default); // neither starts twice
        Assert.Equal(2, started);
        release.SetResult();
        for (var i = 0; i < 100 && ScheduleState.Load(_state).Entries.Values.Any(e => e.RunningSlotUtc is not null); i++) await Task.Delay(50);
        var entries = ScheduleState.Load(_state).Entries.Values.ToList();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => { Assert.Null(e.RunningSlotUtc); Assert.True(e.NextUtc > now); });
    }
}
