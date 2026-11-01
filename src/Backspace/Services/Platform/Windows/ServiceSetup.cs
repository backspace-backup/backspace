using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using Backspace.Models;

namespace Backspace.Services.Platform.Windows;

/// <summary>Elevated preparation only. Schedules remain disabled until the
/// ordinary owner has transferred and verified its own credentials.</summary>
internal static class ServiceSetup
{
    internal static readonly string[] ProgramFiles = { "Backspace.exe", "backrest.exe", "restic.exe", "rclone.exe" };
    internal sealed record Prepared(ServiceWorkerBinding Binding, string MigrationId, string Revision);
    internal sealed record PermissionRecord(string RelativePath, string Sddl);

    internal static Prepared Prepare(string installation, string ownerSid, Action<string>? checkpoint = null)
    {
        ServiceRegistration.RequireAdministrator();
        var root = ValidatePreparationPreflight(installation);
        var owner = new SecurityIdentifier(ownerSid);
        if (!owner.IsAccountSid()) throw new SecurityException("Choose the Windows account that owns these backups.");
        ServiceWorkerPrivileges.EnableExistingBackupRights();
        ServiceRetirement.ArchiveCompleted(root);
        var configRoot = ServicePortableLayout.Config(root);
        if (Directory.Exists(ServicePortableLayout.Service(root)) || File.Exists(Path.Combine(root, ServiceWorkerBinding.FileName)))
            throw new InvalidOperationException("This copy already has service setup files. Finish or remove its existing setup first.");

        using var rootPermissions = new ServiceObjectPermissions(root, workingFolder: true);
        using var configPermissions = new ServiceObjectPermissions(configRoot, workingFolder: true);
        using var originalOperations = ServiceCoordinationFiles.HoldOriginalOperations(root);
        var codeLocks = new Dictionary<string, ServiceObjectPermissions>();
        var prior = new List<PermissionRecord>();
        var serviceCreated = false;
        var membershipAdded = false;
        var serviceFolderCreated = false;
        ServiceObjectPermissions? servicePin = null, controlPin = null;
        var binding = new ServiceWorkerBinding(2, "Backspace-" + Guid.NewGuid().ToString("N"), ownerSid, ServicePortableLayout.Data(root));
        try
        {
            foreach (var name in ProgramFiles.Concat(File.Exists(Path.Combine(root, WindowsUpdateRuntime.GateName))
                ? new[] { WindowsUpdateRuntime.GateName } : Array.Empty<string>()))
                codeLocks.Add(name, new ServiceObjectPermissions(Path.Combine(root, name), directory: false));
            using var configLock = StateFileLock.Acquire(Path.Combine(configRoot, "config.json"));
            var bytes = File.ReadAllBytes(Path.Combine(configRoot, "config.json"));
            if (bytes.Length > ServiceRunAuthorization.MaximumApprovalBytes) throw new InvalidDataException("The backup settings are too large for service setup.");
            var json = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? bytes.AsSpan(3) : bytes.AsSpan();
            var config = JsonSerializer.Deserialize<AppConfig>(json, ServiceRunAuthorization.Json)
                ?? throw new InvalidDataException("Backup settings are missing.");
            var revision = Guid.NewGuid().ToString("N");
            var approval = ServiceRunAuthorization.CreateApproval(ownerSid, revision, config);
            foreach (var backup in config.Backups.Where(b => SchedulePlan.ServiceCanRun(b, config.Destinations)))
                _ = ServiceApprovedWorker.LocalPathsFor(new(revision, backup, config, RunKind.Backup)).ToArray();

            prior.Add(new(".", rootPermissions.Read()));
            prior.Add(new(AppPaths.AppFolderName, configPermissions.Read()));
            foreach (var pair in codeLocks) prior.Add(new(pair.Key, pair.Value.Read()));
            // The journal must survive a process crash before even the first
            // permission change. Protected children are created with their ACL
            // already applied, then held while their parents are still writable.
            CreateProtectedDirectory(ServicePortableLayout.Service(root), ProtectedDescriptor(ownerSid, directory: true, container: false));
            servicePin = new ServiceObjectPermissions(ServicePortableLayout.Service(root));
            ServiceInstallationSecurity.ValidateDescriptor(new RawSecurityDescriptor(servicePin.Read()),
                content: true, ServiceInstallationSecurity.IsTrustedInstallerIdentity);
            if (Directory.EnumerateFileSystemEntries(ServicePortableLayout.Service(root)).Any())
                throw new SecurityException("The new service folder changed before setup could hold it. Its contents were not removed.");
            // Creation and opening are separate Windows calls. Only claim
            // ownership after pinning a trusted, empty object; a replacement
            // supplied through the still-writable parent must never be swept.
            serviceFolderCreated = true;
            CreateProtectedDirectory(ServicePortableLayout.Control(root), ProtectedDescriptor(ownerSid, directory: true, container: false));
            controlPin = new ServiceObjectPermissions(ServicePortableLayout.Control(root));
            var identities = codeLocks.ToDictionary(p => p.Key, p => p.Value.Identity);
            identities["."] = rootPermissions.Identity;
            identities[AppPaths.AppFolderName] = configPermissions.Identity;
            ServiceSetupRecovery.Save(root, binding, prior, identities);
            WriteRecord(root, "permissions-before.json", JsonSerializer.SerializeToUtf8Bytes(prior, ServiceRunAuthorization.Json));
            checkpoint?.Invoke("journal-saved");
            // The program folder, Backspace.config and the program files keep
            // the permissions the owner gave them (decision, 28 October 2026).
            // Their recorded permissions and identities still let removal
            // restore copies prepared by earlier builds, which did protect them.
            checkpoint?.Invoke("permissions-protected");

            ServiceRegistration.CreateDisabled(binding.ServiceName, Path.Combine(root, "Backspace.exe"), ownerSid);
            serviceCreated = true;
            var serviceSid = binding.ServiceSid;
            ServiceRegistration.SetBackupMembership(serviceSid, enabled: true); membershipAdded = true;
            CreateProtectedDirectory(binding.DataDirectory, $"O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;{serviceSid})");
            ServiceCoordinationFiles.Prepare(root, binding, config);
            var migration = Guid.NewGuid().ToString("N");
            WriteRecord(root, "service-approval.json", approval);
            WriteRecord(root, ServiceMigrationProtocol.GrantFileName, JsonSerializer.SerializeToUtf8Bytes(
                new ServiceMigrationProtocol.Grant(1, ownerSid, migration), ServiceRunAuthorization.Json));
            WriteRecord(root, ServiceWorkerBinding.FileName, JsonSerializer.SerializeToUtf8Bytes(binding, ServiceRunAuthorization.Json));
            checkpoint?.Invoke("registration-saved");
            ServiceInstallationSecurity.ValidateCodeTree(root, binding.DataDirectory, serviceSid);
            ServiceRegistration.SetStartMode(binding, Path.Combine(root, "Backspace.exe"), automatic: false);
            using var controller = new ServiceController(binding.ServiceName);
            controller.Start(); controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            return new(binding, migration, revision);
        }
        catch (Exception original)
        {
            // No vault migration has started at this stage. Roll back only the
            // registration and directories created by this exact attempt.
            var cleanupErrors = new List<Exception>();
            if (serviceCreated)
            {
                if (membershipAdded) Try(() => ServiceRegistration.SetBackupMembership(binding.ServiceSid, false));
                Try(() => ServiceRegistration.Remove(binding, Path.Combine(root, "Backspace.exe")));
            }
            // Do not make code writable if service removal failed.
            if (cleanupErrors.Count == 0)
            {
                foreach (var record in prior)
                    Try(() =>
                    {
                        var path = Path.GetFullPath(Path.Combine(root, record.RelativePath));
                        if (path == root) rootPermissions.Set(record.Sddl);
                        else if (path == configRoot) configPermissions.Set(record.Sddl);
                        else codeLocks[record.RelativePath].Set(record.Sddl);
                    });
                if (cleanupErrors.Count == 0 && serviceFolderCreated)
                { controlPin?.Dispose(); controlPin = null; Try(() => RemoveNewTree(ServicePortableLayout.Service(root), servicePin)); }
            }
            if (cleanupErrors.Count > 0)
                throw new AggregateException("Service setup failed and needs recovery. Existing backup data was retained; protected files were not made writable while registration removal was uncertain.", new[] { original }.Concat(cleanupErrors));
            throw;
            void Try(Action action) { try { action(); } catch (Exception error) { cleanupErrors.Add(error); } }
        }
        finally { controlPin?.Dispose(); servicePin?.Dispose(); foreach (var file in codeLocks.Values) file.Dispose(); }
    }

    internal static string ValidatePreparationPreflight(string installation)
    {
        var root = ServiceInstallationSecurity.CanonicalLocalPath(installation);
        ValidateContents(root);
        var configRoot = ServicePortableLayout.Config(root);
        if (!Directory.Exists(configRoot)) throw new IOException("Save a backup before enabling background backups.");
        RejectLink(configRoot);
        // Run before ArchiveCompleted or any other elevated write. Folders above
        // the copy are the owner's choice and are never changed by setup.
        ServiceInstallationSecurity.ValidateServiceLocation(root);
        return root;
    }

    internal static void ValidateContents(string root)
    {
        RejectLink(root);
        // Other files may share the folder: setup no longer changes its permissions.
        foreach (var name in ProgramFiles)
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path)) throw new FileNotFoundException("A bundled backup engine is missing. Open the complete portable release first.", path);
            RejectLink(path);
        }
        var gate = Path.Combine(root, WindowsUpdateRuntime.GateName);
        if (File.Exists(gate) || Directory.Exists(gate))
        {
            RejectLink(gate);
            using var stream = new FileStream(gate, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != 0) throw new IOException("The application-use lock is not an empty lock file. It was not changed.");
        }
    }

    internal static void AbortPreparation(string installation)
    {
        ServiceRegistration.RequireAdministrator();
        ServiceWorkerPrivileges.EnableExistingBackupRights();
        var root = ServiceInstallationSecurity.CanonicalLocalPath(installation);
        if (File.Exists(ServicePortableLayout.Record(root, ServiceSetupRecovery.FileName)))
        { ServiceSetupRecovery.Recover(root); return; }
        var binding = ServiceWorkerBinding.Load(root);
        if (binding.Version != 2 || binding.CanRun)
            throw new InvalidOperationException("Active background backups require a verified credential return before removal.");
        if (File.Exists(Path.Combine(binding.DataDirectory, "secrets.bin"))
            || Directory.EnumerateFiles(binding.DataDirectory, "migration-*.receipt").Any())
            throw new InvalidOperationException("Credential handoff has already started. Resume verification before removing this setup; both vault copies have been preserved.");
        var path = ServicePortableLayout.Record(root, "permissions-before.json");
        using var record = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ServiceInstallationSecurity.ValidateProtectedRecord(path, root);
        if (record.Length is <= 0 or > 65536) throw new InvalidDataException("The saved folder permissions could not be verified.");
        var prior = JsonSerializer.Deserialize<List<PermissionRecord>>(record, ServiceRunAuthorization.Json)
            ?? throw new InvalidDataException("The saved folder permissions are missing.");
        ValidatePermissionRecords(prior);
        var serviceSid = binding.ServiceSid;
        using var rootPermissions = new ServiceObjectPermissions(root, workingFolder: true);
        using var configPermissions = new ServiceObjectPermissions(ServicePortableLayout.Config(root), workingFolder: true);
        var codeLocks = new Dictionary<string, ServiceObjectPermissions>();
        try
        {
            foreach (var name in PermissionFiles(prior))
                codeLocks.Add(name, new ServiceObjectPermissions(Path.Combine(root, name), directory: false));
            ServiceRegistration.Remove(binding, Path.Combine(root, "Backspace.exe"));
            ServiceRegistration.SetBackupMembership(serviceSid, enabled: false);
            record.Dispose();
            RemoveNewTree(ServicePortableLayout.Service(root));
            foreach (var saved in prior.OrderBy(p => p.RelativePath == "." ? 0 : p.RelativePath == AppPaths.AppFolderName ? 1 : 2))
            {
                if (saved.RelativePath == ".") rootPermissions.Set(saved.Sddl);
                else if (saved.RelativePath == AppPaths.AppFolderName) configPermissions.Set(saved.Sddl);
                else codeLocks[saved.RelativePath].Set(saved.Sddl);
            }
        }
        finally { foreach (var file in codeLocks.Values) file.Dispose(); }
    }

    internal static void ValidatePermissionRecords(IReadOnlyCollection<PermissionRecord> records)
    {
        var expected = new HashSet<string>(ProgramFiles, StringComparer.OrdinalIgnoreCase) { ".", AppPaths.AppFolderName };
        // Older preparations predate the desktop lifetime gate. Keep their
        // exact journal valid; a new preparation records the gate when present.
        if (records.Any(r => r.RelativePath == WindowsUpdateRuntime.GateName)) expected.Add(WindowsUpdateRuntime.GateName);
        if (records.Count != expected.Count || records.Any(r => !expected.Remove(r.RelativePath)))
            throw new SecurityException("Saved service permissions contain an unexpected or repeated path.");
        foreach (var saved in records)
        {
            var descriptor = new RawSecurityDescriptor(saved.Sddl);
            if (descriptor.Owner is null || descriptor.DiscretionaryAcl is null)
                throw new SecurityException("Saved service permissions are incomplete.");
        }
    }

    internal static IEnumerable<string> PermissionFiles(IEnumerable<PermissionRecord> records)
        => records.Where(p => p.RelativePath is not "." and not AppPaths.AppFolderName).Select(p => p.RelativePath);

    internal static void WriteRecord(string installation, string name, byte[] bytes)
    {
        var path = ServicePortableLayout.Record(installation, name);
        // Only trusted, fixed names reach this internal method.
        if (Path.GetFileName(name) != name) throw new ArgumentException("Expected a record filename.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string ProtectedDescriptor(string ownerSid, bool directory, bool container)
    {
        var inherit = directory ? "OICI" : "";
        // Read/execute plus create-file/create-subdirectory on the shared data
        // container. Existing protected service children cannot be replaced.
        var ownerRights = container ? "0x1200af" : "FRFX";
        var sddl = $"O:BAG:BAD:P(A;{inherit};FA;;;SY)(A;{inherit};FA;;;BA)(A;{inherit};FRFX;;;BU)(A;;{ownerRights};;;{ownerSid})";
        if (container) sddl += $"(A;OICIIO;FA;;;{ownerSid})";
        return sddl;
    }

    internal static void CreateProtectedDirectory(string path, string sddl)
    {
        // Apply security at creation, with fail-if-present semantics. Creating
        // first and tightening later leaves a window for an ordinary account to
        // open a writable handle that survives the later permission change.
        var descriptor = new RawSecurityDescriptor(sddl);
        var bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0);
        var memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = memory };
            if (!CreateDirectoryW(path, ref attributes)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    internal static string ReadPermissions(string path)
        => (Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access))
            .GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);

    private static void RejectLink(string path)
    { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new SecurityException("Background service setup cannot follow redirected files or folders."); }

    internal static void RemoveNewTree(string root, ServiceObjectPermissions? pinned = null)
    {
        // Keep each directory pinned through deletion, including after restored
        // parent ACLs allow the owner to create or rename unrelated children.
        var held = pinned ?? new ServiceObjectPermissions(root);
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            {
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) RemoveNewTree(entry);
                else { using var file = new ServiceObjectPermissions(entry, directory: false, allowDelete: true); file.DeleteOnClose(); }
            }
            held.DeleteOnClose();
        }
        finally { if (pinned is null) held.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes
    { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectoryW(string path, ref SecurityAttributes attributes);
}
