using System;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text.Json;

namespace Backspace.Services.Platform.Windows;

/// <summary>Installer-owned identity and paths, read only from beside the
/// protected executable. Nothing in a run request can replace these values.</summary>
internal sealed record ServiceWorkerBinding(int Version, string ServiceName, string OwnerSid, string DataDirectory, bool Activated = false)
{
    internal const string FileName = "service-installation.json";
    internal string ServiceSid => ((SecurityIdentifier)new NTAccount("NT SERVICE", ServiceName)
        .Translate(typeof(SecurityIdentifier))).Value;
    internal bool CanRun => Version == 1 || Activated;

    internal static ServiceWorkerBinding Load(string installation)
    {
        var binding = ReadProtectedBinding(installation);
        ServiceInstallationSecurity.ValidatePrivateDataDirectory(binding.DataDirectory, binding.ServiceSid, installation);
        return binding;
    }

    /// <summary>The desktop authenticates the protected binding and its owner,
    /// then uses the pipe. It must not need access to the service's private vault
    /// directory just to discover that endpoint. The service validates that
    /// directory independently through Load before starting its listener.</summary>
    internal static ServiceWorkerBinding LoadForOwner(string installation)
    {
        var binding = ReadProtectedBinding(installation);
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != binding.OwnerSid)
            throw new SecurityException("This backup service belongs to another Windows account.");
        return binding;
    }

    private static ServiceWorkerBinding ReadProtectedBinding(string installation)
    {
        var path = ServicePortableLayout.ExistingRecord(installation, FileName);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ServiceInstallationSecurity.ValidateProtectedRecord(path, installation);
        if (file.Length is <= 0 or > 8192) throw new InvalidDataException("Invalid service installation record.");
        var binding = JsonSerializer.Deserialize<ServiceWorkerBinding>(file, ServiceRunAuthorization.Json)
            ?? throw new InvalidDataException("Missing service installation record.");
        if (binding.Version is not (1 or 2) || string.IsNullOrEmpty(binding.ServiceName)
            || binding.ServiceName.Length > 100 || !binding.ServiceName.StartsWith("Backspace", StringComparison.Ordinal)
            || !System.Text.RegularExpressions.Regex.IsMatch(binding.ServiceName, "^[A-Za-z0-9-]+$")
            || !new SecurityIdentifier(binding.OwnerSid).IsAccountSid())
            throw new SecurityException("Invalid service installation identity.");
        ServiceInstallationSecurity.CanonicalLocalPath(binding.DataDirectory);
        ServicePortableLayout.ValidateDataLocation(binding, installation);
        return binding;
    }

    internal void ValidateWorkerIdentity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != ServiceSid || Environment.UserInteractive)
            throw new SecurityException("This command can run only as the installed Windows service.");
        var principal = new WindowsPrincipal(identity);
        if (principal.IsInRole(WindowsBuiltInRole.Administrator)
            || !principal.IsInRole(WindowsBuiltInRole.BackupOperator))
            throw new SecurityException("The backup service requires its restricted Backup Operators identity.");
    }
}
