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
        _loop = new ScheduleLoop(new(StatePath, Plans, RunAsync, Blocked, ScheduleConditions.OnBattery));
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
            // An earlier version's SYSTEM tasks go with the next settings update,
            // which the owner may start without a prompt. Do it now, even when
            // Backspace starts in the tray and no window opens.
            try
            {
                var binding = Platform.Windows.ServiceWorkerBinding.LoadForOwner(root);
                if (binding.Activated && Platform.Windows.ServiceScheduleRegistration.HasLegacyTasks(binding))
                {
                    var outcome = new Platform.Windows.WindowsBackgroundBackupSetup().ApplyChangesQuietlyAsync().GetAwaiter().GetResult();
                    log.Information("Moved earlier Windows tasks into the background service: {Result}", outcome.Result);
                }
            }
            catch (Exception ex) { log.Warning(ex, "Could not move earlier Windows tasks into the background service yet"); }
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
        return ServiceOwnsSchedules ? plans.Where(p => !SchedulePlan.ServiceCanRun(p.Backup, config.Destinations)).ToList() : plans;
    }

    private static string? Blocked(SchedulePlan plan)
    {
        if (ServiceLocator.Orchestrator.IsRunning(plan.Backup.Id)) return "it is already running";
        return ScheduleConditions.Blocked(plan, ServiceLocator.Config.Current.Destinations,
            ScheduleConditions.OnBattery, ScheduleConditions.NetworkAvailable);
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
                ScheduledTaskKind.Check => await ServiceLocator.Orchestrator.RunCheckAsync(backup, run.Token).ConfigureAwait(false),
                _ => await ServiceLocator.Orchestrator.RunBackupAsync(backup, run.Token).ConfigureAwait(false),
            };
            AppLogger.For(nameof(DesktopSchedule)).Information("Scheduled {Kind} for {Name}: {Status}", plan.Kind, backup.Name, record.Status);
        }
        return true;
    }
}
