using System;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backspace.Services.Platform.Windows;

/// <summary>Installer-owned identity and paths. Versions 1 and 2 (a virtual
/// service account) are read only from beside the protected executable;
/// version 3 (Windows' own account, U100) from the service's registry key
/// (<see cref="SystemService"/>). Nothing in a run request can replace these.</summary>
internal sealed record ServiceWorkerBinding(int Version, string ServiceName, string OwnerSid, string DataDirectory, bool Activated = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Installation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EngineRoot = null)
{
    internal const string FileName = "service-installation.json";
    /// <summary>Set by the version-3 service for itself and its workers.</summary>
    internal const string ServiceNameVariable = "BACKSPACE_SYSTEM_SERVICE";
    internal bool IsSystem => Version == SystemService.Version;
    internal string ServiceSid => IsSystem ? SystemService.LocalSystemSid
        : ((SecurityIdentifier)new NTAccount("NT SERVICE", ServiceName).Translate(typeof(SecurityIdentifier))).Value;
    internal bool CanRun => Version == 1 || Activated;

    internal static ServiceWorkerBinding Load(string installation)
    {
        if (RunningAsSystemService(installation) is { } system) return system;
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
        if (SystemService.ForCopy(installation) is { } system) return system.Binding;
        var binding = ReadProtectedBinding(installation);
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != binding.OwnerSid)
            throw new SecurityException("This backup service belongs to another Windows account.");
        return binding;
    }

    /// <summary>The service this copy's window talks to: the version-3
    /// service, or an earlier one still set up here; null when there is none.</summary>
    internal static ServiceWorkerBinding? ForDesktop(string installation)
    {
        if (SystemService.ForCopy(installation) is { } system) return system.Binding;
        if (ServiceRetirement.Read(installation) is not null) return null;
        if (!File.Exists(ServicePortableLayout.Record(installation, FileName))) return null;
        return LoadForOwner(installation);
    }

    private static ServiceWorkerBinding ReadProtectedBinding(string installation)
    {
        var path = ServicePortableLayout.ExistingRecord(installation, FileName);
        using var file = FileAccessRetry.Get(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), path);
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

    /// <summary>The version-3 service and its workers know their service by
    /// name; its registry record must point at this very copy.</summary>
    private static ServiceWorkerBinding? RunningAsSystemService(string installation)
    {
        if (Environment.GetEnvironmentVariable(ServiceNameVariable) is not { Length: > 0 } name) return null;
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != SystemService.LocalSystemSid) return null;
        var registration = SystemService.Read(name, null)
            ?? throw new SecurityException("The background service record is missing.");
        if (!SystemService.SamePath(registration.Installation, installation))
            throw new SecurityException("The background service record belongs to another copy of Backspace.");
        return registration.Binding;
    }

    internal void ValidateWorkerIdentity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (IsSystem)
        {
            if (identity.User?.Value != SystemService.LocalSystemSid || Environment.UserInteractive)
                throw new SecurityException("This command can run only as the installed Windows service.");
            return;
        }
        if (identity.User?.Value != ServiceSid || Environment.UserInteractive)
            throw new SecurityException("This command can run only as the installed Windows service.");
        var principal = new WindowsPrincipal(identity);
        if (principal.IsInRole(WindowsBuiltInRole.Administrator)
            || !principal.IsInRole(WindowsBuiltInRole.BackupOperator))
            throw new SecurityException("The backup service requires its restricted Backup Operators identity.");
    }
}
