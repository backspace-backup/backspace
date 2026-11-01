using System;
using System.IO;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Backspace.Services.Platform.Windows;

/// <summary>Checks the service's own protected state before it is trusted:
/// the windows-service folder (approval records, coordination files and the
/// private vault) must not be changeable by ordinary accounts. The portable
/// program itself is not locked down. Where Backspace lives, and who can
/// replace its files, is the owner's responsibility, as with portable restic
/// or Duplicacy run from a scheduled task (product decision, 28 October 2026).
/// Links and network locations are still refused.</summary>
internal static class ServiceInstallationSecurity
{
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly Lazy<SecurityIdentifier> TrustedInstaller = new(() =>
        (SecurityIdentifier)new NTAccount("NT SERVICE", "TrustedInstaller").Translate(typeof(SecurityIdentifier)));

    // Create-file/create-directory on an ancestor is harmless to an existing
    // protected child. DELETE can rename the ancestor itself; DELETE_CHILD,
    // WRITE_DAC and WRITE_OWNER can also permit replacement.
    private const FileSystemRights AncestorMutation = FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
    private const FileSystemRights ContentMutation = AncestorMutation
        | FileSystemRights.WriteData | FileSystemRights.AppendData
        | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes;

    internal static bool IsTrustedInstallerIdentity(SecurityIdentifier sid) =>
        sid.Equals(SystemSid) || sid.Equals(Administrators) || sid.Equals(TrustedInstaller.Value);

    /// <summary>Reject permissive, noncanonical and null ACLs rather than trying
    /// to reproduce Windows' group expansion and ordered access-check semantics.
    /// Installer-created objects use explicit, narrow allow rules.</summary>
    internal static void ValidateDescriptor(RawSecurityDescriptor descriptor,
        bool content, Func<SecurityIdentifier, bool> trustedWriter, bool confidential = false)
    {
        if (descriptor.Owner is null || !trustedWriter(descriptor.Owner))
            throw new SecurityException("The backup service installation has an untrusted owner.");
        if ((descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || descriptor.DiscretionaryAcl is null)
            throw new SecurityException("The backup service installation has no restrictive access list.");
        var mutation = confidential ? -1 : (int)(content ? ContentMutation : AncestorMutation);
        foreach (GenericAce entry in descriptor.DiscretionaryAcl)
        {
            if ((entry.AceFlags & AceFlags.InheritOnly) != 0) continue;
            if (entry is not CommonAce ace || ace.IsCallback)
                throw new SecurityException("The backup service installation uses an unsupported access rule.");
            if (ace.AceQualifier == AceQualifier.AccessDenied) continue;
            if (ace.AceQualifier != AceQualifier.AccessAllowed)
                throw new SecurityException("The backup service installation uses an unsupported access rule.");
            // Generic access masks are not necessarily expanded in a raw SDDL.
            const int genericWrite = 0x40000000, genericAll = 0x10000000;
            if ((ace.AccessMask & (mutation | genericWrite | genericAll)) != 0 && !trustedWriter(ace.SecurityIdentifier))
                throw new SecurityException("An untrusted account can change the backup service installation.");
        }
    }

    internal static void ValidateCodeFile(string path, string installationRoot)
    {
        var root = CanonicalLocalPath(installationRoot);
        var file = CanonicalLocalPath(path);
        if (!IsInside(file, root) || file.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Service code must be inside its installation folder.");
        if (!File.Exists(file)) throw new FileNotFoundException("A backup service file is missing.", file);
        // Its permissions are the owner's choice; only redirection is refused.
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            throw new SecurityException("Service code cannot be a link to another file.");
    }

    // A data container may allow creation of unrelated files without permitting
    // replacement of an existing protected record. Executable dependency folders
    // still use the stricter ValidateCodeFile check above.
    internal static void ValidateProtectedRecord(string path, string installationRoot)
    {
        var root = CanonicalLocalPath(installationRoot);
        var file = CanonicalLocalPath(path);
        if (!IsInside(file, root) || file.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("The service record must be inside this portable copy.");
        ValidateObject(file, content: true, IsTrustedInstallerIdentity);
        ValidateAncestors(Path.GetDirectoryName(file)!, root);
    }

    /// <summary>Folders from <paramref name="directory"/> up to the
    /// windows-service folder must not let ordinary accounts replace a child.
    /// Backspace.config and the program folder above it are not checked.</summary>
    internal static void ValidateAncestors(string directory, string installationRoot)
    {
        var boundary = ServicePortableLayout.Service(CanonicalLocalPath(installationRoot));
        for (string? path = CanonicalLocalPath(directory); path is not null && IsInside(path, boundary); path = Path.GetDirectoryName(path))
            ValidateObject(path, content: false, IsTrustedInstallerIdentity);
    }

    /// <summary>Accept any local folder, including a drive root or a folder
    /// shared with other files. Refuse network drives, which the service cannot
    /// reach before sign-in.</summary>
    internal static void ValidateServiceLocation(string installationRoot)
    {
        var root = CanonicalLocalPath(installationRoot);
        if (new DriveInfo(root).DriveType == DriveType.Network)
            throw new SecurityException("Backspace is on a network drive, which Windows cannot reach before you sign in. Copy the whole Backspace folder to this PC and try again.");
    }

    internal static void ValidateCodeTree(string installationRoot, string privateDataDirectory, string serviceSid)
    {
        var root = CanonicalLocalPath(installationRoot);
        var data = CanonicalLocalPath(privateDataDirectory);
        if (root.Equals(data, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Service code and writable data must use separate folders.");
        ValidatePrivateDataDirectory(data, serviceSid, root);
        // The program files themselves are not locked down; the owner decides
        // who may change them. Check only that the engines the service starts
        // are present, local and not redirected elsewhere.
        foreach (var name in ServiceSetup.ProgramFiles)
            ValidateCodeFile(Path.Combine(root, name), root);
    }

    internal static void ValidatePrivateDataDirectory(string path, string serviceSid, string installationRoot)
    {
        var service = new SecurityIdentifier(serviceSid);
        if (!service.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal))
            throw new SecurityException("A dedicated virtual service account is required.");
        var directory = CanonicalLocalPath(path);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The protected service data folder is missing.");
        bool Trusted(SecurityIdentifier sid) => sid.Equals(service) || IsTrustedInstallerIdentity(sid);
        ValidateObject(directory, content: true, Trusted, confidential: true);
        // A data folder inside the portable copy is bounded by its protected
        // windows-service folder. Any other location keeps the full check.
        var boundary = ServicePortableLayout.Service(CanonicalLocalPath(installationRoot));
        var bounded = IsInside(directory, boundary);
        for (var parent = Path.GetDirectoryName(directory); parent is not null && (!bounded || IsInside(parent, boundary)); parent = Path.GetDirectoryName(parent))
            ValidateObject(parent, content: false, IsTrustedInstallerIdentity);
    }

    private static void ValidateObject(string path, bool content, Func<SecurityIdentifier, bool> trusted, bool confidential = false)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new SecurityException("The backup service installation cannot contain links or redirected folders.");
        FileSystemSecurity acl = (attributes & FileAttributes.Directory) != 0
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        ValidateDescriptor(new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0), content, trusted, confidential);
    }

    internal static string CanonicalLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Length < 3 || path[1] != ':'
            || path[2] != '\\' || path.AsSpan(2).Contains(':'))
            throw new SecurityException("The backup service requires an absolute local drive path.");
        var full = Path.GetFullPath(path).TrimEnd('\\');
        if (full.Length == 2) full += "\\";
        // Reject aliases rather than allowing a caller to conceal an ancestor.
        if (!full.Equals(path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            && !(full.Length == 3 && path.Equals(full, StringComparison.OrdinalIgnoreCase)))
            throw new SecurityException("The backup service requires a canonical installation path.");
        foreach (var part in full[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (part.EndsWith(' ') || part.EndsWith('.') || part.Contains('/'))
                throw new SecurityException("The backup service installation path contains an ambiguous name.");
        return full;
    }

    private static bool IsInside(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}
