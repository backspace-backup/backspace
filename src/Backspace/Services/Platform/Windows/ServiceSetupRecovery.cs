using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Backspace.Services.Platform.Windows;

/// <summary>Durable undo for preparation, before any credential transfer.
/// Object identities prevent an old journal from changing replacement files.</summary>
internal static class ServiceSetupRecovery
{
    internal const string FileName = "setup-recovery.json";
    internal sealed record Journal(int Version, string Installation, ServiceWorkerBinding Binding,
        ServiceSetup.PermissionRecord[] Permissions, Dictionary<string, string> Identities);

    internal static void Save(string root, ServiceWorkerBinding binding,
        IEnumerable<ServiceSetup.PermissionRecord> permissions, Dictionary<string, string> identities)
    {
        var journal = new Journal(1, root, binding, permissions.ToArray(), identities);
        Validate(journal, root);
        ServiceSetup.WriteRecord(root, FileName, JsonSerializer.SerializeToUtf8Bytes(journal, ServiceRunAuthorization.Json));
    }

    internal static void Validate(Journal journal, string root)
    {
        if (journal.Version != 1 || journal.Installation != root || journal.Binding.Version != 2
            || journal.Binding.Activated || journal.Binding.ServiceName.Length != 42
            || !journal.Binding.ServiceName.StartsWith("Backspace-", StringComparison.Ordinal)
            || !Guid.TryParseExact(journal.Binding.ServiceName[10..], "N", out _)
            || !new SecurityIdentifier(journal.Binding.OwnerSid).IsAccountSid())
            throw new SecurityException("The saved setup recovery record does not belong to this portable copy.");
        ServicePortableLayout.ValidateDataLocation(journal.Binding, root);
        ServiceSetup.ValidatePermissionRecords(journal.Permissions);
        if (journal.Identities.Count != journal.Permissions.Length
            || journal.Permissions.Any(p => !journal.Identities.TryGetValue(p.RelativePath, out var id)
                || id.Length != 25 || id[8] != ':' || !id.Where(c => c != ':').All(char.IsAsciiHexDigit)))
            throw new SecurityException("The original service setup files could not be identified.");
    }

    internal static void Recover(string installation)
    {
        ServiceRegistration.RequireAdministrator();
        ServiceWorkerPrivileges.EnableExistingBackupRights();
        var root = ServiceInstallationSecurity.CanonicalLocalPath(installation);
        var pins = new Dictionary<string, ServiceObjectPermissions>(StringComparer.OrdinalIgnoreCase);
        ServiceObjectPermissions? servicePin = null, controlPin = null;
        try
        {
            pins.Add(".", new(root, workingFolder: true));
            pins.Add(AppPaths.AppFolderName, new(ServicePortableLayout.Config(root), workingFolder: true));
            servicePin = new(ServicePortableLayout.Service(root));
            controlPin = new(ServicePortableLayout.Control(root));
            // Parents may still have their original writable ACLs if setup
            // crashed early. Trust only the administrator-owned pinned journal
            // and its two protected containers, then match every object ID.
            ValidateTrusted(servicePin); ValidateTrusted(controlPin);
            var saved = ReadTrusted<Journal>(ServicePortableLayout.Record(root, FileName));
            Validate(saved, root);
            foreach (var name in ServiceSetup.PermissionFiles(saved.Permissions))
                pins.Add(name, new(Path.Combine(root, name), directory: false));
            foreach (var pair in pins)
                if (pair.Value.Identity != saved.Identities[pair.Key])
                    throw new SecurityException("A file or folder was replaced after setup began. Recovery left its permissions unchanged: " + pair.Key);

            var bindingPath = ServicePortableLayout.Record(root, ServiceWorkerBinding.FileName);
            if (File.Exists(bindingPath))
            {
                var current = ReadTrusted<ServiceWorkerBinding>(bindingPath);
                if (current != saved.Binding)
                    throw new InvalidOperationException("This setup has advanced beyond preparation. Keep its credentials and use service removal instead.");
            }
            var executable = Path.Combine(root, "Backspace.exe");
            var exists = ServiceRegistration.QuiesceForSetupRecovery(saved.Binding, executable);
            var data = saved.Binding.DataDirectory;
            try
            {
                if (Directory.Exists(data))
                {
                    using var privatePin = new ServiceObjectPermissions(data);
                    var grantPath = ServicePortableLayout.Record(root, ServiceMigrationProtocol.GrantFileName);
                    var migrationId = File.Exists(grantPath) ? ReadTrusted<ServiceMigrationProtocol.Grant>(grantPath).MigrationId : null;
                    // A listener writes one bounded progress record before
                    // receiving anything. Reservations, vaults and all other
                    // files still block this preparation-only cancellation.
                    if (HasPrivateState(data, migrationId))
                        throw new InvalidOperationException("Credential handoff or service work has started. Its files were retained; resume setup before removing the service.");
                }
            }
            catch
            {
                if (exists) ServiceRegistration.SetStartMode(saved.Binding, executable, automatic: false);
                throw;
            }
            if (exists)
            {
                ServiceRegistration.SetBackupMembership(saved.Binding.ServiceSid, enabled: false);
                ServiceRegistration.Remove(saved.Binding, executable);
            }
            // Keep the journal until ALL original ACLs have been restored. A
            // process crash during this loop can repeat the same recovery.
            foreach (var savedPermission in saved.Permissions)
                pins[savedPermission.RelativePath].Set(savedPermission.Sddl);
            controlPin.Dispose(); controlPin = null;
            ServiceSetup.RemoveNewTree(ServicePortableLayout.Service(root), servicePin);
        }
        finally
        {
            controlPin?.Dispose(); servicePin?.Dispose();
            foreach (var pin in pins.Values.Reverse()) pin.Dispose();
        }
    }

    private static void ValidateTrusted(ServiceObjectPermissions file) =>
        ServiceInstallationSecurity.ValidateDescriptor(new RawSecurityDescriptor(file.Read()),
            content: true, ServiceInstallationSecurity.IsTrustedInstallerIdentity);

    internal static bool HasPrivateState(string data, string? migrationId)
    {
        var diagnostic = Guid.TryParseExact(migrationId, "N", out _)
            ? "migration-" + migrationId + ".receipt.progress" : null;
        foreach (var path in Directory.EnumerateFileSystemEntries(data))
        {
            if (Path.GetFileName(path) != diagnostic || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return true;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is <= 0 or > 1024) return true;
            using var record = JsonDocument.Parse(file);
            if (record.RootElement.ValueKind != JsonValueKind.Object
                || record.RootElement.EnumerateObject().Count() != 2
                || !record.RootElement.TryGetProperty("stage", out var stage) || stage.ValueKind != JsonValueKind.String
                || !record.RootElement.TryGetProperty("utc", out var utc) || !utc.TryGetDateTime(out _)) return true;
        }
        return false;
    }

    private static T ReadTrusted<T>(string path)
    {
        using var held = new ServiceObjectPermissions(path, directory: false);
        ValidateTrusted(held);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 65536) throw new InvalidDataException("The setup recovery record is invalid.");
        return JsonSerializer.Deserialize<T>(stream, ServiceRunAuthorization.Json)
            ?? throw new InvalidDataException("The setup recovery record is empty.");
    }
}
