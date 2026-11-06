using System;
using System.ServiceProcess;

namespace Backspace.Services.Platform.Windows;

/// <summary>With administrator access on, the background service keeps the
/// schedule itself (U043, 31 October 2026; U100): it starts with Windows and
/// runs due backups before anyone signs in. Nothing is registered with the
/// Windows Task Scheduler.</summary>
internal static class ServiceScheduleRegistration
{
    /// <summary>True while this copy's background service is set up.</summary>
    internal static bool OwnsSchedules(string root)
        => SystemService.ForCopy(root) is { Activated: true };

    /// <summary>The service starts with Windows. Opening Backspace ends a
    /// Quit (U100) and starts it again; the owner may start it but not change it.</summary>
    internal static void StartIfStopped(string root)
    {
        if (SystemService.ForCopy(root) is { } system)
            SystemServiceLifetime.Resume(system, TimeSpan.FromSeconds(30));
    }

    /// <summary>A saved backup runs on schedule once the service is set up;
    /// it reads the saved settings itself.</summary>
    internal static void RequireApplied(string root)
    {
        if (SystemService.ForCopy(root) is { Activated: false })
            throw new NotSupportedException("Finish setting up scheduled backups in Settings before this schedule can run.");
    }

    internal static ScheduleCheck Inspect(string root, string executable, string backupId)
    {
        if (SystemService.ForCopy(root) is not { } system)
            return new(ScheduleReadiness.Ready, "Runs on schedule while Backspace is running.");
        if (!system.Activated) return new(ScheduleReadiness.Attention, "Finish setup in Settings → Administrator access.");
        return SystemService.Status(system.ServiceName) == ServiceControllerStatus.Running
            ? new(ScheduleReadiness.Ready, "The background service runs this backup on schedule.")
            : new(ScheduleReadiness.Attention, "The background service isn't running. Open Settings → Administrator access.");
    }
}
