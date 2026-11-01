using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Backspace.Models;

namespace Backspace.Services.Platform.Windows;

/// <summary>Give this installation's service access to approved local backup
/// repositories. Never propagate ACL changes through uninspected descendants,
/// follow links, change ownership or grant access to an entire source tree.</summary>
internal static class ServiceRepositoryPermissions
{
    internal const string FileName = "repository-access.json";
    internal sealed record Root(string Path, string Identity);
    internal sealed record Record(int Version, string ServiceName, Root[] Roots);

    /// <summary>Grants the external drive folders that are connected now and
    /// not yet granted. Run by the settings task, which the service starts when
    /// a drive is plugged in (U061).</summary>
    internal static void GrantConnectedExternal(string installation, ServiceWorkerBinding binding, ServiceRunAuthorization.Approval approval)
    {
        var external = ServiceExternalDrives.ConnectedRoots(approval.Configuration).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (external.Count == 0) return;
        var recorded = ReadRecord(installation, binding).Roots.Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Granting walks the whole folder, so skip folders that already carry
        // this service's rule.
        if (external.All(path => recorded.Contains(path) && HasServiceRule(path, binding.ServiceSid))) return;
        Grant(installation, binding, approval, onlyExternal: true);
    }

    private static bool HasServiceRule(string path, string serviceSid)
    {
        try
        {
            if (!Directory.Exists(path)) return false;
            var sid = new SecurityIdentifier(serviceSid);
            return new DirectoryInfo(path).GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>().Any(r => r.AccessControlType == AccessControlType.Allow && sid.Equals(r.IdentityReference)
                    && (r.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return false; }
    }

    internal static void Grant(string installation, ServiceWorkerBinding binding, ServiceRunAuthorization.Approval approval, bool onlyExternal = false)
    {
        ServiceRegistration.RequireAdministrator();
        var external = ServiceExternalDrives.ConnectedRoots(approval.Configuration).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roots = RepositoryRoots(installation, approval.Configuration).Where(r => !onlyExternal || external.Contains(r)).ToArray();
        var previous = ReadRecord(installation, binding);
        var retained = previous.Roots.ToDictionary(r => r.Path, StringComparer.OrdinalIgnoreCase);
        var sid = new SecurityIdentifier(binding.ServiceSid);
        foreach (var path in roots)
        {
            // A missing folder (deleted, or on a drive that is not connected) has
            // nothing to grant yet. Failing here would leave the service stopped
            // with every backup waiting (VM, 31 October 2026); its own runs report
            // the missing destination instead.
            if (!Directory.Exists(path))
            {
                // On a connected external drive, a new backup folder is made
                // here: the service's account may not create it itself.
                if (external.Contains(path) && Directory.Exists(System.IO.Path.GetDirectoryName(path)))
                    Directory.CreateDirectory(path);
                else
                {
                    AppLogger.For(nameof(ServiceRepositoryPermissions)).Warning("Backup destination {Path} is missing; its access is granted when settings are next applied", path);
                    continue;
                }
            }
            // Hold every actual ancestor against replacement while opening and
            // visiting this repository. MAXIMUM_ALLOWED on the repository
            // itself also suppresses the Windows ACL API's automatic recursive
            // inheritance propagation. Ancestors are only held, so they are
            // opened in a way another program's working folder cannot block.
            var pins = new List<ServiceObjectPermissions>();
            try
            {
                var ancestors = new Stack<string>();
                for (string? parent = path; parent is not null; parent = System.IO.Path.GetDirectoryName(parent)) ancestors.Push(parent);
                while (ancestors.TryPop(out var parent)) pins.Add(new ServiceObjectPermissions(parent, workingFolder: ancestors.Count > 0));
                var root = pins[^1];
                // Another drive, or the same one formatted again, can carry the
                // same folder name; that is expected for external drives.
                if (retained.TryGetValue(path, out var saved) && saved.Identity != root.Identity && !external.Contains(path))
                    throw new IOException("A backup destination folder was replaced after service setup. Review its location before changing background access: " + path);
                retained[path] = new(path, root.Identity);
                // Record the scope BEFORE the first change. An interrupted
                // grant can be retried, or its exact service ACEs removed.
                ServiceSetup.WriteRecord(installation, FileName, JsonSerializer.SerializeToUtf8Bytes(
                    new Record(1, binding.ServiceName, retained.Values.ToArray()), ServiceRunAuthorization.Json));
                ChangeTree(path, root, sid, grant: true, depth: 0);
            }
            finally { for (var i = pins.Count - 1; i >= 0; i--) pins[i].Dispose(); }
        }
    }

    internal static IEnumerable<string> RepositoryRoots(string installation, AppConfig config)
    {
        var used = config.Backups.SelectMany(b => b.Targets).Select(t => t.DestinationId).ToHashSet(StringComparer.Ordinal);
        var paths = config.Destinations.Where(d => used.Contains(d.Id) && d.Kind == DestinationKind.LocalFolder)
            .Select(d => ServiceInstallationSecurity.CanonicalLocalPath(d.PathOrSubpath))
            .Concat(ServiceExternalDrives.ConnectedRoots(config)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (path.Length <= 3 || Contains(path, installation)
                || config.Backups.SelectMany(b => b.SourcePaths).Any(source => Contains(path, System.IO.Path.GetFullPath(source))))
                throw new SecurityException("Choose a dedicated backup destination folder. Background access cannot be granted to a drive root, the application folder or a folder containing a backup source.");
            yield return path;
        }
    }

    private static bool Contains(string parent, string child) => child.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || child.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    internal static void Revoke(string installation, ServiceWorkerBinding binding)
        => RevokeExcept(installation, binding, Array.Empty<string>(), retainRecord: true);

    internal static void RevokeForRetirement(string installation, ServiceWorkerBinding binding, string serviceSid)
        => RevokeExcept(installation, binding, Array.Empty<string>(), retainRecord: true, serviceSid);

    internal static void RevokeUnused(string installation, ServiceWorkerBinding binding, AppConfig config)
        => RevokeExcept(installation, binding, RepositoryRoots(installation, config).ToArray(), retainRecord: false);

    private static void RevokeExcept(string installation, ServiceWorkerBinding binding, string[] keep, bool retainRecord, string? knownServiceSid = null)
    {
        ServiceRegistration.RequireAdministrator();
        var sid = new SecurityIdentifier(knownServiceSid ?? binding.ServiceSid);
        var record = ReadRecord(installation, binding);
        foreach (var saved in record.Roots.Where(r => !keep.Contains(r.Path, StringComparer.OrdinalIgnoreCase)))
        {
            // A folder that no longer exists carries no service permissions.
            if (!Directory.Exists(saved.Path)) continue;
            var pins = new List<ServiceObjectPermissions>();
            try
            {
                var ancestors = new Stack<string>();
                for (string? parent = saved.Path; parent is not null; parent = Path.GetDirectoryName(parent)) ancestors.Push(parent);
                while (ancestors.TryPop(out var parent)) pins.Add(new ServiceObjectPermissions(parent, workingFolder: ancestors.Count > 0));
                if (pins[^1].Identity != saved.Identity)
                    throw new IOException("A destination folder was replaced since service setup. Its permissions were not changed: " + saved.Path);
                // Remove only this installation's explicit service rules. The
                // owner may have legitimately changed other permissions since
                // activation; restoring an old complete ACL would erase those.
                ChangeTree(saved.Path, pins[^1], sid, grant: false, depth: 0);
            }
            finally { for (var i = pins.Count - 1; i >= 0; i--) pins[i].Dispose(); }
        }
        // Keep the small record until service removal finishes. A crash midway
        // through removal can safely repeat the same, identity-checked work.
        if (!retainRecord)
            ServiceSetup.WriteRecord(installation, FileName, JsonSerializer.SerializeToUtf8Bytes(
                record with { Roots = record.Roots.Where(r => keep.Contains(r.Path, StringComparer.OrdinalIgnoreCase)).ToArray() }, ServiceRunAuthorization.Json));
    }

    internal static Record ReadRecord(string installation, ServiceWorkerBinding binding)
    {
        var path = ServicePortableLayout.Record(installation, FileName);
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            ServiceInstallationSecurity.ValidateProtectedRecord(path, installation);
            if (file.Length is <= 0 or > 1024 * 1024) throw new InvalidDataException("The repository-access record is invalid.");
            var saved = JsonSerializer.Deserialize<Record>(file, ServiceRunAuthorization.Json) ?? throw new InvalidDataException();
            if (saved.Version != 1 || saved.ServiceName != binding.ServiceName || saved.Roots is null
                || saved.Roots.Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != saved.Roots.Length
                || saved.Roots.Any(r => r.Path.Length <= 3 || string.IsNullOrWhiteSpace(r.Identity))) throw new InvalidDataException();
            foreach (var root in saved.Roots) ServiceInstallationSecurity.CanonicalLocalPath(root.Path);
            return saved;
        }
        catch (FileNotFoundException) { return new(1, binding.ServiceName, Array.Empty<Root>()); }
    }

    // Test entry also performs real Windows handle/ACL operations, confined to
    // its caller's disposable directory. Production pins all ancestors above.
    internal static void ChangeTree(string path, ServiceObjectPermissions root, SecurityIdentifier sid, bool grant, int depth)
    {
        if (depth > 256) throw new IOException("The repository directory tree is too deep to review safely.");
        root.SetDacl(ChangeDescriptor(root.Read(), sid, directory: true, grant));
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var directory = (File.GetAttributes(entry) & FileAttributes.Directory) != 0;
            using var child = new ServiceObjectPermissions(entry, directory);
            if (directory) ChangeTree(entry, child, sid, grant, depth + 1);
            else child.SetDacl(ChangeDescriptor(child.Read(), sid, directory: false, grant));
        }
    }

    internal static string ChangeDescriptor(string original, SecurityIdentifier sid, bool directory, bool grant)
    {
        if (!sid.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal)) throw new SecurityException("A dedicated service identity is required.");
        FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity();
        security.SetSecurityDescriptorSddlForm(original, AccessControlSections.Owner | AccessControlSections.Access);
        security.PurgeAccessRules(sid);
        if (grant) security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify | FileSystemRights.Synchronize,
            directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
            PropagationFlags.None, AccessControlType.Allow));
        return security.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
    }
}
