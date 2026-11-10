using System;
using System.Security;
using System.Security.Principal;
using System.ServiceProcess;

namespace Backspace.Services.Platform.Windows;

/// <summary>Small helpers shared by the elevated setup command and the
/// owner's app for the background service (<see cref="SystemService"/>).</summary>
internal static class ServiceRegistration
{
    internal static void RequireAdministrator()
    {
        using var current = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator))
            throw new SecurityException("Windows administrator approval is required to change background backup registration.");
    }

    /// <summary>Starts the service unless it runs, then waits until it does.
    /// The owner's tray app starts it too, for alerts and saved passwords; when
    /// both start it at once Windows refuses the second start, which is not an
    /// error (a settings update failed this way in the release VM check).</summary>
    /// <param name="arguments">Passed to the service when this call starts
    /// it: <see cref="SystemServiceLifetime.ForCommand"/> runs the owner's
    /// command even after Quit, and only that (U180).</param>
    internal static void EnsureRunning(ServiceController controller, TimeSpan timeout, params string[] arguments)
    {
        // Still stopping (Quit a moment ago): wait until it has, then start it.
        controller.Refresh();
        if (controller.Status == ServiceControllerStatus.StopPending)
            controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
        StartUnlessStarted(() => { controller.Refresh(); return controller.Status; },
            () => { if (arguments.Length > 0) controller.Start(arguments); else controller.Start(); });
        controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
    }

    internal static void StartUnlessStarted(Func<ServiceControllerStatus> status, Action start)
    {
        if (status() != ServiceControllerStatus.Stopped) return;
        try { start(); }
        catch (InvalidOperationException) when (status() != ServiceControllerStatus.Stopped) { }
    }
}
