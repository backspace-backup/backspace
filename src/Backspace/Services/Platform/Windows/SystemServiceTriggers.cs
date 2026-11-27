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

    /// <summary>One start trigger: an event provider, and optionally the
    /// keywords an event must carry (all of <see cref="KeywordAll"/>, any of
    /// <see cref="KeywordAny"/>).</summary>
    internal readonly record struct Spec(Guid Provider, ulong KeywordAny = 0, ulong KeywordAll = 0);

    /// <summary>Winlogon's sign-in and sign-out events only (7001 "User Logon
    /// Notification", 7002 "User Logoff Notification"): the only ones of
    /// its events written to the System log (channel keyword
    /// 0x2000000000000000) with its Telemetry keyword (0x200000000000), as its
    /// manifest declares them (wevtutil gp Microsoft-Windows-Winlogon /ge
    /// /gm, 27 November 2026). Locking, unlocking, sleep and wake raise other
    /// Winlogon events, which do not carry both.</summary>
    internal static readonly Spec SignInOrOut = new(Providers[0], KeywordAny: 0x2000000000000000, KeywordAll: 0x2000200000000000);

    /// <summary>While the owner's Quit holds (U261, owner 26 November 2026: "If
    /// I'm logged in, I NEVER want to see Backspace in the task manager if I
    /// have quit"): sign-in and sign-out, and Kernel-Boot for a start of
    /// Windows. Signing out, signing in again or restarting ends a Quit
    /// (B023, owner 13 October 2026: "if nobody has logged in yet, or the
    /// user has logged out, schedule tasks would be allowed to run"); locking,
    /// unlocking, sleep and wake do not, so they no longer start it.</summary>
    internal static readonly Spec[] WhileQuit = { SignInOrOut, new(Providers[1]) };

    private const uint TriggerInfoLevel = 8, CustomTrigger = 20, StartAction = 1, KeywordAnyType = 4, KeywordAllType = 5;

    internal static void Apply(IntPtr service) => Apply(service, Array.ConvertAll(Providers, p => new Spec(p)));

    internal static void Apply(IntPtr service, Spec[] specs)
    {
        var size = Marshal.SizeOf<Trigger>();
        var itemSize = Marshal.SizeOf<DataItem>();
        var guids = Marshal.AllocHGlobal(16 * specs.Length);
        var triggers = Marshal.AllocHGlobal(size * specs.Length);
        // Up to two keyword items per trigger, each pointing at its 8 bytes.
        var items = Marshal.AllocHGlobal(itemSize * 2 * specs.Length);
        var keywords = Marshal.AllocHGlobal(8 * 2 * specs.Length);
        try
        {
            for (var i = 0; i < specs.Length; i++)
            {
                Marshal.Copy(specs[i].Provider.ToByteArray(), 0, guids + i * 16, 16);
                var count = 0u;
                foreach (var (type, value) in new[] { (KeywordAnyType, specs[i].KeywordAny), (KeywordAllType, specs[i].KeywordAll) })
                {
                    if (value == 0) continue;
                    var slot = i * 2 + (int)count;
                    Marshal.WriteInt64(keywords + slot * 8, unchecked((long)value));
                    Marshal.StructureToPtr(new DataItem { Type = type, Size = 8, Data = keywords + slot * 8 }, items + slot * itemSize, false);
                    count++;
                }
                Marshal.StructureToPtr(new Trigger { Type = CustomTrigger, Action = StartAction, Subtype = guids + i * 16,
                    DataCount = count, Data = count == 0 ? IntPtr.Zero : items + i * 2 * itemSize }, triggers + i * size, false);
            }
            var info = new TriggerInfo { Count = (uint)specs.Length, Triggers = triggers };
            if (!ChangeServiceConfig2W(service, TriggerInfoLevel, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(keywords); Marshal.FreeHGlobal(items); Marshal.FreeHGlobal(triggers); Marshal.FreeHGlobal(guids); }
    }

    /// <summary>Run by the service itself (Windows' own account may change
    /// it): <see cref="WhileQuit"/> while the owner's Quit holds, so Windows
    /// does not start it for a moment at every wake or unlock in that session;
    /// all of them again at its next real start. Opening Backspace starts it
    /// again (SessionTaskControl), a full restart starts it automatically. A
    /// failure leaves the triggers as they were: the service then stops again
    /// at once, as before.</summary>
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
                Apply(service, on ? Array.ConvertAll(Providers, p => new Spec(p)) : WhileQuit);
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    /// <summary>The service's start triggers as Windows keeps them (the
    /// installed-service test reads them back).</summary>
    internal static Spec[] Read(string serviceName)
    {
        var manager = OpenSCManagerW(null, null, 0x0001);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenServiceW(manager, serviceName, 0x0001); // SERVICE_QUERY_CONFIG
            if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                QueryServiceConfig2W(service, TriggerInfoLevel, IntPtr.Zero, 0, out var needed);
                var buffer = Marshal.AllocHGlobal((int)Math.Max(needed, 64));
                try
                {
                    if (!QueryServiceConfig2W(service, TriggerInfoLevel, buffer, Math.Max(needed, 64), out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var info = Marshal.PtrToStructure<TriggerInfo>(buffer);
                    var specs = new Spec[info.Count];
                    for (var i = 0; i < specs.Length; i++)
                    {
                        var trigger = Marshal.PtrToStructure<Trigger>(info.Triggers + i * Marshal.SizeOf<Trigger>());
                        var bytes = new byte[16];
                        Marshal.Copy(trigger.Subtype, bytes, 0, 16);
                        ulong any = 0, all = 0;
                        for (var j = 0; j < trigger.DataCount; j++)
                        {
                            var item = Marshal.PtrToStructure<DataItem>(trigger.Data + j * Marshal.SizeOf<DataItem>());
                            if (item.Size != 8) continue;
                            var value = unchecked((ulong)Marshal.ReadInt64(item.Data));
                            if (item.Type == KeywordAnyType) any = value; else if (item.Type == KeywordAllType) all = value;
                        }
                        specs[i] = new Spec(new Guid(bytes), any, all);
                    }
                    return specs;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig2W(IntPtr service, uint level, IntPtr buffer, uint size, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)] private struct Trigger { public uint Type, Action; public IntPtr Subtype; public uint DataCount; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct DataItem { public uint Type, Size; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct TriggerInfo { public uint Count; public IntPtr Triggers; public IntPtr Reserved; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2W(IntPtr service, uint level, ref TriggerInfo info);
}
