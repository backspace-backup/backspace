using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32.SafeHandles;

namespace Backspace.Services.Platform.Windows;

/// <summary>Small SCM boundary used only by an explicitly elevated setup
/// command. It registers the existing portable executable, never another copy.</summary>
internal static class ServiceRegistration
{
    private const uint AllServiceAccess = 0xF01FF, NoChange = 0xffffffff;

    internal static void RequireAdministrator()
    {
        using var current = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator))
            throw new SecurityException("Windows administrator approval is required to change background backup registration.");
    }

    internal static void CreateDisabled(string name, string executable, string ownerSid)
    {
        RequireAdministrator();
        using var manager = OpenSCManagerW(null, null, 0x0003);
        Check(manager);
        using var service = CreateServiceW(manager, name, "Backspace background backups", AllServiceAccess,
            0x10, 4, 1, Command(executable), null, IntPtr.Zero, null, "NT SERVICE\\" + name, null);
        Check(service);
        try
        {
            // The owner may start, stop and inspect their own service, but may
            // not replace its executable, identity or security descriptor.
            var descriptor = new RawSecurityDescriptor($"D:P(A;;0xF01FF;;;SY)(A;;0xF01FF;;;BA)(A;;0x20035;;;{ownerSid})");
            var bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0);
            if (!SetServiceObjectSecurity(service, 4, bytes)) throw Error();
        }
        catch { DeleteService(service); throw; }
    }

    internal static string Command(string executable)
    {
        var path = ServiceInstallationSecurity.CanonicalLocalPath(executable);
        if (path.Contains('"') || !Path.GetFileName(path).Equals("Backspace.exe", StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("The service must use this portable Backspace executable.");
        return "\"" + path + "\" --service-host";
    }

    internal static void SetStartMode(ServiceWorkerBinding binding, string executable, bool automatic)
    {
        RequireAdministrator();
        using var manager = OpenSCManagerW(null, null, 1); Check(manager);
        using var service = OpenServiceW(manager, binding.ServiceName, AllServiceAccess); Check(service);
        Verify(service, binding, executable);
        if (!ChangeServiceConfigW(service, NoChange, automatic ? 2u : 3u, NoChange,
                null, null, IntPtr.Zero, null, null, null, null)) throw Error();
        if (!automatic) return;
        // The service keeps the schedule (U043): it runs from now on, not
        // only from the next restart. A settings update had stopped it.
        using var controller = new ServiceController(binding.ServiceName);
        EnsureRunning(controller, TimeSpan.FromSeconds(30));
    }

    /// <summary>Starts the service unless it runs, then waits until it does.
    /// The owner's tray app starts it too, for alerts and saved passwords; when
    /// both start it at once Windows refuses the second start, which is not an
    /// error (a settings update failed this way in the release VM check).</summary>
    internal static void EnsureRunning(ServiceController controller, TimeSpan timeout)
    {
        StartUnlessStarted(() => { controller.Refresh(); return controller.Status; }, controller.Start);
        controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
    }

    internal static void StartUnlessStarted(Func<ServiceControllerStatus> status, Action start)
    {
        if (status() != ServiceControllerStatus.Stopped) return;
        try { start(); }
        catch (InvalidOperationException) when (status() != ServiceControllerStatus.Stopped) { }
    }

    internal static void VerifyInstalled(ServiceWorkerBinding binding, string executable)
    {
        using var manager = OpenSCManagerW(null, null, 1); Check(manager);
        using var service = OpenServiceW(manager, binding.ServiceName, 1); Check(service);
        Verify(service, binding, executable);
    }

    internal static bool Exists(string serviceName)
    {
        using var manager = OpenSCManagerW(null, null, 1); Check(manager);
        using var service = OpenServiceW(manager, serviceName, 1);
        if (service.IsInvalid && Marshal.GetLastWin32Error() == 1060) return false;
        Check(service); return true;
    }

    internal static bool QuiesceForSetupRecovery(ServiceWorkerBinding binding, string executable)
    {
        RequireAdministrator();
        using var manager = OpenSCManagerW(null, null, 1); Check(manager);
        using var service = OpenServiceW(manager, binding.ServiceName, AllServiceAccess);
        if (service.IsInvalid && Marshal.GetLastWin32Error() == 1060) return false;
        Check(service); Verify(service, binding, executable);
        // Prevent an owner client from restarting a prepared service between
        // the vault inspection and removal. This does not authorize deleting a
        // vault: the caller must retain everything if migration has begun.
        if (!ChangeServiceConfigW(service, NoChange, 4, NoChange,
            null, null, IntPtr.Zero, null, null, null, null)) throw Error();
        using var controller = new ServiceController(binding.ServiceName);
        if (controller.Status != ServiceControllerStatus.Stopped)
        { controller.Stop(); controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); }
        return true;
    }

    private static void Verify(ServiceHandle service, ServiceWorkerBinding binding, string executable)
    {
        QueryServiceConfigW(service, IntPtr.Zero, 0, out var needed);
        if (needed is 0 or > 65536) throw Error();
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!QueryServiceConfigW(service, buffer, needed, out _)) throw Error();
            var config = Marshal.PtrToStructure<QueryConfig>(buffer);
            if (config.Type != 0x10
                || !string.Equals(Marshal.PtrToStringUni(config.Binary), Command(executable), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Marshal.PtrToStringUni(config.Account), "NT SERVICE\\" + binding.ServiceName, StringComparison.OrdinalIgnoreCase))
                throw new SecurityException("The Windows service registration no longer matches this portable copy. Nothing was changed.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static void Remove(ServiceWorkerBinding binding, string executable)
    {
        RequireAdministrator();
        using var manager = OpenSCManagerW(null, null, 1); Check(manager);
        using var service = OpenServiceW(manager, binding.ServiceName, AllServiceAccess);
        if (service.IsInvalid && Marshal.GetLastWin32Error() == 1060) return;
        Check(service); Verify(service, binding, executable);
        using (var controller = new ServiceController(binding.ServiceName))
        {
            if (controller.Status != ServiceControllerStatus.Stopped)
            { controller.Stop(); controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); }
        }
        if (!DeleteService(service)) throw Error();
    }

    internal static void SetBackupMembership(string serviceSid, bool enabled)
    {
        RequireAdministrator();
        var group = ((NTAccount)new SecurityIdentifier(WellKnownSidType.BuiltinBackupOperatorsSid, null)
            .Translate(typeof(NTAccount))).Value.Split('\\')[^1];
        var sid = new SecurityIdentifier(serviceSid);
        if (!serviceSid.StartsWith("S-1-5-80-", StringComparison.Ordinal)) throw new SecurityException("Expected a virtual service identity.");
        var bytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(bytes, 0);
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var member = new GroupMember { Sid = pointer };
            var error = enabled ? NetLocalGroupAddMembers(null, group, 0, ref member, 1)
                : NetLocalGroupDelMembers(null, group, 0, ref member, 1);
            if (error != 0 && !(enabled && error == 1378) && !(!enabled && error == 1377))
                throw new Win32Exception((int)error);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static void Check(ServiceHandle handle) { if (handle.IsInvalid) throw Error(); }
    private static Win32Exception Error() => new(Marshal.GetLastWin32Error());
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct GroupMember { public IntPtr Sid; }
    [StructLayout(LayoutKind.Sequential)] private struct QueryConfig
    { public uint Type, Start, Error; public IntPtr Binary, Group; public uint Tag; public IntPtr Dependencies, Account, DisplayName; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenServiceW(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle CreateServiceW(ServiceHandle manager, string name, string display, uint access, uint type, uint start, uint error, string binary, string? group, IntPtr tag, string? dependencies, string account, string? password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfigW(ServiceHandle service, uint type, uint start, uint error, string? binary, string? group, IntPtr tag, string? dependencies, string? account, string? password, string? display);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceConfigW(ServiceHandle service, IntPtr config, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetServiceObjectSecurity(ServiceHandle service, uint information, byte[] descriptor);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteService(ServiceHandle service);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr service);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] private static extern uint NetLocalGroupAddMembers(string? server, string group, uint level, ref GroupMember member, uint count);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] private static extern uint NetLocalGroupDelMembers(string? server, string group, uint level, ref GroupMember member, uint count);
}
