using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services.Platform;

namespace Backspace.Services;

/// <summary>One scheduled entry: a backup, one of its checks (U179), or its
/// cleanup.</summary>
public sealed record SchedulePlan(Backup Backup, ScheduledTaskKind Kind, BackupSchedule Schedule, BackupCheck? Check = null)
{
    internal string Key => Check is null ? Backup.Id + "/" + Kind : Backup.Id + "/" + Kind + "/" + Check.Id;
    internal string Timing => FormattableString.Invariant(
        $"{Schedule.Frequency}|{Schedule.TimeOfDay.Ticks}|{Schedule.WeeklyDaysBitmask}|{Schedule.HourlyInterval}");

    /// <summary>Checks due at the same time come in run order, cheapest first,
    /// and then run one after another (U179).</summary>
    public static IReadOnlyList<SchedulePlan> From(AppConfig config)
    {
        var plans = new List<SchedulePlan>();
        foreach (var backup in config.Backups)
        {
            plans.Add(new(backup, ScheduledTaskKind.Backup, backup.Schedule));
            foreach (var check in BackupCheck.InRunOrder(backup.Checks).Where(c => c.Schedule is not null))
                plans.Add(new(backup, ScheduledTaskKind.Check, check.Schedule!, check));
            if (backup.PruneSchedule is not null) plans.Add(new(backup, ScheduledTaskKind.Prune, backup.PruneSchedule));
        }
        return plans;
    }
}

/// <summary>When each plan is next due. Saved beside the settings, so a run
/// missed while the computer was off or Backspace was closed happens once
/// afterwards, as Task Scheduler's "start when available" did.</summary>
internal sealed class ScheduleState
{
    /// <param name="RunningSlotUtc">Set while a backup runs, until it ends.
    /// Still set when the schedule starts again means the run was cut short
    /// (a crash, a power loss, a restart, Windows shutting down or Quit), and
    /// it is resumed (U131).</param>
    /// <param name="ResumeDue">NextUtc is such a resume, due at once.</param>
    /// <param name="Resuming">The resume is running. A resume cut short is
    /// not resumed again: the next scheduled time is the next try, so a run
    /// that keeps failing in the same way doesn't start at every launch.</param>
    internal sealed record Entry(DateTime NextUtc, string Timing, DateTime? RunningSlotUtc = null,
        bool ResumeDue = false, bool Resuming = false);

    internal static string BackupKey(string backupId) => backupId + "/" + ScheduledTaskKind.Backup;

    /// <summary>Records that a backup was cut short while it ran, so that it
    /// is resumed when the schedule next starts. Also used for backups the
    /// service started outside the schedule (Backspace --run). False when it
    /// can't be: the backup has no schedule, or a resume crashed or lost power
    /// (so a backup that brings the computer down doesn't restart forever).</summary>
    /// <param name="stopped">Backspace stopped it cleanly (Windows shutting
    /// down or restarting, an update, Quit): it resumes again, even if it was
    /// already a resume.</param>
    internal static bool MarkCutShort(string path, string backupId, bool stopped = false)
    {
        var state = Load(path);
        if (state.Entries.GetValueOrDefault(BackupKey(backupId)) is not { } entry || entry.Resuming && !stopped) return false;
        state.Set(BackupKey(backupId), entry with { RunningSlotUtc = entry.RunningSlotUtc ?? entry.NextUtc, Resuming = false });
        state.Save();
        return true;
    }
    private readonly string _path;
    internal Dictionary<string, Entry> Entries { get; } = new(StringComparer.Ordinal);
    internal bool Changed { get; private set; }

    private ScheduleState(string path) => _path = path;

    internal static ScheduleState Load(string path)
    {
        var state = new ScheduleState(path);
        try
        {
            if (!File.Exists(path)) return state;
            // A file another program holds (Dropbox, a virus scanner) is
            // waited for, never taken as empty: that lost the times and the
            // backups cut short, and skipped due runs (U135, 4 November 2026).
            var bytes = FileAccessRetry.Get(() => File.ReadAllBytes(path), path);
            var saved = JsonSerializer.Deserialize<Dictionary<string, Entry>>(bytes);
            if (saved is not null) foreach (var (key, entry) in saved) state.Entries[key] = entry;
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException or UnauthorizedAccessException && !FileAccessRetry.IsLock(ex))
        {
            // Starting again from the next due times loses at most one catch-up run.
            AppLogger.For<ScheduleState>().Warning(ex, "The saved schedule times could not be read; using the next scheduled times");
            state.Changed = true;
        }
        return state;
    }

    internal void Set(string key, Entry? entry)
    {
        if (entry is null ? Entries.Remove(key) : !Equals(Entries.GetValueOrDefault(key), entry))
        {
            if (entry is not null) Entries[key] = entry;
            Changed = true;
        }
    }

    internal void Save()
    {
        if (!Changed) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var staging = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(staging, JsonSerializer.SerializeToUtf8Bytes(Entries));
            FileAccessRetry.Run(() => File.Move(staging, _path, overwrite: true), _path);
            Changed = false;
        }
        finally { try { if (File.Exists(staging)) File.Delete(staging); } catch { /* Preserve the original error. */ } }
    }
}

internal static class SchedulePlanner
{
    /// <summary>With catch-up off, a run this late counts as missed.</summary>
    internal static readonly TimeSpan MissedGrace = TimeSpan.FromMinutes(10);

    internal static DateTime? NextAfter(BackupSchedule schedule, DateTime utc)
        => schedule.ComputeNextFire(utc.ToLocalTime())?.ToUniversalTime();

    internal static void Advance(ScheduleState state, SchedulePlan plan, DateTime utc)
        => state.Set(plan.Key, NextAfter(plan.Schedule, utc) is { } next ? new(next, plan.Timing) : null);

    /// <summary>Plans due now, earliest first. New and changed schedules start
    /// from their next time; disabled, snoozed and (without catch-up) late
    /// slots are passed over rather than run later.</summary>
    internal static List<SchedulePlan> Due(IReadOnlyList<SchedulePlan> plans, ScheduleState state, DateTime nowUtc)
    {
        var due = new List<(SchedulePlan Plan, DateTime At)>();
        foreach (var plan in plans)
        {
            var entry = state.Entries.GetValueOrDefault(plan.Key);
            if (entry is null || entry.Timing != plan.Timing) { Advance(state, plan, nowUtc); continue; }
            if (entry.NextUtc > nowUtc) continue;
            if (!plan.Backup.Enabled || plan.Schedule.PausedUntilUtc > nowUtc
                || !plan.Schedule.CatchUpMissedRuns && !entry.ResumeDue && nowUtc - entry.NextUtc > MissedGrace)
                Advance(state, plan, nowUtc);
            else due.Add((plan, entry.NextUtc));
        }
        var current = plans.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in state.Entries.Keys.Where(k => !current.Contains(k)).ToList()) state.Set(key, null);
        return due.OrderBy(d => d.At).Select(d => d.Plan).ToList();
    }
}

/// <summary>Runs due schedules, in the tray app or, with administrator access,
/// in the background service. Between runs it only sleeps: it wakes at the
/// next due time, or after at most a minute so that sleep, hibernation and
/// clock changes are noticed. A due run waits while its conditions (power,
/// network, Quit) are not met, then runs once.
///
/// <para>With <see cref="Options.MaxParallel"/> above one, due plans start
/// together (U156): how many back up at once is then up to "Backups at once
/// per destination" and "in total" (<see cref="RepositoryAccess"/>), and the
/// ones waiting say "Queued". Otherwise one runs at a time.</para></summary>
internal sealed class ScheduleLoop(ScheduleLoop.Options options)
{
    internal sealed record Options(
        string StatePath,
        // Null while schedules cannot be read or are waiting for settings.
        Func<IReadOnlyList<SchedulePlan>?> Plans,
        // False when the run was not admitted, so it stays due.
        Func<SchedulePlan, CancellationToken, Task<bool>> Run,
        // A reason the plan must wait, or null.
        Func<SchedulePlan, string?> Blocked,
        Func<bool> OnBattery,
        Func<DateTime>? UtcNow = null,
        TimeSpan? MaxSleep = null,
        TimeSpan? PowerCheck = null,
        // Plans started at once; one when not given.
        Func<int>? MaxParallel = null,
        // True while the connection is metered; null where it can't be told.
        Func<bool>? Metered = null);

    private readonly SemaphoreSlim _wake = new(0, 1);
    // Guards the saved state file and the two dictionaries: parallel runs end
    // on their own threads.
    private readonly object _sync = new();
    private readonly Dictionary<string, string> _waiting = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _running = new(StringComparer.Ordinal);
    private TimeSpan MaxSleep => options.MaxSleep ?? TimeSpan.FromMinutes(1);
    private DateTime Now() => options.UtcNow?.Invoke() ?? DateTime.UtcNow;
    private static Serilog.ILogger Log => AppLogger.For<ScheduleLoop>();

    /// <summary>Re-read schedules now, after a settings change.</summary>
    internal void Wake()
    {
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    internal async Task RunAsync(CancellationToken stop)
    {
        ResumeInterruptedRuns();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var sleep = MaxSleep;
                try { sleep = await TickAsync(stop).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (Exception ex) { Log.Warning(ex, "Checking scheduled backups failed; trying again shortly"); }
                if (sleep <= TimeSpan.Zero) continue;
                try { await _wake.WaitAsync(sleep, stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
        finally
        {
            // Runs started together end before the loop does, each saving
            // how it ended (a run cut short is resumed later).
            Task[] left;
            lock (_sync) left = _running.Values.ToArray();
            try { await Task.WhenAll(left).ConfigureAwait(false); } catch { /* Each run logged its own failure. */ }
        }
    }

    /// <summary>Starts due plans and returns how long to sleep: at most one,
    /// awaited, unless <see cref="Options.MaxParallel"/> allows more.</summary>
    internal async Task<TimeSpan> TickAsync(CancellationToken stop)
    {
        if (options.Plans() is not { } plans) return MaxSleep;
        var parallel = Math.Max(1, options.MaxParallel?.Invoke() ?? 1);
        ScheduleState state;
        DateTime now;
        List<SchedulePlan> due;
        lock (_sync)
        {
            state = ScheduleState.Load(options.StatePath);
            now = Now();
            due = SchedulePlanner.Due(plans, state, now).Where(p => !_running.ContainsKey(p.Key)).ToList();
            state.Save();
            foreach (var key in _waiting.Keys.Where(k => due.All(p => p.Key != k)).ToList()) _waiting.Remove(key);
        }
        var startedAny = false;
        var leftDue = false;
        foreach (var plan in due)
        {
            if (options.Blocked(plan) is { } reason)
            {
                lock (_sync)
                {
                    if (_waiting.GetValueOrDefault(plan.Key) != reason)
                        Log.Information("{Name} ({Kind}) is due and waits: {Reason}", plan.Backup.Name, plan.Kind, reason);
                    _waiting[plan.Key] = reason;
                }
                leftDue = true;
                continue;
            }
            bool paused;
            lock (_sync) paused = _waiting.GetValueOrDefault(plan.Key) == "paused";
            if (parallel == 1)
            {
                if (await RunOneAsync(plan, now, stop, quiet: paused).ConfigureAwait(false))
                {
                    lock (_sync) _waiting.Remove(plan.Key);
                    return TimeSpan.Zero;
                }
                NotAdmitted(plan, paused);
                return MaxSleep;
            }
            lock (_sync)
            {
                if (_running.Count >= parallel) { leftDue = true; break; }
                // Not awaited: the next due plan starts too. Its end wakes
                // the loop, so a plan left waiting is looked at again.
                _running[plan.Key] = RunInBackgroundAsync(plan, now, stop, paused);
            }
            startedAny = true;
        }
        if (leftDue || startedAny && parallel == 1) return MaxSleep;
        ScheduleState saved;
        lock (_sync) saved = ScheduleState.Load(options.StatePath);
        var next = saved.Entries.Count == 0 ? (DateTime?)null : saved.Entries.Values.Min(e => e.NextUtc);
        if (next is not { } at) return MaxSleep;
        var wait = at - now;
        return wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait > MaxSleep ? MaxSleep : wait;
    }

    private void NotAdmitted(SchedulePlan plan, bool paused)
    {
        // Not admitted (Quit): it stays due. Wait like any blocked run;
        // retrying at once would spin until the pause ends.
        if (!paused) Log.Information("{Name} ({Kind}) is due and waits: automatic backups are paused (Quit)", plan.Backup.Name, plan.Kind);
        lock (_sync) _waiting[plan.Key] = "paused";
    }

    private async Task RunInBackgroundAsync(SchedulePlan plan, DateTime now, CancellationToken stop, bool quiet)
    {
        await Task.Yield();
        var ran = false;
        try { ran = await RunOneAsync(plan, now, stop, quiet).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { Log.Error(ex, "The scheduled {Kind} for {Name} could not run", plan.Kind, plan.Backup.Name); }
        finally
        {
            lock (_sync)
            {
                _running.Remove(plan.Key);
                if (ran) _waiting.Remove(plan.Key);
            }
        }
        if (!ran) NotAdmitted(plan, quiet);
        else Wake();
    }

    /// <summary>False when the run was not admitted and stays due.</summary>
    private async Task<bool> RunOneAsync(SchedulePlan plan, DateTime now, CancellationToken stop, bool quiet)
    {
        // Record the start first, with the slot being run. A crash, power loss,
        // restart or Quit during the run leaves it recorded, and the next
        // launch resumes it once (ResumeInterruptedRuns); a resumed slot cut
        // short again waits for the next scheduled time.
        ScheduleState.Entry previous;
        lock (_sync)
        {
            // Read again: a run that ended meanwhile saved its own outcome.
            var state = ScheduleState.Load(options.StatePath);
            previous = state.Entries[plan.Key];
            SchedulePlanner.Advance(state, plan, now);
            if (plan.Kind == ScheduledTaskKind.Backup && state.Entries.GetValueOrDefault(plan.Key) is { } advanced)
                state.Set(plan.Key, previous.ResumeDue ? advanced with { Resuming = true } : advanced with { RunningSlotUtc = previous.NextUtc });
            state.Save();
        }
        var ran = true;
        using var run = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var limit = plan.Schedule.ExecutionTimeLimit;
        if (limit > TimeSpan.Zero && limit < TimeSpan.FromDays(20)) run.CancelAfter(limit);
        using var watching = new CancellationTokenSource();
        var power = plan.Schedule.StopOnBattery ? WatchPowerAsync(plan, run, watching.Token) : Task.CompletedTask;
        try
        {
            if (quiet) Log.Debug("Trying the paused scheduled {Kind} for {Name} again", plan.Kind, plan.Backup.Name);
            else Log.Information("Starting scheduled {Kind} for {Name}", plan.Kind, plan.Backup.Name);
            ran = await options.Run(plan, run.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log.Error(ex, "The scheduled {Kind} for {Name} could not run", plan.Kind, plan.Backup.Name); }
        finally
        {
            watching.Cancel();
            await power.ConfigureAwait(false);
        }
        if (run.IsCancellationRequested && !stop.IsCancellationRequested && !watching.IsCancellationRequested)
            Log.Warning("Stopped the scheduled {Kind} for {Name}: it reached its time limit or the computer went on battery", plan.Kind, plan.Backup.Name);
        // Stopped (or found stopped) by its own conditions: on battery with
        // "stop if the computer switches to battery", or a metered connection
        // with "stop if the connection becomes metered". It stays due and runs
        // again once they are met (U185, owner, 11 November 2026: "stop any
        // check mid run ... and relaunch once all conditions are met"). A run
        // that had just finished when the condition began runs once more.
        var cutByCondition = ran && !stop.IsCancellationRequested
            && (plan.Schedule.StopOnBattery && options.OnBattery()
                || plan.Backup.AbortOnMeteredNetwork && options.Metered?.Invoke() == true
                // The connection went away during a check or cleanup (U185).
                || plan.Kind != ScheduledTaskKind.Backup && options.Blocked(plan) is "no network connection");
        if (cutByCondition)
            Log.Information("The scheduled {Kind} for {Name} stopped for its conditions (battery, metered connection or no connection); it runs again once they are met", plan.Kind, plan.Backup.Name);
        lock (_sync)
        {
            var after = ScheduleState.Load(options.StatePath);
            if (!ran) after.Set(plan.Key, previous);
            else if (cutByCondition) after.Set(plan.Key, previous with { ResumeDue = true, RunningSlotUtc = null, Resuming = false });
            // Backspace itself stopping (Quit, Windows shutting down or
            // restarting) cut the run short: it stays marked, and is resumed
            // when the schedule starts again, a resume included.
            else if (stop.IsCancellationRequested && after.Entries.GetValueOrDefault(plan.Key) is { } cut
                && (cut.RunningSlotUtc is not null || cut.Resuming))
                after.Set(plan.Key, cut with { RunningSlotUtc = cut.RunningSlotUtc ?? cut.NextUtc, Resuming = false });
            else
            {
                // Slots that passed during a long run are skipped, not queued.
                if (after.Entries.GetValueOrDefault(plan.Key) is { } entry && entry.NextUtc <= Now())
                    SchedulePlanner.Advance(after, plan, Now());
                // The run ended, whatever its outcome: it is no longer running.
                if (after.Entries.GetValueOrDefault(plan.Key) is { } finished && (finished.RunningSlotUtc is not null || finished.Resuming))
                    after.Set(plan.Key, finished with { RunningSlotUtc = null, Resuming = false });
            }
            after.Save();
        }
        return ran;
    }

    /// <summary>A backup cut short by a crash, a power loss, a restart,
    /// Windows shutting down or Quit is due again at once, whatever "Catch up
    /// missed runs" says: it isn't a missed run. Before, it waited until the
    /// next scheduled time, usually the next day (U131, 4 November 2026: the
    /// PC went down during the night). A resume cut short isn't resumed.</summary>
    internal void ResumeInterruptedRuns()
    {
        var state = ScheduleState.Load(options.StatePath);
        var now = Now();
        foreach (var (key, entry) in state.Entries.ToList())
        {
            if (entry.RunningSlotUtc is not null)
            {
                state.Set(key, entry with { NextUtc = now, RunningSlotUtc = null, ResumeDue = true, Resuming = false });
                Log.Information("A backup ({Key}) was cut short; it is resumed now", key);
            }
            else if (entry.Resuming)
            {
                state.Set(key, entry with { Resuming = false });
                Log.Information("A resumed backup ({Key}) was cut short again; it waits for its next scheduled time", key);
            }
        }
        state.Save();
    }

    private async Task WatchPowerAsync(SchedulePlan plan, CancellationTokenSource run, CancellationToken finished)
    {
        try
        {
            while (true)
            {
                await Task.Delay(options.PowerCheck ?? TimeSpan.FromSeconds(30), finished).ConfigureAwait(false);
                if (!options.OnBattery()) continue;
                Log.Information("Stopping the scheduled {Kind} for {Name}: the computer is on battery", plan.Kind, plan.Backup.Name);
                run.Cancel();
                return;
            }
        }
        catch (OperationCanceledException) { }
    }
}

/// <summary>Conditions shared by the tray app and the background service.</summary>
internal static class ScheduleConditions
{
    internal static string? Blocked(SchedulePlan plan, IEnumerable<Destination> destinations, Func<bool> onBattery, Func<bool> networkAvailable,
        Func<bool>? metered = null)
    {
        if (plan.Schedule.SkipOnBattery && onBattery()) return "the computer is on battery";
        if (plan.Schedule.RequireNetwork && plan.Backup.NeedsNetwork(destinations) && !networkAvailable())
            return "no network connection";
        // A run that would stop on a metered connection doesn't start on one (U185).
        if (plan.Backup.AbortOnMeteredNetwork && plan.Backup.NeedsNetwork(destinations) && metered?.Invoke() == true)
            return "the connection is metered";
        return null;
    }

    /// <summary>True while Windows reports the connection as metered.</summary>
    internal static bool Metered()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows()) return Platform.Windows.WindowsMeteredNetworkProbe.MeteredNow();
#endif
        return false;
    }

    internal static bool NetworkAvailable()
    {
        try { return System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable(); }
        catch (System.Net.NetworkInformation.NetworkInformationException) { return true; }
    }

    internal static bool OnBattery()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
            // ACLineStatus: 0 offline, 1 online, 255 unknown (desktops report online).
            return GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;
#endif
        return false;
    }

#if WINDOWS
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
#endif
}
