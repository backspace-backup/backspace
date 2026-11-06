using System;
using System.Security;
using System.Security.Principal;
using System.Text.Json.Serialization;

namespace Backspace.Services.Platform.Windows;

/// <summary>The background service's identity and paths, read from the
/// service's registry key (<see cref="SystemService"/>, U100). Nothing in a run
/// request can replace these.</summary>
internal sealed record ServiceWorkerBinding(int Version, string ServiceName, string OwnerSid, string DataDirectory, bool Activated = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Installation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EngineRoot = null)
{
    /// <summary>Set by the service for itself and its workers.</summary>
    internal const string ServiceNameVariable = "BACKSPACE_SYSTEM_SERVICE";
    internal string ServiceSid => SystemService.LocalSystemSid;
    internal bool CanRun => Activated;

    /// <summary>The service and its workers know their service by name; its
    /// registry record must point at this very copy.</summary>
    internal static ServiceWorkerBinding Load(string installation)
    {
        if (Environment.GetEnvironmentVariable(ServiceNameVariable) is not { Length: > 0 } name)
            throw new SecurityException("This command can run only as the installed Windows service.");
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != SystemService.LocalSystemSid)
            throw new SecurityException("This command can run only as the installed Windows service.");
        var registration = SystemService.Read(name, null)
            ?? throw new SecurityException("The background service record is missing.");
        if (!SystemService.SamePath(registration.Installation, installation))
            throw new SecurityException("The background service record belongs to another copy of Backspace.");
        return registration.Binding;
    }

    /// <summary>This account's service for this copy, as its window sees it.</summary>
    internal static ServiceWorkerBinding LoadForOwner(string installation)
        => ForDesktop(installation) ?? throw new System.IO.IOException("There is no background service for this copy of Backspace.");

    /// <summary>The service this copy's window talks to; null when there is none.</summary>
    internal static ServiceWorkerBinding? ForDesktop(string installation)
        => SystemService.ForCopy(installation)?.Binding;

    internal void ValidateWorkerIdentity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != SystemService.LocalSystemSid || Environment.UserInteractive)
            throw new SecurityException("This command can run only as the installed Windows service.");
    }
}
