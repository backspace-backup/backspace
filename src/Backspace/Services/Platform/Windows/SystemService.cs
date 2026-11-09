using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// The background service (U100, 2 November 2026). One per Windows account: it
/// runs as Windows' own account (LocalSystem), from the owner's Backspace.exe,
/// with its working files in Backspace.config\service. Nothing changes any
/// file or folder permission. Its record (owner, engine folder, handoff) lives
/// in the service's own registry key, which only administrators can change.
/// The owner may start, stop and query it, so Quit stops it without a prompt.
/// </summary>
internal static class SystemService
{
    internal const string HostArgument = "--system-service";
    internal const string LocalSystemSid = "S-1-5-18";
    internal const int Version = 3;
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    /// <param name="Executable">The Backspace.exe the service runs (its
    /// registered command), which is also the installation it belongs to.</param>
    internal sealed record Registration(string ServiceName, string OwnerSid, string Executable, string EngineRoot,
        string MigrationId, bool Activated)
    {
        internal string Installation => Path.GetDirectoryName(Executable)!;
        internal string DataDirectory => DataDirectoryFor(Installation);
        internal ServiceWorkerBinding Binding => new(Version, ServiceName, OwnerSid, DataDirectory, Activated, Installation, EngineRoot);
    }

    internal static string NameFor(string ownerSid)
        => "Backspace-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ownerSid)))[..12].ToLowerInvariant();

    internal static string DataDirectoryFor(string installation)
        => Path.Combine(installation, AppPaths.AppFolderName, "service");

    internal static string Command(string executable, string serviceName)
    {
        var path = ServiceInstallationSecurity.CanonicalLocalPath(executable);
        if (path.Contains('"') || !Path.GetFileName(path).Equals("Backspace.exe", StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("The service must use this portable Backspace executable.");
        return $"\"{path}\" {HostArgument} {serviceName}";
    }

    internal static string CurrentOwnerSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new IOException("Windows could not identify this account.");
    }

    /// <summary>This account's service, wherever its Backspace.exe is.</summary>
    internal static Registration? ForCurrentUser() => Find(CurrentOwnerSid());

    internal static Registration? Find(string ownerSid) => Read(NameFor(ownerSid), ownerSid);

    /// <summary>This account's service when it runs this copy of Backspace.</summary>
    internal static Registration? ForCopy(string installation)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var found = ForCurrentUser();
        return found is not null && SamePath(found.Installation, installation) ? found : null;
    }

    internal static bool SamePath(string a, string b)
        => string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    internal static Registration? Read(string serviceName, string? expectedOwner)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(serviceName, "^Backspace-[0-9a-f]{12}$")) return null;
        using var key = Registry.LocalMachine.OpenSubKey(ServicesKey + "\\" + serviceName);
        if (key is null) return null;
        using var parameters = key.OpenSubKey("Parameters");
        if (parameters?.GetValue("Version") is not int version || version != Version) return null;
        if (parameters.GetValue("OwnerSid") is not string owner || !new SecurityIdentifier(owner).IsAccountSid()
            || (expectedOwner is not null && owner != expectedOwner) || NameFor(owner) != serviceName)
            throw new SecurityException("The background service record belongs to another Windows account.");
        if (key.GetValue("ObjectName") is string account && !account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("The background service no longer runs as Windows' own account. Turn administrator access off and on again.");
        var image = key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string
            ?? throw new InvalidDataException("The background service has no program.");
        var executable = ExecutableFrom(image, serviceName)
            ?? throw new SecurityException("The background service runs a program other than Backspace. Turn administrator access off and on again.");
        return new(serviceName, owner, executable, parameters.GetValue("EngineRoot") as string ?? "",
            parameters.GetValue("MigrationId") as string ?? "", parameters.GetValue("Activated") is int and 1);
    }

    internal static string? ExecutableFrom(string image, string serviceName)
    {
        var suffix = $"\" {HostArgument} {serviceName}";
        if (!image.StartsWith('"') || !image.EndsWith(suffix, StringComparison.Ordinal)) return null;
        var path = image[1..^suffix.Length];
        try { return Command(path, serviceName) == image ? path : null; }
        catch (SecurityException) { return null; }
    }

    /// <summary>Run by the service itself once it opened the portable vault with
    /// the key the owner's app sent (U153): from then on it counts as set up.</summary>
    internal static void MarkActivated(string serviceName)
    {
        using var parameters = Registry.LocalMachine.OpenSubKey(ServicesKey + "\\" + serviceName + "\\Parameters", writable: true)
            ?? throw new IOException("The background service record is missing.");
        parameters.SetValue("Activated", 1, RegistryValueKind.DWord);
    }

    /// <summary>Administrator approval: creates this account's service for
    /// <paramref name="installation"/>, or points the existing one at it, and
    /// starts it. It then gets this computer's vault key from the owner's app
    /// (<see cref="ServiceVaultUnlock"/>, U153).</summary>
    internal static Registration Install(string installation, string ownerSid, string engineRoot, bool moved = false)
    {
        ServiceRegistration.RequireAdministrator();
        var root = ServiceInstallationSecurity.CanonicalLocalPath(installation);
        ServiceInstallationSecurity.ValidateServiceLocation(root);
        var executable = Path.Combine(root, "Backspace.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Backspace.exe is missing.", executable);
        if ((File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
            throw new SecurityException("Background service setup cannot follow a redirected Backspace.exe.");
        var owner = new SecurityIdentifier(ownerSid);
        if (!owner.IsAccountSid()) throw new SecurityException("Choose the Windows account that owns these backups.");
        engineRoot = ServiceInstallationSecurity.CanonicalLocalPath(engineRoot);
        var name = NameFor(ownerSid);
        var existing = Read(name, ownerSid);
        using var manager = OpenSCManagerW(null, null, 0x0003); Check(manager);
        using (var service = existing is null ? Create(manager, name, Command(executable, name)) : Open(manager, name))
        {
            StopAndWait(name, TimeSpan.FromSeconds(60));
            if (!ChangeServiceConfigW(service, NoChange, 2, NoChange, Command(executable, name), null, IntPtr.Zero, null, "LocalSystem", null, null))
                throw Error();
            Configure(service, ownerSid);
        }
        using (var parameters = Registry.LocalMachine.CreateSubKey(ServicesKey + "\\" + name + "\\Parameters"))
        {
            parameters.SetValue("Version", Version, RegistryValueKind.DWord);
            parameters.SetValue("OwnerSid", ownerSid);
            parameters.SetValue("EngineRoot", engineRoot);
            // A moved copy brought its Backspace.config. Any other copy is set
            // up again: the service waits for this computer's vault key, and
            // an earlier Quit record in its folder no longer applies.
            var sameCopy = existing is not null && (moved || SamePath(existing.Installation, root)) && existing.Activated;
            if (!sameCopy)
            {
                parameters.SetValue("MigrationId", Guid.NewGuid().ToString("N"));
                parameters.SetValue("Activated", 0, RegistryValueKind.DWord);
                SystemServiceClient.ForgetQuitRecord(DataDirectoryFor(root));
            }
        }
        Directory.CreateDirectory(DataDirectoryFor(root));
        Start(name, TimeSpan.FromSeconds(60));
        return Read(name, ownerSid) ?? throw new IOException("The background service record could not be saved.");
    }

    /// <summary>Administrator approval: stops and deletes this account's
    /// service. Its registry record goes with it. Files are left to the caller.</summary>
    internal static bool Uninstall(string ownerSid)
    {
        ServiceRegistration.RequireAdministrator();
        var name = NameFor(ownerSid);
        if (Read(name, ownerSid) is null) return false;
        using var manager = OpenSCManagerW(null, null, 0x0001); Check(manager);
        using var service = Open(manager, name);
        StopAndWait(name, TimeSpan.FromSeconds(60));
        if (!DeleteService(service) && Marshal.GetLastWin32Error() != 1072) throw Error();
        return true;
    }

    internal static ServiceControllerStatus? Status(string serviceName)
    {
        try { using var controller = new ServiceController(serviceName); return controller.Status; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>The owner may start the service without a prompt.</summary>
    internal static void Start(string serviceName, TimeSpan timeout)
    {
        using var controller = new ServiceController(serviceName);
        ServiceRegistration.EnsureRunning(controller, timeout);
    }

    /// <summary>The owner may stop the service without a prompt (Quit, an
    /// update, a move). It finishes or cancels its current run first.</summary>
    internal static void StopAndWait(string serviceName, TimeSpan timeout)
    {
        // Only the stop and status rights the owner has: ServiceController.Stop
        // also lists dependent services, which the owner may not, and Quit
        // failed with "Cannot open service" (lab VM, 2 November 2026).
        using (var manager = OpenSCManagerW(null, null, 0x0001))
        {
            Check(manager);
            using var service = OpenServiceW(manager, serviceName, 0x0024);
            if (service.IsInvalid && Marshal.GetLastWin32Error() == 1060) return;
            Check(service);
            if (!ControlService(service, 1, out var status))
            {
                var error = Marshal.GetLastWin32Error();
                // Already stopped (1062) or stopping (1061): wait below.
                if (error is not (1062 or 1061)) throw new Win32Exception(error);
            }
        }
        using var controller = new ServiceController(serviceName);
        controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
    }

    private const uint AllServiceAccess = 0xF01FF, NoChange = 0xffffffff;

    private static ServiceHandle Create(ServiceHandle manager, string name, string command)
    {
        // Own process, automatic start, LocalSystem (no account name).
        var service = CreateServiceW(manager, name, "Backspace background backups", AllServiceAccess,
            0x10, 2, 1, command, null, IntPtr.Zero, null, null, null);
        // A service of this name without Backspace's record (an interrupted
        // setup) is this account's: take it over.
        if (service.IsInvalid && Marshal.GetLastWin32Error() == 1073) { service.Dispose(); return Open(manager, name); }
        Check(service);
        return service;
    }

    private static ServiceHandle Open(ServiceHandle manager, string name)
    {
        var service = OpenServiceW(manager, name, AllServiceAccess);
        Check(service);
        return service;
    }

    private static void Configure(ServiceHandle service, string ownerSid)
    {
        // The owner may query, start and stop it, but not change its program,
        // account or security. Administrators and Windows keep full control.
        var descriptor = new System.Security.AccessControl.RawSecurityDescriptor(
            $"D:P(A;;0xF01FF;;;SY)(A;;0xF01FF;;;BA)(A;;0x200B5;;;{ownerSid})");
        var bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0);
        if (!SetServiceObjectSecurity(service, 4, bytes)) throw Error();
        SetDescription(service, "Runs your Backspace backups on schedule, including before you sign in. Quit Backspace to stop it until you next sign in or restart.");
        // Start after the rest of Windows, so startup is not slowed down.
        var delayed = new DelayedAutoStart { Delayed = true };
        if (!ChangeServiceConfig2W(service, 3, ref delayed)) throw Error();
        // Restart after an unexpected exit: one minute, then again, then
        // after ten minutes. A clean stop (Quit) is not a failure.
        SetRestartOnFailure(service);
        SystemServiceTriggers.Apply(service.DangerousGetHandle());
    }

    private static void SetDescription(ServiceHandle service, string text)
    {
        var pointer = Marshal.StringToHGlobalUni(text);
        try
        {
            var info = new Description { Text = pointer };
            if (!ChangeServiceConfig2W(service, 1, ref info)) throw Error();
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static void SetRestartOnFailure(ServiceHandle service)
    {
        var actions = new[] { new ScAction(1, 60_000), new ScAction(1, 60_000), new ScAction(1, 600_000) };
        var size = Marshal.SizeOf<ScAction>();
        var memory = Marshal.AllocHGlobal(size * actions.Length);
        try
        {
            for (var i = 0; i < actions.Length; i++) Marshal.StructureToPtr(actions[i], memory + i * size, false);
            var info = new FailureActions { ResetPeriod = 86400, Count = (uint)actions.Length, Actions = memory };
            if (!ChangeServiceConfig2W(service, 2, ref info)) throw Error();
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static void Check(ServiceHandle handle) { if (handle.IsInvalid) throw Error(); }
    private static Win32Exception Error() => new(Marshal.GetLastWin32Error());

    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Description { public IntPtr Text; }
    [StructLayout(LayoutKind.Sequential)] private struct DelayedAutoStart { [MarshalAs(UnmanagedType.Bool)] public bool Delayed; }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct ScAction(uint Type, uint Delay);
    [StructLayout(LayoutKind.Sequential)] private struct FailureActions
    { public uint ResetPeriod; public IntPtr RebootMessage, Command; public uint Count; public IntPtr Actions; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenServiceW(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle CreateServiceW(ServiceHandle manager, string name, string display, uint access, uint type, uint start, uint error, string binary, string? group, IntPtr tag, string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfigW(ServiceHandle service, uint type, uint start, uint error, string? binary, string? group, IntPtr tag, string? dependencies, string? account, string? password, string? display);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfig2W(ServiceHandle service, uint level, ref Description info);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfig2W(ServiceHandle service, uint level, ref DelayedAutoStart info);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfig2W(ServiceHandle service, uint level, ref FailureActions info);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetServiceObjectSecurity(ServiceHandle service, uint information, byte[] descriptor);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteService(ServiceHandle service);
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint Type, State, Accepted, ExitCode, SpecificExitCode, CheckPoint, WaitHint; }
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ControlService(ServiceHandle service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr service);
}
