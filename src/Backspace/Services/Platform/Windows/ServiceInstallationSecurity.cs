using System;
using System.IO;
using System.Security;

namespace Backspace.Services.Platform.Windows;

/// <summary>Where the background service can run from. Where Backspace lives,
/// and who can replace its files, is the owner's responsibility, as with
/// portable restic run from a scheduled task (product decision, 28 October
/// 2026). Links, aliases and network locations are still refused.</summary>
internal static class ServiceInstallationSecurity
{
    /// <summary>Accept any local folder, including a drive root or a folder
    /// shared with other files. Refuse network drives, which the service cannot
    /// reach before sign-in.</summary>
    internal static void ValidateServiceLocation(string installationRoot)
    {
        var root = CanonicalLocalPath(installationRoot);
        if (new DriveInfo(root).DriveType == DriveType.Network)
            throw new SecurityException("Backspace is on a network drive, which Windows cannot reach before you sign in. Copy the whole Backspace folder to this PC and try again.");
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
}
