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

/// <summary>The tray app's part of the schedule (U043, 31 October 2026).
/// Without the background service it runs due backups in this process, so
/// they run while Backspace runs and stop when it quits. With the service, it
/// only makes sure the service is running. Started by the desktop app, never
/// by tests or command-line runs.</summary>
internal static class DesktopSchedule
{
    private static ScheduleLoop? _loop;
    private static readonly CancellationTokenSource _stop = new();
    internal static string StatePath => Path.Combine(AppPaths.ConfigRoot, "schedule-state.json");

    internal static bool ServiceOwnsSchedules
    {
        get
        {
#if WINDOWS
            if (OperatingSystem.IsWindows())
                return Platform.Windows.ServiceScheduleRegistration.OwnsSchedules(AppContext.BaseDirectory.TrimEnd('\\'));
#endif
            return false;
        }
    }

    internal static void Start()
    {
        if (_loop is not null) return;
        // Due backups start together; the limits decide who waits (U156).
        _loop = new ScheduleLoop(new(StatePath, Plans, RunAsync, Blocked, ScheduleConditions.OnBattery,
            MaxParallel: () => Models.BackupLimits.Maximum, Metered: ScheduleConditions.Metered));
        TaskSchedulerService.Changed += _loop.Wake;
        ServiceLocator.Config.Changed += (_, _) => _loop.Wake();
        _ = Task.Run(() => _loop.RunAsync(_stop.Token));
        _ = Task.Run(Housekeeping);
    }

    internal static void Stop() => _stop.Cancel();

    private static void Housekeeping()
    {
        var log = AppLogger.For(nameof(DesktopSchedule));
        try
        {
            var executable = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(executable))
            {
                var wanted = ServiceLocator.Config.Current.Preferences.StartAtSignIn;
#if DEBUG
                const bool mayAdd = false;
#else
                const bool mayAdd = true;
#endif
                SignInStart.Apply(ServiceLocator.SignInStart, wanted, executable, takeOver: false, mayAdd);
            }
        }
        catch (Exception ex) { log.Warning(ex, "Could not update the start-at-sign-in entry"); }
#if WINDOWS
        if (!OperatingSystem.IsWindows()) return;
        try { Platform.Windows.LegacyUserTasks.RemoveForCurrentUser(); }
        catch (Exception ex) { log.Warning(ex, "Could not remove earlier Windows tasks; they are retried at the next start"); }
        if (ServiceOwnsSchedules)
        {
            var root = AppContext.BaseDirectory.TrimEnd('\\');
            try { Platform.Windows.ServiceScheduleRegistration.StartIfStopped(root); }
            catch (Exception ex) { log.Warning(ex, "Could not start the background service"); }
        }
#endif
    }

    private static IReadOnlyList<SchedulePlan>? Plans()
    {
        if (!ServiceLocator.Config.IsLoaded) return null;
        // A frozen copy: settings can change while a run is starting.
        var config = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(ServiceLocator.Config.Current))!;
        var plans = SchedulePlan.From(config);
        // With the service on, the tray app keeps only what the service cannot run.
        return ServiceOwnsSchedules ? plans.Where(p => !ServiceRuns(p.Backup, config)).ToList() : plans;
    }

    /// <summary>A backup the background service runs, so the tray app does
    /// not: every complete backup, network shares included (U171).</summary>
    private static bool ServiceRuns(Backup backup, AppConfig config)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows() && Platform.Windows.SystemService.ForCopy(AppContext.BaseDirectory.TrimEnd('\\')) is not null)
            return Platform.Windows.SystemServiceRuns.RunsOnItsOwn(backup, config);
#endif
        return true;
    }

    private static string? Blocked(SchedulePlan plan)
    {
        if (ServiceLocator.Orchestrator.IsRunning(plan.Backup.Id)) return "it is already running";
        if (VaultBlocked() is { } locked) return locked;
        return ScheduleConditions.Blocked(plan, ServiceLocator.Config.Current.Destinations,
            ScheduleConditions.OnBattery, ScheduleConditions.NetworkAvailable, ScheduleConditions.Metered);
    }

    private static int _vaultAlertSent;

    /// <summary>While the saved passwords are locked on this computer (or
    /// can't be opened), scheduled runs wait instead of failing, and one alert asks
    /// to unlock them (U153). The password is never asked by a background run.</summary>
    internal static string? VaultBlocked()
    {
        VaultState state;
        try { state = ServiceLocator.Secrets.Refresh(); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { return null; }
        if (state is VaultState.Unlocked or VaultState.NoVault)
        {
            Interlocked.Exchange(ref _vaultAlertSent, 0);
            return null;
        }
        if (Interlocked.Exchange(ref _vaultAlertSent, 1) == 0)
            _ = ServiceLocator.Notifier.NotifyAsync("Scheduled backups are waiting",
                state == VaultState.Unreadable ? "Backspace can't open your saved passwords. Open Backspace to see what to do."
                : "Your saved passwords are locked on this computer. Open Backspace and enter their password once; scheduled backups then run.",
                NotificationSeverity.Warning);
        return "the saved passwords are locked on this computer";
    }

    private static async Task<bool> RunAsync(SchedulePlan plan, CancellationToken ct)
    {
        // The same admission as Quit: a saved pause keeps the run due, and
        // Quit cancels a run that has started.
        if (!new SessionTaskControl().TryBeginUnattended(out var lease)) return false;
        using (lease)
        {
            using var run = CancellationTokenSource.CreateLinkedTokenSource(ct, lease?.Token ?? CancellationToken.None);
            var backup = ServiceLocator.Config.Current.Backups.FirstOrDefault(b => b.Id == plan.Backup.Id);
            if (backup is null) return true;
            var record = plan.Kind switch
            {
                ScheduledTaskKind.Prune => await ServiceLocator.Orchestrator.RunPruneAsync(backup, run.Token).ConfigureAwait(false),
                // One check entry (U179); removed meanwhile, it does not run.
                ScheduledTaskKind.Check => backup.Checks.FirstOrDefault(c => c.Id == plan.Check?.Id) is { } check
                    ? await ServiceLocator.Orchestrator.RunCheckAsync(backup, check, run.Token).ConfigureAwait(false)
                    : null,
                _ => await ServiceLocator.Orchestrator.RunBackupAsync(backup, run.Token, automatic: true).ConfigureAwait(false),
            };
            if (record is not null)
                AppLogger.For(nameof(DesktopSchedule)).Information("Scheduled {Kind} for {Name}: {Status}", plan.Kind, backup.Name, record.Status);
            // Completed: not run again for a condition that began after it (U185).
            if (record?.Status is Models.BackupRunStatus.Success or Models.BackupRunStatus.Warning) ScheduleLoop.ReportCompleted();
        }
        return true;
    }
}
