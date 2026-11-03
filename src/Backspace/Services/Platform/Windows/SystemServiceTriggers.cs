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

    internal static void Apply(IntPtr service)
    {
        var size = Marshal.SizeOf<Trigger>();
        var guids = Marshal.AllocHGlobal(16 * Providers.Length);
        var triggers = Marshal.AllocHGlobal(size * Providers.Length);
        try
        {
            for (var i = 0; i < Providers.Length; i++)
            {
                Marshal.Copy(Providers[i].ToByteArray(), 0, guids + i * 16, 16);
                Marshal.StructureToPtr(new Trigger { Type = CustomTrigger, Action = StartAction, Subtype = guids + i * 16 }, triggers + i * size, false);
            }
            var info = new TriggerInfo { Count = (uint)Providers.Length, Triggers = triggers };
            if (!ChangeServiceConfig2W(service, TriggerInfoLevel, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(triggers); Marshal.FreeHGlobal(guids); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Trigger { public uint Type, Action; public IntPtr Subtype; public uint DataCount; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct TriggerInfo { public uint Count; public IntPtr Triggers; public IntPtr Reserved; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2W(IntPtr service, uint level, ref TriggerInfo info);
}
