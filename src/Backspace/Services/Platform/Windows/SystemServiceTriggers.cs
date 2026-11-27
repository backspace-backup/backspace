using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// "Shut down" with Fast Startup (Windows' default) hibernates Windows' own
/// session instead of ending it, so a service stopped by Quit would still be
/// stopped after the computer is switched on again: automatic start applies
/// only to a full start. A start trigger on the Windows event that each start
/// raises brings it back (U100). It may also fire at other times; the service
/// then checks whether the owner's Quit still holds and stops again
/// (<see cref="SystemServiceLifetime.StaysQuit(string, out string)"/>).
/// </summary>
internal static class SystemServiceTriggers
{
    /// <summary>Event providers whose events start the service. Measured in the
    /// lab VM (2 November 2026): Winlogon raises events within seconds of every
    /// start, a full start and a resume of Windows' session alike, and as a
    /// shutdown begins; Kernel-Boot on a resume. Neither fires while Windows
    /// simply runs with nobody signing in or out.</summary>
    internal static readonly Guid[] Providers =
    {
        new("DBE9B383-7CF3-4331-91CC-A3CB16A3B538"), // Microsoft-Windows-Winlogon
        new("15CA44FF-4D7A-4BAA-BBA5-0998955E531E"), // Microsoft-Windows-Kernel-Boot
    };

    private const uint TriggerInfoLevel = 8, CustomTrigger = 20, StartAction = 1;

    /// <summary>While the owner's Quit holds: Kernel-Boot only. It fires when
    /// Windows starts from "Shut down" with Fast Startup (a new sign-in
    /// session, so the Quit no longer holds and backups resume), not at each
    /// lock, unlock or wake as Winlogon does.</summary>
    internal static readonly Guid[] WhileQuit = { Providers[1] };

    internal static void Apply(IntPtr service) => Apply(service, Providers);

    internal static void Apply(IntPtr service, Guid[] providers)
    {
        var size = Marshal.SizeOf<Trigger>();
        var guids = Marshal.AllocHGlobal(16 * providers.Length);
        var triggers = Marshal.AllocHGlobal(size * providers.Length);
        try
        {
            for (var i = 0; i < providers.Length; i++)
            {
                Marshal.Copy(providers[i].ToByteArray(), 0, guids + i * 16, 16);
                Marshal.StructureToPtr(new Trigger { Type = CustomTrigger, Action = StartAction, Subtype = guids + i * 16 }, triggers + i * size, false);
            }
            var info = new TriggerInfo { Count = (uint)providers.Length, Triggers = triggers };
            if (!ChangeServiceConfig2W(service, TriggerInfoLevel, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(triggers); Marshal.FreeHGlobal(guids); }
    }

    /// <summary>Run by the service itself (Windows' own account may change
    /// it): only the Kernel-Boot trigger while the owner's Quit holds, so
    /// Windows does not start it for a moment at every wake or unlock in that session
    /// (U261, owner 26 November 2026: "If I'm logged in, I NEVER want to see
    /// Backspace in the task manager if I have quit"); back at its next real
    /// start. Opening Backspace starts it again (SessionTaskControl), a full
    /// restart starts it automatically. A failure leaves the triggers as
    /// they were: the service then stops again at once, as before.</summary>
    internal static void SetForOwnService(string serviceName, bool on)
    {
        var manager = OpenSCManagerW(null, null, 0x0001);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenServiceW(manager, serviceName, 0x0002); // SERVICE_CHANGE_CONFIG
            if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                Apply(service, on ? Providers : WhileQuit);
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)] private struct Trigger { public uint Type, Action; public IntPtr Subtype; public uint DataCount; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct TriggerInfo { public uint Count; public IntPtr Triggers; public IntPtr Reserved; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2W(IntPtr service, uint level, ref TriggerInfo info);
}
