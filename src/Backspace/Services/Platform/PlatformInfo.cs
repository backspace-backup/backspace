using System;
using System.Runtime.InteropServices;

namespace Backspace.Services.Platform;

/// <summary>
/// Single source of truth for "what OS is this" decisions. Wraps
/// <see cref="OperatingSystem"/> and <see cref="RuntimeInformation"/>
/// behind a tiny surface so the rest of the codebase doesn't repeat
/// the same three checks in twenty places.
///
/// Keep this class boring. If a feature gate needs more nuance than
/// "is this Windows / macOS / Linux", expose a named property here
/// rather than threading raw OS checks through the call site.
/// </summary>
public static class PlatformInfo
{
    public static bool IsWindows { get; } = OperatingSystem.IsWindows();
    public static bool IsMacOS   { get; } = OperatingSystem.IsMacOS();
    public static bool IsLinux   { get; } = OperatingSystem.IsLinux();
    public static bool IsUnix    { get; } = !IsWindows;

    /// <summary>
    /// Stable, human-readable RID label for logs / config / install
    /// surfaces. <see cref="RuntimeInformation.RuntimeIdentifier"/> can
    /// vary between published-self-contained ("win-x64") and live
    /// runtimes ("win10-x64", "win11-x64") in ways that make
    /// log-grepping painful.
    /// </summary>
    public static string DisplayPlatform =>
        IsWindows ? "Windows" :
        IsMacOS   ? "macOS"   :
        IsLinux   ? "Linux"   :
                    "Unknown";

    /// <summary>
    /// Whether the current process is running with administrator /
    /// root privileges. VSS backups on Windows need this; restic's
    /// <c>--use-fs-snapshot</c> fails without it. On Unix this is the
    /// effective-UID-zero check; on Windows it's the BUILTIN\Administrators
    /// role on the current token. The check is cheap and is cached
    /// for the process lifetime — elevation never changes while a
    /// process is running.
    /// </summary>
    public static bool IsProcessElevated { get; } = ComputeIsProcessElevated();

    private static bool ComputeIsProcessElevated()
    {
        try
        {
            if (IsWindows)
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            // POSIX: euid 0 = root.
            return Environment.UserName == "root"
                || Environment.GetEnvironmentVariable("SUDO_USER") is not null;
        }
        catch
        {
            // Defensive — if any platform API throws, assume unprivileged.
            // A backup that needs elevation will surface the failure
            // through the engine, not silently run unprivileged thinking
            // it had admin.
            return false;
        }
    }
}
