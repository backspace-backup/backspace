using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Backspace.Services.Platform.Windows;

/// <summary>Shared Windows file locks for the desktop and its virtual service
/// account. They stay in the portable folder and work across logon sessions.</summary>
internal static class ServiceCoordinationFiles
{
    internal const string OperationFile = "operations.lock";
    internal const string MigrationFile = "migration.lock";
    internal sealed record Context(string Installation, ServiceWorkerBinding Binding)
    {
        internal string Directory => Path.Combine(ServicePortableLayout.Control(Installation), "coordination");
    }

    internal static Context? Current()
        => Resolve(AppContext.BaseDirectory.TrimEnd('\\'), AppPaths.ConfigRoot);

    internal static Context? Resolve(string installation, string configurationRoot)
    {
        var root = Path.GetFullPath(installation).TrimEnd('\\');
        var config = Path.GetFullPath(configurationRoot).TrimEnd('\\');
        var ownerContext = string.Equals(config, ServicePortableLayout.Config(root), StringComparison.OrdinalIgnoreCase);
        var workerContext = string.Equals(config, ServicePortableLayout.Data(root), StringComparison.OrdinalIgnoreCase);
        if (!ownerContext && !workerContext) return null;
        ServiceProgramUpdate.RequireNoPending(root);
        if (ServiceRetirement.Read(root) is { } retirement)
        {
            if (retirement.Stage == "complete" && ownerContext) return null;
            throw new IOException("Finish background service removal in Settings before starting another operation.");
        }
        var path = ServicePortableLayout.Record(root, ServiceWorkerBinding.FileName);
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        var binding = ownerContext ? ServiceWorkerBinding.LoadForOwner(root) : ServiceWorkerBinding.Load(root);
        if (workerContext) binding.ValidateWorkerIdentity();
        if (!binding.CanRun)
            throw new InvalidOperationException("Finish or cancel background backup setup before starting a backup, restore or cleanup.");
        return new(root, binding);
    }

    internal static string Name(string logicalName)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(logicalName))) + ".lock";

    internal static void Prepare(string installation, ServiceWorkerBinding binding, Backspace.Models.AppConfig configuration)
    {
        ServiceRegistration.RequireAdministrator();
        var context = new Context(installation, binding);
        if (!Directory.Exists(context.Directory))
            ServiceSetup.CreateProtectedDirectory(context.Directory,
                $"O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;;FRFX;;;{binding.OwnerSid})(A;;FRFX;;;{binding.ServiceSid})");
        ServiceInstallationSecurity.ValidateAncestors(context.Directory, context.Installation);
        var names = configuration.Backups.Select(b => Name(BackupOrchestrator.MutexNameFor(b.Id)))
            .Concat(configuration.Backups.SelectMany(b => b.Targets)
                .Select(t => Name(BackupOrchestrator.MutexNameForDestination(t.DestinationId, t.StorageName))))
            .Append(OperationFile).Append(MigrationFile).Distinct(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var path = Path.Combine(context.Directory, name);
            if (!File.Exists(path))
            {
                var security = new FileSecurity();
                security.SetSecurityDescriptorSddlForm($"O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFW;;;{binding.OwnerSid})(A;;FRFW;;;{binding.ServiceSid})");
                using var created = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Read | FileSystemRights.Write,
                    FileShare.None, 4096, FileOptions.None, security);
                created.Flush(true);
            }
            // A busy existing gate is valid during a settings refresh. Its
            // metadata can be inspected without joining its data-sharing lock.
            using var metadata = OpenMetadata(path);
            ValidateGate(metadata, binding.OwnerSid, binding.ServiceSid);
        }
    }

    internal static FileStream? TryOpen(Context context, string name, bool exclusive)
    {
        if (Path.GetFileName(name) != name) throw new SecurityException("Expected a coordination filename.");
        ServiceInstallationSecurity.ValidateAncestors(context.Directory, context.Installation);
        var path = Path.Combine(context.Directory, name);
        var handle = CreateFileW(path, exclusive ? 0xc0000000u : 0x80000000u,
            exclusive ? 0u : 1u, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            if (error is 32 or 33) return null;
            throw new IOException("The desktop and background service could not share their operation lock. Finish updating background backup setup before running this operation.", new Win32Exception(error));
        }
        try
        {
            ValidateGate(handle, context.Binding.OwnerSid, context.Binding.ServiceSid);
            return new FileStream(handle, exclusive ? FileAccess.ReadWrite : FileAccess.Read);
        }
        catch { handle.Dispose(); throw; }
    }

    internal static FileStream HoldOriginalOperations(string installation)
    {
        var path = Path.Combine(ServicePortableLayout.Config(installation), OperationFile);
        var handle = CreateFileW(path, 0xc0000000, 0, IntPtr.Zero, 4, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            if (error is 32 or 33) throw new IOException("A backup, restore or cleanup is still running. Finish it before changing background backup setup.");
            throw new Win32Exception(error);
        }
        try { ValidateObject(handle); return new FileStream(handle, FileAccess.ReadWrite); }
        catch { handle.Dispose(); throw; }
    }

    private static SafeFileHandle OpenMetadata(string path)
    {
        var handle = CreateFileW(path, 0x20080, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error);
    }

    internal static void ValidateGate(SafeFileHandle handle, string ownerSid, string serviceSid)
    {
        ValidateObject(handle);
        ValidateDescriptor(new RawSecurityDescriptor(ServiceObjectPermissions.Read(handle)), ownerSid, serviceSid);
    }

    internal static void ValidateDescriptor(RawSecurityDescriptor descriptor, string ownerSid, string serviceSid)
    {
        // The owner/service can read/write the empty lock object, but cannot
        // replace it, change its ACL or give another account access.
        ServiceInstallationSecurity.ValidateDescriptor(descriptor, content: false, ServiceInstallationSecurity.IsTrustedInstallerIdentity);
        ServiceInstallationSecurity.ValidateDescriptor(descriptor, content: true, sid =>
            ServiceInstallationSecurity.IsTrustedInstallerIdentity(sid) || sid.Value == ownerSid || sid.Value == serviceSid);
    }

    private static void ValidateObject(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var file)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((file.Attributes & 0x410) != 0 || file.Links != 1)
            throw new SecurityException("The operation lock is redirected, hard-linked or no longer a regular file. It was not used.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
