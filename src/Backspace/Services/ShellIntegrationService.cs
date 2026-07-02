using Backspace.Services.Platform;

namespace Backspace.Services;

/// <summary>
/// Cross-platform shell-integration facade. On Windows, registers the
/// "Restore an older version with Backspace" right-click verb under
/// HKCU. On macOS / Linux the file-manager extension story is per-host
/// (Finder Quick Actions, Nautilus / Dolphin / Thunar plugins) and
/// outside the scope of v1 — this class returns false from
/// <see cref="IsInstalled"/>, <see cref="Install"/>, and
/// <see cref="Uninstall"/> so the UI naturally hides the offer.
/// </summary>
public static class ShellIntegrationService
{
    /// <summary>
    /// True iff the current platform supports installing a shell
    /// integration. Drives whether onboarding/settings show the
    /// "Add right-click restore" toggle at all.
    /// </summary>
    public static bool IsSupportedOnThisPlatform => PlatformInfo.IsWindows;

    public static bool IsInstalled()
    {
#if WINDOWS
        if (PlatformInfo.IsWindows) return Platform.Windows.WindowsShellIntegration.IsInstalled();
#endif
        return false;
    }

    public static bool Install()
    {
#if WINDOWS
        if (PlatformInfo.IsWindows) return Platform.Windows.WindowsShellIntegration.Install();
#endif
        return false;
    }

    public static bool Uninstall()
    {
#if WINDOWS
        if (PlatformInfo.IsWindows) return Platform.Windows.WindowsShellIntegration.Uninstall();
#endif
        return false;
    }

    /// <summary>
    /// Idempotent sweep of stale shell-integration verbs from previous
    /// Backspace dev builds (the "Backspace, no icon" entry users
    /// reported) and from the predecessor app Duplimate (whose
    /// "DuplimateRestore" verb is still registered under HKCU on
    /// upgraded installs). Run unconditionally on app startup so the
    /// user doesn't have to actively reinstall the integration to
    /// get the stale entries removed.
    /// </summary>
    public static void CleanLegacyEntries()
    {
#if WINDOWS
        if (PlatformInfo.IsWindows) Platform.Windows.WindowsShellIntegration.CleanLegacyEntries();
#endif
    }
}
