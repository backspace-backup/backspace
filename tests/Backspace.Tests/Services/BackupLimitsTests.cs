using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U156 (7 November 2026): "Backups at once per destination" (default 1) and
/// "in total" (default 2). A backup waiting its turn says why ("Queued:
/// waiting for local_c (same destination)", "Queued: 2 backups already
/// running"); cleanup and checks wait for the destination alone.
/// </summary>
public sealed class BackupLimitsTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static string Gate() => BackupOrchestrator.MutexNameForDestination(Guid.NewGuid().ToString("N"), "default");

    private static Task<RepositoryAccess> Take(RepositoryAccess.Kind kind, string gate, string who, BackupLimits limits,
        ConcurrentQueue<string>? reasons = null, CancellationToken ct = default, bool prepared = true)
        => Prepared(RepositoryAccess.AcquireAsync(kind, gate, who, "Google Drive", limits, r => reasons?.Enqueue(who + ": " + r), ct), prepared);

    // Like the engine once the repository exists: the next backup may prepare.
    private static async Task<RepositoryAccess> Prepared(Task<RepositoryAccess> taking, bool prepared)
    {
        var access = await taking;
        if (prepared) using (access.Enter()) RepositoryAccess.SetupDone();
        return access;
    }

    [Fact]
    public async Task One_backup_at_a_time_prepares_a_repository()
    {
        var gate = Gate(); var limits = new BackupLimits { PerDestination = 2, Total = 8 }; var reasons = new ConcurrentQueue<string>();
        using var a = await Take(RepositoryAccess.Kind.Backup, gate, "A", limits, reasons, prepared: false).WaitAsync(Patience);
        var b = Take(RepositoryAccess.Kind.Backup, gate, "B", limits, reasons);
        await Settle();
        Assert.False(b.IsCompleted);
        Assert.Contains("B: waiting for A (same destination)", reasons);
        using (a.Enter()) RepositoryAccess.SetupDone();
        using var both = await b.WaitAsync(Patience); // A still backs up: they share it
    }

    private static async Task Settle() => await Task.Delay(RepositoryAccess.PollInterval * 3);

    [Fact]
    public void The_defaults_are_one_per_destination_and_two_in_total()
    {
        var limits = new AppConfig().Limits;
        Assert.Equal(1, limits.PerDestination);
        Assert.Equal(2, limits.Total);
        Assert.Equal(1, new BackupLimits { PerDestination = 0 }.PerDestinationClamped);
        Assert.Equal(BackupLimits.Maximum, new BackupLimits { Total = 99 }.TotalClamped);
    }

    [Fact]
    public void The_reasons_read_as_the_owner_asked()
    {
        Assert.Equal("waiting for local_c (same destination)", RepositoryAccess.DestinationReason(["local_c"], "Google Drive", cleanup: false));
        Assert.Equal("waiting for local_c and local_e (same destination)", RepositoryAccess.DestinationReason(["local_c", "local_e"], "Google Drive", cleanup: false));
        Assert.Equal("waiting for another backup to «Google Drive»", RepositoryAccess.DestinationReason([], "Google Drive", cleanup: false));
        Assert.Equal("waiting for local_d (same destination) before cleanup and checks", RepositoryAccess.DestinationReason(["local_d"], "Google Drive", cleanup: true));
        Assert.Equal("2 backups already running", RepositoryAccess.TotalReason(2));
        Assert.Equal("1 backup already running", RepositoryAccess.TotalReason(1));
    }

    [Fact]
    public async Task With_one_per_destination_the_second_backup_waits_naming_the_first_then_runs()
    {
        var gate = Gate(); var limits = new BackupLimits { PerDestination = 1, Total = 8 }; var reasons = new ConcurrentQueue<string>();
        var first = await Take(RepositoryAccess.Kind.Backup, gate, "local_c", limits, reasons);
        var second = Take(RepositoryAccess.Kind.Backup, gate, "local_d", limits, reasons);
        await Settle();
        Assert.False(second.IsCompleted);
        Assert.Contains("local_d: waiting for local_c (same destination)", reasons);
        first.Dispose();
        using var running = await second.WaitAsync(Patience);
        Assert.Equal("local_d: ", reasons.Last()); // "" clears "Queued" once it runs
        Assert.DoesNotContain(reasons, r => r.StartsWith("local_c", StringComparison.Ordinal)); // the first never queued
    }

    [Fact]
    public async Task With_two_per_destination_two_backups_share_it_and_a_third_waits()
    {
        var gate = Gate(); var limits = new BackupLimits { PerDestination = 2, Total = 8 }; var reasons = new ConcurrentQueue<string>();
        using var a = await Take(RepositoryAccess.Kind.Backup, gate, "A", limits, reasons).WaitAsync(Patience);
        var b = await Take(RepositoryAccess.Kind.Backup, gate, "B", limits, reasons).WaitAsync(Patience);
        Assert.Empty(reasons);
        var c = Take(RepositoryAccess.Kind.Backup, gate, "C", limits, reasons);
        await Settle();
        Assert.False(c.IsCompleted);
        Assert.Contains("C: waiting for A and B (same destination)", reasons);
        b.Dispose();
        using var third = await c.WaitAsync(Patience);
    }

    [Fact]
    public async Task The_total_limit_counts_every_destination_and_a_queued_backup_holds_no_total_slot()
    {
        var limits = new BackupLimits { PerDestination = 1, Total = 2 }; var reasons = new ConcurrentQueue<string>();
        var d1 = Gate(); var d2 = Gate(); var d3 = Gate();
        using var a = await Take(RepositoryAccess.Kind.Backup, d1, "A", limits, reasons).WaitAsync(Patience);
        // B waits for A's destination: it must not take the second total slot.
        var b = Take(RepositoryAccess.Kind.Backup, d1, "B", limits, reasons);
        await Settle();
        Assert.False(b.IsCompleted);
        // C, to another destination, runs alongside A.
        var c = await Take(RepositoryAccess.Kind.Backup, d2, "C", limits, reasons).WaitAsync(Patience);
        // D, to a third destination, waits for the total.
        var d = Take(RepositoryAccess.Kind.Backup, d3, "D", limits, reasons);
        await Settle();
        Assert.False(d.IsCompleted);
        Assert.Contains("D: 2 backups already running", reasons);
        Assert.DoesNotContain(reasons, r => r.StartsWith("C:", StringComparison.Ordinal) && r.Length > 3);
        c.Dispose();
        using var fourth = await d.WaitAsync(Patience);
        Assert.False(b.IsCompleted);
        a.Dispose();
        // A gone: B takes its destination and its total slot.
        using var second = await b.WaitAsync(Patience);
    }

    [Fact]
    public async Task Cleanup_waits_for_the_backups_on_its_destination_and_new_ones_wait_for_it()
    {
        var gate = Gate(); var limits = new BackupLimits { PerDestination = 2, Total = 8 }; var reasons = new ConcurrentQueue<string>();
        var running = await Take(RepositoryAccess.Kind.Backup, gate, "local_d", limits, reasons).WaitAsync(Patience);
        var cleanup = Take(RepositoryAccess.Kind.Exclusive, gate, "local_c", limits, reasons);
        await Settle();
        Assert.False(cleanup.IsCompleted);
        Assert.Contains("local_c: waiting for local_d (same destination) before cleanup and checks", reasons);
        // A backup arriving now waits behind the cleanup, though a slot is free.
        var late = Take(RepositoryAccess.Kind.Backup, gate, "local_e", limits, reasons);
        await Settle();
        Assert.False(late.IsCompleted);
        running.Dispose();
        var alone = await cleanup.WaitAsync(Patience);
        await Settle();
        Assert.False(late.IsCompleted);
        alone.Dispose();
        using var after = await late.WaitAsync(Patience);
    }

    [Fact]
    public async Task Two_backups_sharing_a_destination_both_reach_cleanup_without_deadlock()
    {
        var gate = Gate(); var limits = new BackupLimits { PerDestination = 2, Total = 8 }; var reasons = new ConcurrentQueue<string>();
        var a = await Take(RepositoryAccess.Kind.Backup, gate, "A", limits, reasons).WaitAsync(Patience);
        var b = await Take(RepositoryAccess.Kind.Backup, gate, "B", limits, reasons).WaitAsync(Patience);
        var alone = 0; var peak = 0;
        async Task CleanUp(RepositoryAccess access)
        {
            using (access.Enter()) await RepositoryAccess.ExclusiveAsync(CancellationToken.None);
            var now = Interlocked.Increment(ref alone);
            lock (reasons) peak = Math.Max(peak, now);
            await Task.Delay(300);
            Interlocked.Decrement(ref alone);
            access.Dispose();
        }
        await Task.WhenAll(CleanUp(a), CleanUp(b)).WaitAsync(Patience);
        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task Cancelling_a_queued_wait_clears_its_queued_state_and_holds_nothing()
    {
        var gate = Gate(); var limits = new BackupLimits { PerDestination = 1, Total = 8 }; var reasons = new ConcurrentQueue<string>();
        var first = await Take(RepositoryAccess.Kind.Backup, gate, "first", limits, reasons);
        using var cancel = new CancellationTokenSource();
        var waiting = Take(RepositoryAccess.Kind.Backup, gate, "second", limits, reasons, cancel.Token);
        await Settle();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Patience));
        Assert.Equal("second: ", reasons.Last());
        first.Dispose();
        using var again = await Take(RepositoryAccess.Kind.Backup, gate, "third", limits).WaitAsync(Patience);
    }

    // ---- through the orchestrator ----

    private sealed class Rig : IDisposable
    {
        internal readonly TempWorkspace Workspace = new("u156-limits");
        internal readonly ConfigStore Config = new();
        internal readonly InMemoryBackupEngine Engine = new();
        internal readonly BackupOrchestrator Orchestrator;
        internal readonly ConcurrentQueue<(string Id, string Reason)> Queued = new();

        internal Rig(int perDestination, int total)
        {
            if (!ServiceLocator.Initialized) ServiceLocator.InitializeCore();
            Config.Load();
            Config.Update(c =>
            {
                c.Backups.Clear(); c.Destinations.Clear();
                c.Limits = new BackupLimits { PerDestination = perDestination, Total = total };
                c.Notifications.OnSuccess = new(); c.Notifications.OnFailure = new(); c.Notifications.OnSkipped = new();
            });
            var secrets = new SecretsStore();
            Orchestrator = new BackupOrchestrator(Config, secrets, Engine, new LogStore(),
                new HealthcheckService(Config, secrets), new MailService(Config, secrets), new NotificationService(Config), new BackupProgressService());
            Orchestrator.RunQueuedReasonChanged += (id, reason) => Queued.Enqueue((id, reason));
        }

        internal Destination Destination(string name)
        {
            var d = new Destination { Name = name, Kind = DestinationKind.LocalFolder, PathOrSubpath = Workspace.Sub("dest-" + name), Encrypted = false };
            Config.Update(c => c.Destinations.Add(d));
            return d;
        }

        internal Backup Backup(string name, Destination destination)
        {
            var source = Workspace.Sub("src-" + name);
            File.WriteAllText(Path.Combine(source, "file.txt"), name);
            var b = new Backup
            {
                Name = name, SourcePaths = { source }, UseVss = false, VssDisabledAcknowledged = true, AbortOnMeteredNetwork = false, Threads = 1,
                PruneSchedule = new BackupSchedule(), CheckSchedule = new BackupSchedule(),
                Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } },
            };
            Config.Update(c => c.Backups.Add(b));
            return b;
        }

        internal string[] ReasonsOf(Backup b) => Queued.Where(q => q.Id == b.Id).Select(q => q.Reason).ToArray();

        public void Dispose() => Workspace.Dispose();
    }

    [Fact]
    public async Task With_the_defaults_a_second_backup_to_the_same_destination_is_queued_another_destination_runs_and_a_third_waits_for_the_total()
    {
        using var rig = new Rig(perDestination: 1, total: 2);
        var drive = rig.Destination("Google Drive"); var nas = rig.Destination("NAS"); var usb = rig.Destination("USB");
        var localC = rig.Backup("local_c", drive); var localD = rig.Backup("local_d", drive);
        var photos = rig.Backup("photos", nas); var music = rig.Backup("music", usb);
        var release = new ConcurrentDictionary<string, TaskCompletionSource>();
        TaskCompletionSource Gate(string name) => release.GetOrAdd(name, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        rig.Engine.HoldBackup = (b, ct) => Gate(b.Name).Task.WaitAsync(ct);

        var c = rig.Orchestrator.RunBackupAsync(localC, CancellationToken.None);
        await WaitFor(() => rig.Engine.Calls.Any(x => x.Contains("local_c")));
        var d = rig.Orchestrator.RunBackupAsync(localD, CancellationToken.None);
        await WaitFor(() => rig.ReasonsOf(localD).Contains("waiting for local_c (same destination)"));
        Assert.True(rig.Orchestrator.TryGetRunningRecord(localD.Id, out var queuedRecord));
        Assert.Equal("Queued: waiting for local_c (same destination)", queuedRecord!.Summary);

        // A backup to another destination runs alongside local_c (2 in total).
        var p = rig.Orchestrator.RunBackupAsync(photos, CancellationToken.None);
        await WaitFor(() => rig.Engine.Calls.Any(x => x.Contains("photos")));
        Assert.Empty(rig.ReasonsOf(photos));
        // A third waits for the total.
        var m = rig.Orchestrator.RunBackupAsync(music, CancellationToken.None);
        await WaitFor(() => rig.ReasonsOf(music).Contains("2 backups already running"));
        Assert.DoesNotContain(rig.Engine.Calls, x => x.Contains("music") || x.Contains("local_d"));
        Assert.Equal(TimeSpan.Zero, rig.Orchestrator.QueuedTime(localC.Id)); // never queued

        Gate("photos").SetResult();
        Assert.Equal(BackupRunStatus.Success, (await p.WaitAsync(Patience)).Status);
        await WaitFor(() => rig.Engine.Calls.Any(x => x.Contains("music")));
        Assert.Equal("", rig.ReasonsOf(music).Last());
        Assert.DoesNotContain(rig.Engine.Calls, x => x.Contains("local_d"));

        Gate("music").SetResult(); Gate("local_c").SetResult();
        await WaitFor(() => rig.Engine.Calls.Any(x => x.Contains("local_d")));
        Assert.Equal("", rig.ReasonsOf(localD).Last());
        // The time it waited is kept, and stops growing once it runs: the
        // card's "Running · N min" leaves it out.
        var waited = rig.Orchestrator.QueuedTime(localD.Id);
        Assert.True(waited > TimeSpan.FromMilliseconds(200), $"Waited {waited}.");
        await Task.Delay(400);
        Assert.Equal(waited, rig.Orchestrator.QueuedTime(localD.Id));
        Gate("local_d").SetResult();
        foreach (var run in new[] { c, d, m })
            Assert.Equal(BackupRunStatus.Success, (await run.WaitAsync(Patience)).Status);
        Assert.Empty(rig.Orchestrator.SnapshotRunningBackupIds());
    }

    [Fact]
    public async Task With_two_per_destination_two_backups_back_up_together_and_clean_up_one_at_a_time()
    {
        using var rig = new Rig(perDestination: 2, total: 2);
        var drive = rig.Destination("Shared");
        var a = rig.Backup("A", drive); var b = rig.Backup("B", drive);
        var bothRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inBackup = 0;
        rig.Engine.HoldBackup = async (_, ct) =>
        {
            if (Interlocked.Increment(ref inBackup) == 2) bothRunning.TrySetResult();
            await bothRunning.Task.WaitAsync(ct);
        };
        var cleaning = 0; var peakCleaning = 0;
        rig.Engine.HoldCleanup = async (_, ct) =>
        {
            var now = Interlocked.Increment(ref cleaning);
            lock (rig) peakCleaning = Math.Max(peakCleaning, now);
            await Task.Delay(400, ct);
            Interlocked.Decrement(ref cleaning);
        };
        var results = await Task.WhenAll(rig.Orchestrator.RunBackupAsync(a, CancellationToken.None),
            rig.Orchestrator.RunBackupAsync(b, CancellationToken.None)).WaitAsync(Patience);
        Assert.All(results, r => Assert.Equal(BackupRunStatus.Success, r.Status));
        Assert.True(bothRunning.Task.IsCompleted, "Both backups were in their backup phase at the same time.");
        Assert.Equal(1, peakCleaning);
        Assert.Contains(rig.Queued, q => q.Reason.EndsWith("(same destination) before cleanup and checks", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_standalone_check_waits_for_the_backup_running_on_its_destination()
    {
        using var rig = new Rig(perDestination: 2, total: 2);
        var drive = rig.Destination("Shared");
        var a = rig.Backup("A", drive); var check = rig.Backup("B", drive);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Engine.HoldBackup = (_, ct) => release.Task.WaitAsync(ct);
        var backup = rig.Orchestrator.RunBackupAsync(a, CancellationToken.None);
        await WaitFor(() => rig.Engine.Calls.Any(x => x.StartsWith("BackupPruneCheck(A", StringComparison.Ordinal)));
        var checking = rig.Orchestrator.RunCheckAsync(check, CancellationToken.None);
        await WaitFor(() => rig.ReasonsOf(check).Contains("waiting for A (same destination) before cleanup and checks"));
        Assert.DoesNotContain(rig.Engine.Calls, x => x.StartsWith("RunCheckOnly", StringComparison.Ordinal));
        release.SetResult();
        await backup.WaitAsync(Patience);
        await checking.WaitAsync(Patience);
        Assert.Equal("", rig.ReasonsOf(check).Last());
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The expected state was not reached.");
            await Task.Delay(50);
        }
    }
}
