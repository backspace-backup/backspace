using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;

namespace Backspace.Services.Platform.Windows;

/// <summary>Recoverable removal of one portable installation. The service is
/// frozen before credentials return; Windows registration is removed before
/// program permissions are restored. No application data is deleted.</summary>
internal static class ServiceRetirement
{
    internal const string FileName = "service-retirement.json";
    internal sealed record Record(int Version, string Installation, string ReturnId,
        ServiceWorkerBinding Binding, string ServiceSid, string Stage, string? OwnerCiphertextSha256 = null);
    internal static string ReceiptPath(Record saved) => Path.Combine(saved.Binding.DataDirectory, "return-" + saved.ReturnId + ".accepted");

    internal static Record? Read(string installation)
    {
        var path = ServicePortableLayout.Record(installation, FileName);
        try
        {
            var saved = ReadTrusted<Record>(path);
            Validate(saved);
            if (saved.Installation != installation && saved.Stage != "complete") throw new SecurityException("The service removal record belongs to another folder.");
            return saved;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal static void RequireNotRetiring(string installation)
    {
        if (Read(installation) is not null)
            throw new IOException("Background service removal has started. Finish it in Settings before enabling or changing automatic backups.");
    }

    internal static string ArchiveName(Record saved) => "windows-service-archive-" + saved.Binding.ServiceName[10..];

    internal static Record ReadArchive(string directory)
    {
        var saved = ReadTrusted<Record>(Path.Combine(directory, "control", FileName));
        Validate(saved);
        if (saved.Stage != "complete" || Path.GetFileName(directory) != ArchiveName(saved)) throw new SecurityException("Invalid retained service history record.");
        return saved;
    }

    internal static void Validate(Record saved)
    {
        if (saved.Version != 1 || string.IsNullOrEmpty(saved.Installation) || !Path.IsPathFullyQualified(saved.Installation)
            || !Guid.TryParseExact(saved.ReturnId, "N", out _) || saved.Binding is null || saved.Binding.Version != 2 || !saved.Binding.Activated
            || saved.Binding.ServiceName is not { Length: 42 } || !saved.Binding.ServiceName.StartsWith("Backspace-", StringComparison.Ordinal)
            || !Guid.TryParseExact(saved.Binding.ServiceName[10..], "N", out _)
            || saved.ServiceSid is null || !saved.ServiceSid.StartsWith("S-1-5-80-", StringComparison.Ordinal)
            || saved.Stage is not ("frozen" or "removing" or "removed" or "complete")
            || (saved.Stage != "frozen" && !ServiceVaultReturnProtocol.Hash(saved.OwnerCiphertextSha256)))
            throw new SecurityException("The service removal record is invalid.");
        if (!new SecurityIdentifier(saved.Binding.OwnerSid).IsAccountSid()) throw new SecurityException("The service removal owner is invalid.");
        _ = new SecurityIdentifier(saved.ServiceSid);
        ServicePortableLayout.ValidateDataLocation(saved.Binding, saved.Installation);
    }

    internal static bool HasCompletedArchive(string installation, string? serviceName, string ownerSid)
    {
        if (serviceName is null || serviceName.Length != 42 || !Guid.TryParseExact(serviceName[10..], "N", out _)) return false;
        var directory = Path.Combine(ServicePortableLayout.Config(installation), "windows-service-archive-" + serviceName[10..]);
        if (!Directory.Exists(directory)) return false;
        var record = ReadArchive(directory);
        return record.Binding.ServiceName == serviceName && record.Binding.OwnerSid == ownerSid;
    }

    internal static void ArchiveCompleted(string installation)
    {
        ServiceRegistration.RequireAdministrator();
        var saved = Read(installation);
        if (saved is null) return;
        if (saved.Stage != "complete" || ServiceRegistration.Exists(saved.Binding.ServiceName))
            throw new IOException("Finish removing the previous Windows service before enabling a new one.");
        var config = ServicePortableLayout.Config(installation);
        using var parent = new ServiceObjectPermissions(config, workingFolder: true);
        using var service = new ServiceObjectPermissions(ServicePortableLayout.Service(installation));
        var target = Path.GetFullPath(Path.Combine(config, ArchiveName(saved)));
        if (Path.GetDirectoryName(target) != config || Directory.Exists(target) || File.Exists(target))
            throw new IOException("The previous service history cannot be archived without replacing existing data.");
        service.MoveTo(target); // Rename the inspected object; never follow a replaced pathname.
        if (!Directory.Exists(target)) throw new IOException("The retained service history was not moved to its expected location.");
    }

    internal static void Prepare(string installation)
    {
        ServiceRegistration.RequireAdministrator();
        var root = ServiceInstallationSecurity.CanonicalLocalPath(installation);
        using var setup = StateFileLock.Acquire(ServicePortableLayout.Record(root, "activation"));
        ServiceProgramUpdate.RequireNoPending(root);
        var saved = Read(root);
        if (saved is { Stage: "removing" or "removed" or "complete" }) return;
        var binding = ServiceWorkerBinding.Load(root);
        if (!binding.Activated || binding.Version != 2) throw new IOException("Finish credential handoff before removing the service.");
        var executable = Path.Combine(root, "Backspace.exe");
        ServiceRegistration.VerifyInstalled(binding, executable);
        ServiceInstallationSecurity.ValidateCodeTree(root, binding.DataDirectory, binding.ServiceSid);
        using var operations = ServiceCoordinationFiles.TryOpen(new(root, binding), ServiceCoordinationFiles.OperationFile, true)
            ?? throw new IOException("A backup, restore or cleanup is running. Finish it before turning off automatic backups.");
        using var migration = ServiceCoordinationFiles.TryOpen(new(root, binding), ServiceCoordinationFiles.MigrationFile, true)
            ?? throw new IOException("Credential handoff is busy. Retry removal after it finishes.");
        if (File.Exists(ServicePortableLayout.Record(root, ServiceConfigurationUpdate.FileName)))
            throw new IOException("Finish applying the pending background settings before removing the service.");
        saved ??= new(1, root, Guid.NewGuid().ToString("N"), binding, binding.ServiceSid, "frozen");
        Write(root, saved); // Host admission and credential writes now refuse work.
        ServiceScheduleRegistration.DisableOrRemove(binding, executable, remove: false);
        ServiceRegistration.QuiesceForSetupRecovery(binding, executable);
        // Restart only the frozen host, so its own identity can decrypt its
        // vault and return it over the authenticated owner pipe.
        ServiceRegistration.SetStartMode(binding, executable, automatic: false);
        using var service = new ServiceController(binding.ServiceName);
        service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
    }

    internal static void Complete(string installation, Action<string>? checkpoint = null)
    {
        ServiceRegistration.RequireAdministrator();
        // Restore the saved owner SID, including an ordinary user's SID that
        // is not in this elevated helper's token. This enables existing rights
        // only; it never grants an account new Windows privileges.
        ServiceWorkerPrivileges.EnableExistingBackupRights();
        var root = ServiceInstallationSecurity.CanonicalLocalPath(installation);
        using var setup = StateFileLock.Acquire(ServicePortableLayout.Record(root, "activation"));
        var saved = Read(root) ?? throw new IOException("Service removal has not been prepared.");
        if (saved.Stage == "complete") return;
        var binding = saved.Binding;
        var journal = ReadTrusted<ServiceSetupRecovery.Journal>(ServicePortableLayout.Record(root, ServiceSetupRecovery.FileName));
        ServiceSetupRecovery.Validate(journal, root);
        if (journal.Binding.ServiceName != binding.ServiceName || journal.Binding.OwnerSid != binding.OwnerSid)
            throw new SecurityException("The original permission record belongs to another service.");
        var pins = new Dictionary<string, ServiceObjectPermissions>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Pin and identify every object before removal or any ACL change.
            pins.Add(".", new(root, workingFolder: true)); pins.Add(AppPaths.AppFolderName, new(ServicePortableLayout.Config(root), workingFolder: true));
            foreach (var name in ServiceSetup.PermissionFiles(journal.Permissions)) pins.Add(name, new(Path.Combine(root, name), directory: false));
            foreach (var pair in pins)
                if (pair.Value.Identity != journal.Identities[pair.Key])
                    throw new SecurityException("A program file or folder was replaced after setup. Its permissions were not changed: " + pair.Key);
            using var servicePin = new ServiceObjectPermissions(ServicePortableLayout.Service(root));
            using var controlPin = new ServiceObjectPermissions(ServicePortableLayout.Control(root));
            ServiceInstallationSecurity.ValidateDescriptor(new RawSecurityDescriptor(controlPin.Read()), true, ServiceInstallationSecurity.IsTrustedInstallerIdentity);
            var executable = Path.Combine(root, "Backspace.exe");
            if (saved.Stage is "frozen" or "removing") ServiceRegistration.QuiesceForSetupRecovery(binding, executable);
            var expectedHash = saved.Stage == "frozen" ? ReadBounded<ServiceVaultReturnProtocol.Receipt>(ReceiptPath(saved)) : null;
            if (expectedHash is not null && (expectedHash.Version != 1 || expectedHash.ReturnId != saved.ReturnId))
                throw new IOException("The service has not accepted this credential return.");
            var hash = expectedHash?.OwnerCiphertextSha256 ?? saved.OwnerCiphertextSha256;
            using var ownerFile = new FileStream(Path.Combine(ServicePortableLayout.Config(root), "secrets.bin"), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!ServiceVaultReturnProtocol.Hash(hash) || Convert.ToHexString(SHA256.HashData(ownerFile)) != hash)
                throw new IOException("The verified returned vault is missing or changed. The retained service data was not removed.");
            if (saved.Stage == "frozen")
            {
                // The receipt is in service-private data. Quiescing prevents
                // restarts before inspecting it and the exact returned file.
                checkpoint?.Invoke("return-verified");
                saved = saved with { Stage = "removing", OwnerCiphertextSha256 = hash }; Write(root, saved);
            }
            if (saved.Stage == "removing")
            {
                // Persist the accepted return before deleting any Windows
                // registration. Retry must not need the deleted service to
                // decrypt or export its credentials a second time.
                ServiceScheduleRegistration.DisableOrRemove(binding, executable, remove: true);
                checkpoint?.Invoke("schedules-removed");
                ServiceRepositoryPermissions.RevokeForRetirement(root, binding, saved.ServiceSid);
                ServiceRegistration.SetBackupMembership(saved.ServiceSid, enabled: false);
                ServiceRegistration.Remove(binding, executable);
                checkpoint?.Invoke("service-deleted");
                saved = saved with { Stage = "removed", OwnerCiphertextSha256 = hash }; Write(root, saved);
                checkpoint?.Invoke("registration-removed");
            }
            // Retain history, transcripts and recovery files in place. Once
            // no service can execute here, the owner may manage that data.
            using (var data = new ServiceObjectPermissions(binding.DataDirectory))
                ReturnDataAccess(binding.DataDirectory, data, new SecurityIdentifier(binding.OwnerSid), 0);
            // Restore parents first: Windows rebuilds inherited entries from
            // the current parent when unprotecting a child. Every affected
            // object remains pinned against replacement throughout this loop.
            foreach (var permission in journal.Permissions.OrderBy(p => p.RelativePath == "." ? 0 : p.RelativePath == AppPaths.AppFolderName ? 1 : 2))
            {
                try { pins[permission.RelativePath].Set(permission.Sddl); }
                catch (Exception ex) { throw new IOException("Could not restore the original permissions of " + permission.RelativePath + ". Retained data was preserved.", ex); }
            }
            Write(root, saved with { Stage = "complete" });
            checkpoint?.Invoke("permissions-restored");
        }
        finally { foreach (var pin in pins.Values.Reverse()) pin.Dispose(); }
    }

    private static void ReturnDataAccess(string path, ServiceObjectPermissions held, SecurityIdentifier owner, int depth)
    {
        if (depth > 256) throw new IOException("The retained service data is too deeply nested to review safely.");
        var security = new DirectorySecurity(); security.SetSecurityDescriptorSddlForm(held.Read());
        security.AddAccessRule(new(owner, FileSystemRights.Modify | FileSystemRights.Synchronize, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        held.SetDacl(security.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access));
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            try
            {
                var directory = (File.GetAttributes(entry) & FileAttributes.Directory) != 0;
                using var child = new ServiceObjectPermissions(entry, directory);
                if (directory) ReturnDataAccess(entry, child, owner, depth + 1);
                else
                {
                    var file = new FileSecurity(); file.SetSecurityDescriptorSddlForm(child.Read());
                    file.AddAccessRule(new(owner, FileSystemRights.Modify | FileSystemRights.Synchronize, AccessControlType.Allow));
                    child.SetDacl(file.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access));
                }
            }
            catch (Exception ex) { throw new IOException("Could not return access to retained service data: " + entry, ex); }
        }
    }

    private static void Write(string root, Record record) => ServiceSetup.WriteRecord(root, FileName, JsonSerializer.SerializeToUtf8Bytes(record, ServiceRunAuthorization.Json));
    private static T ReadBounded<T>(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 65536) throw new InvalidDataException("Invalid service removal record size.");
        return JsonSerializer.Deserialize<T>(file, ServiceRunAuthorization.Json) ?? throw new InvalidDataException();
    }
    private static T ReadTrusted<T>(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // After successful removal the program's original parent permissions
        // are restored. Trust this pinned administrator-owned record itself,
        // not a claim that the whole portable folder is still protected.
        ServiceInstallationSecurity.ValidateDescriptor(new RawSecurityDescriptor(ServiceObjectPermissions.Read(file.SafeFileHandle)), true, ServiceInstallationSecurity.IsTrustedInstallerIdentity);
        if (file.Length is <= 0 or > 65536) throw new InvalidDataException();
        return JsonSerializer.Deserialize<T>(file, ServiceRunAuthorization.Json) ?? throw new InvalidDataException();
    }
}
