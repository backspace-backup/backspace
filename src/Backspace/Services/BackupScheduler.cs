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

/// <summary>One scheduled entry: a backup, or its standalone check or prune.</summary>
public sealed record SchedulePlan(Backup Backup, ScheduledTaskKind Kind, BackupSchedule Schedule)
{
    internal string Key => Backup.Id + "/" + Kind;
    internal string Timing => FormattableString.Invariant(
        $"{Schedule.Frequency}|{Schedule.TimeOfDay.Ticks}|{Schedule.WeeklyDaysBitmask}|{Schedule.HourlyInterval}");

    public static IReadOnlyList<SchedulePlan> From(AppConfig config)
    {
        var plans = new List<SchedulePlan>();
        foreach (var backup in config.Backups)
        {
            plans.Add(new(backup, ScheduledTaskKind.Backup, backup.Schedule));
            if (backup.CheckSchedule is not null) plans.Add(new(backup, ScheduledTaskKind.Check, backup.CheckSchedule));
            if (backup.PruneSchedule is not null) plans.Add(new(backup, ScheduledTaskKind.Prune, backup.PruneSchedule));
        }
        return plans;
    }

    /// <summary>
    /// Whether the background service can run this backup. Its account cannot
    /// sign in to a network share, so those backups stay with the tray app,
    /// while the person is signed in (U058, 31 October 2026). Before, one such
    /// backup made turning on administrator access and every settings change
    /// fail for all backups. External drives run in the service (U061).
    /// </summary>
    public static bool ServiceCanRun(Backup backup, IReadOnlyList<Destination> destinations) =>
        backup.Targets.All(t => destinations.FirstOrDefault(d => d.Id == t.DestinationId)
            is not { Kind: DestinationKind.NetworkShare });
}

/// <summary>When each plan is next due. Saved beside the settings, so a run
/// missed while the computer was off or Backspace was closed happens once
/// afterwards, as Task Scheduler's "start when available" did.</summary>
internal sealed class ScheduleState
{
    internal sealed record Entry(DateTime NextUtc, string Timing);
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
            var saved = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllBytes(path));
            if (saved is not null) foreach (var (key, entry) in saved) state.Entries[key] = entry;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
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
            File.Move(staging, _path, overwrite: true);
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
                || !plan.Schedule.CatchUpMissedRuns && nowUtc - entry.NextUtc > MissedGrace)
                Advance(state, plan, nowUtc);
            else due.Add((plan, entry.NextUtc));
        }
        var current = plans.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in state.Entries.Keys.Where(k => !current.Contains(k)).ToList()) state.Set(key, null);
        return due.OrderBy(d => d.At).Select(d => d.Plan).ToList();
    }
}

/// <summary>Runs due schedules one at a time, in the tray app or, with
/// administrator access, in the background service. Between runs it only
/// sleeps: it wakes at the next due time, or after at most a minute so that
/// sleep, hibernation and clock changes are noticed. A due run waits while
/// its conditions (power, network, Quit) are not met, then runs once.</summary>
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
        TimeSpan? PowerCheck = null);

    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Dictionary<string, string> _waiting = new(StringComparer.Ordinal);
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

    /// <summary>Starts at most one due plan and returns how long to sleep.</summary>
    internal async Task<TimeSpan> TickAsync(CancellationToken stop)
    {
        if (options.Plans() is not { } plans) return MaxSleep;
        var state = ScheduleState.Load(options.StatePath);
        var now = Now();
        var due = SchedulePlanner.Due(plans, state, now);
        state.Save();
        foreach (var key in _waiting.Keys.Where(k => due.All(p => p.Key != k)).ToList()) _waiting.Remove(key);
        foreach (var plan in due)
        {
            if (options.Blocked(plan) is { } reason)
            {
                if (_waiting.GetValueOrDefault(plan.Key) != reason)
                    Log.Information("{Name} ({Kind}) is due and waits: {Reason}", plan.Backup.Name, plan.Kind, reason);
                _waiting[plan.Key] = reason;
                continue;
            }
            var paused = _waiting.GetValueOrDefault(plan.Key) == "paused";
            if (await RunOneAsync(plan, state, now, stop, quiet: paused).ConfigureAwait(false))
            {
                _waiting.Remove(plan.Key);
                return TimeSpan.Zero;
            }
            // Not admitted (Quit): it stays due. Wait like any blocked run;
            // retrying at once would spin until the pause ends.
            if (!paused) Log.Information("{Name} ({Kind}) is due and waits: automatic backups are paused (Quit)", plan.Backup.Name, plan.Kind);
            _waiting[plan.Key] = "paused";
            return MaxSleep;
        }
        if (due.Count > 0) return MaxSleep;
        var next = state.Entries.Count == 0 ? (DateTime?)null : state.Entries.Values.Min(e => e.NextUtc);
        if (next is not { } at) return MaxSleep;
        var wait = at - now;
        return wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait > MaxSleep ? MaxSleep : wait;
    }

    /// <summary>False when the run was not admitted and stays due.</summary>
    private async Task<bool> RunOneAsync(SchedulePlan plan, ScheduleState state, DateTime now, CancellationToken stop, bool quiet)
    {
        // Record the start first: a crash or Quit during the run must not
        // start the same slot again on the next launch.
        var previous = state.Entries[plan.Key];
        SchedulePlanner.Advance(state, plan, now);
        state.Save();
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
        var after = ScheduleState.Load(options.StatePath);
        if (!ran) after.Set(plan.Key, previous);
        // Slots that passed during a long run are skipped, not queued.
        else if (after.Entries.GetValueOrDefault(plan.Key) is { } entry && entry.NextUtc <= Now())
            SchedulePlanner.Advance(after, plan, Now());
        after.Save();
        return ran;
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
    internal static string? Blocked(SchedulePlan plan, IEnumerable<Destination> destinations, Func<bool> onBattery, Func<bool> networkAvailable)
    {
        if (plan.Schedule.SkipOnBattery && onBattery()) return "the computer is on battery";
        if (plan.Schedule.RequireNetwork && plan.Backup.NeedsNetwork(destinations) && !networkAvailable())
            return "no network connection";
        return null;
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
