using System;
using Backspace.Models;
using Backspace.Services.Platform;

namespace Backspace.Services;

/// <summary>
/// Creates the <see cref="IScheduler"/> the viewmodels reach through
/// <c>ServiceLocator.Scheduler</c>. Backspace keeps its schedules itself
/// (U043, 31 October 2026): the tray app runs them, or the background service
/// when administrator access is on. Nothing is registered with the operating
/// system's scheduler, so saving a backup only wakes whichever runs them.
/// </summary>
public static class TaskSchedulerService
{
    public static event Action? Changed;
    internal static void NotifyChanged() => Changed?.Invoke();

    public static IScheduler Create() => new AppScheduler();
}

internal sealed class AppScheduler : IScheduler
{
    public string TaskNameFor(Backup backup) => "Backspace — " + backup.Name;

    public void UpsertTask(Backup backup, string exePath)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows() && ServiceOwns(exePath, out var root))
            // Throws ChangesNeedApprovalException until the service has the saved
            // settings; Your backups applies them right after this call.
            Platform.Windows.ServiceScheduleRegistration.RequireApplied(root);
#endif
        TaskSchedulerService.NotifyChanged();
    }

    public void DeleteTask(Backup backup) => TaskSchedulerService.NotifyChanged();

    public ScheduleCheck InspectBackup(Backup backup, string exePath)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows() && ServiceOwns(exePath, out var root))
            return Platform.Windows.ServiceScheduleRegistration.Inspect(root, exePath, backup.Id);
#endif
        return new(ScheduleReadiness.Ready, "Runs on schedule while Backspace is running.");
    }

#if WINDOWS
    private static bool ServiceOwns(string exePath, out string root)
    {
        root = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(exePath))!;
        return Platform.Windows.ServiceScheduleRegistration.OwnsSchedules(root);
    }
#endif
}
