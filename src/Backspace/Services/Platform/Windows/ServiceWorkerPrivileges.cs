using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Backspace.Services.Platform.Windows;

internal static class ServiceWorkerPrivileges
{
    // Called after authenticating a service worker, or by an explicitly
    // elevated setup/recovery helper. Enable existing rights only; do not
    // grant any right or change group membership.
    internal static void EnableExistingBackupRights()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.AdjustPrivileges | TokenAccessLevels.Query);
        foreach (var name in new[] { "SeBackupPrivilege", "SeRestorePrivilege" })
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var privileges = new Privileges { Count = 1, Luid = luid, Attributes = 2 };
            if (!AdjustTokenPrivileges(identity.AccessToken.DangerousGetHandle(), false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var error = Marshal.GetLastWin32Error();
            if (error != 0) throw new Win32Exception(error, "This Windows operation is missing required backup or restore rights.");
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct Privileges { public uint Count; public Luid Luid; public uint Attributes; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AdjustTokenPrivileges(IntPtr token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref Privileges state, uint length, IntPtr previous, IntPtr returned);
}
