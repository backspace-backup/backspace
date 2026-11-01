using System;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Backspace.Services.WinFsp;

/// <summary>
/// Detects whether the WinFSP runtime (winfsp-x64.dll + the FspService
/// kernel driver) is installed on this host. WinFsp.Net is a thin
/// managed wrapper — without the native runtime, every Mount() call
/// fails immediately with a DllNotFoundException, so we check up-front
/// and surface an actionable "install WinFSP" message instead.
///
/// WinFSP ships as an MSI from https://github.com/winfsp/winfsp/releases.
/// It records its install dir under
/// HKLM\Software\WOW6432Node\WinFsp\InstallDir (32-bit registry view,
/// because the installer is 32-bit even on x64 systems). The native
/// runtime sits at &lt;InstallDir&gt;\bin\winfsp-x64.dll.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WinFspRuntimeDetector
{
    /// <summary>True when the WinFSP runtime DLL is present on disk
    /// AND the registry entry pointing at it exists. Both checks are
    /// belt-and-braces: a half-uninstalled WinFSP leaves the registry
    /// entry but removes the DLL, which is the failure mode we most
    /// want to catch before calling Mount().</summary>
    public static bool IsInstalled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var installDir = ReadInstallDir();
            if (string.IsNullOrEmpty(installDir)) return false;
            var dll = Path.Combine(installDir, "bin", "winfsp-x64.dll");
            return File.Exists(dll);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Path to the WinFSP install dir, or null when not
    /// installed / unreadable. Exposed so the UI can show the user
    /// where the runtime lives in the "WinFSP runtime check"
    /// diagnostic.</summary>
    public static string? GetInstallDir()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return ReadInstallDir(); } catch { return null; }
    }

    /// <summary>Stable URL we point the user at when WinFSP isn't
    /// installed. The releases page rather than a direct MSI link
    /// so the user gets the latest signed installer rather than a
    /// bit-rotted version we hardcoded.</summary>
    public const string InstallUrl = "https://github.com/winfsp/winfsp/releases/latest";

    [SupportedOSPlatform("windows")]
    private static string? ReadInstallDir()
    {
        using var root = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine, RegistryView.Registry32);
        using var key = root.OpenSubKey(@"Software\WinFsp", writable: false);
        return key?.GetValue("InstallDir") as string;
    }
}
