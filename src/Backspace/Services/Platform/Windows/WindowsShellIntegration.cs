using System;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Serilog;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// Per-user (HKCU) Windows Explorer integration: registers a
/// "Restore an older version with Backspace" right-click entry on
/// files and folders. Per-user means we don't need elevation — install
/// is a registry write under the user's own classes hive.
///
/// On click, Explorer launches Backspace with
/// <c>--restore-prompt "&lt;path&gt;"</c>; <c>Program.cs</c> picks that up
/// and routes the user to the Restore tab pre-selecting the matching
/// backup + source.
///
/// We deliberately avoid a real shell extension (a registered COM DLL)
/// for v0:
///   - COM extensions need C++ or a packaged out-of-process .NET host;
///     both are deployment burdens disproportionate to the feature.
///   - The verb-style approach below is the same mechanism Notepad++,
///     7-Zip, Git, VS Code etc. use for "Edit with…" / "Open with…"
///     and is well-understood by Windows.
///   - It works on every modern Windows version (10/11) without
///     install-time elevation or per-machine registration.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsShellIntegration
{
    private static ILogger _log => AppLogger.For(nameof(WindowsShellIntegration));

    private const string VerbName = "BackspaceRestore";
    private const string VerbLabel = "Restore an older version (Backspace)";

    // Per-class registration paths under HKCU\Software\Classes.
    // We register on three classes:
    //   - "*"           → all files (right-click on a file in Explorer)
    //   - "Directory"   → right-click on a folder
    //   - "Directory\\Background" → right-click on empty area of a folder
    private static readonly string[] HkcuClassPaths =
    {
        @"Software\Classes\*\shell\" + VerbName,
        @"Software\Classes\Directory\shell\" + VerbName,
        @"Software\Classes\Directory\Background\shell\" + VerbName,
    };

    // Legacy verb names we've seen in the wild that need cleanup on
    // startup. The first three are old Backspace registrations from
    // earlier dev builds (some without an Icon value, which is the
    // "Backspace with no icon" entry users reported). The Duplimate
    // entries are leftovers from the predecessor app — Backspace
    // started as a Duplimate rename, so users migrating from a
    // Duplimate install have stale verb keys until we clean them up.
    private static readonly string[] LegacyVerbNames =
    {
        "Backspace",
        "BackspaceRestoreFile",      // pre-merge variant
        "DuplimateRestore",          // predecessor's primary verb
        "Duplimate",                 // predecessor's catch-all
        "DuplimateRestoreFile",      // predecessor's per-file variant
    };

    // Classes the legacy verbs may have been registered under. Same
    // three classes we use today, applied per legacy verb name.
    private static readonly string[] LegacyClassPrefixes =
    {
        @"Software\Classes\*\shell\",
        @"Software\Classes\Directory\shell\",
        @"Software\Classes\Directory\Background\shell\",
    };

    /// <summary>True if the verb is currently registered for the user.</summary>
    public static bool IsInstalled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(HkcuClassPaths[0]);
            return k is not null;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "ShellIntegration probe failed");
            return false;
        }
    }

    /// <summary>
    /// Writes the registry verbs so Explorer shows the entry. Idempotent:
    /// re-running just overwrites the existing keys. Returns true on
    /// success.
    /// </summary>
    public static bool Install()
    {
        try
        {
            var exe = ResolveExePath();
            if (exe is null)
            {
                _log.Warning("ShellIntegration install skipped — could not resolve exe path");
                return false;
            }

            // Sweep stale entries first so an upgrade from a build
            // that left an iconless verb (or from the predecessor
            // app, see LegacyVerbNames) ends up with a clean shell
            // registration instead of two entries side-by-side.
            CleanLegacyEntries();

            // The icon shown next to the verb is the app's own exe.
            // Use the explicit ",0" suffix so Windows takes the first
            // icon in the binary — without it some Explorer versions
            // showed a generic placeholder for the verb (the bug the
            // user reported as "Backspace entry with no icon").
            // Background-context registration uses %V instead of %1
            // because Explorer passes the folder path that way.
            var iconRef = "\"" + exe + "\",0";
            for (int i = 0; i < HkcuClassPaths.Length; i++)
            {
                using var verbKey = Registry.CurrentUser.CreateSubKey(HkcuClassPaths[i])!;
                verbKey.SetValue("", VerbLabel);
                verbKey.SetValue("Icon", iconRef);
                using var cmdKey = verbKey.CreateSubKey("command")!;
                var arg = i == 2 ? "%V" : "%1"; // background uses %V
                cmdKey.SetValue("", $"\"{exe}\" --restore-prompt \"{arg}\"");
            }
            _log.Information("ShellIntegration installed at HKCU for {Exe}", exe);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "ShellIntegration install failed");
            return false;
        }
    }

    /// <summary>Removes the verb registration. Safe to call when the
    /// keys don't exist (no-op). Also sweeps the legacy verb names
    /// — see <see cref="CleanLegacyEntries"/> — so a user who
    /// disables the integration doesn't keep stale "Backspace" /
    /// "Duplimate" entries lying around.</summary>
    public static bool Uninstall()
    {
        var ok = true;
        foreach (var p in HkcuClassPaths)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(p, throwOnMissingSubKey: false); }
            catch (Exception ex)
            {
                _log.Warning(ex, "ShellIntegration uninstall failed at {Path}", p);
                ok = false;
            }
        }
        CleanLegacyEntries();
        if (ok) _log.Information("ShellIntegration removed from HKCU");
        return ok;
    }

    /// <summary>
    /// Sweep stale shell-integration verbs:
    ///   - "Backspace" / "BackspaceRestoreFile" — old Backspace dev
    ///     builds that registered without an Icon value, surfacing
    ///     in Explorer as the user-reported "Backspace entry with
    ///     no icon".
    ///   - "DuplimateRestore" / "Duplimate" / "DuplimateRestoreFile"
    ///     — left behind by the predecessor app that Backspace was
    ///     renamed from. Migrating users had both entries in their
    ///     right-click menu.
    ///
    /// Safe to call at any time; missing keys are skipped. We run
    /// this automatically once per app startup (see <see cref="App"/>)
    /// AND from <see cref="Install"/> / <see cref="Uninstall"/> so
    /// there's no scenario in which the legacy entries stay.
    /// </summary>
    public static void CleanLegacyEntries()
    {
        int removed = 0;
        foreach (var prefix in LegacyClassPrefixes)
        {
            foreach (var verb in LegacyVerbNames)
            {
                var path = prefix + verb;
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(path))
                        if (k is null) continue;
                    Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
                    removed++;
                    _log.Information("Removed legacy shell verb {Path}", path);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Failed to clean legacy shell verb {Path}", path);
                }
            }
        }
        if (removed == 0)
            _log.Debug("Legacy shell-verb sweep: nothing to clean");
    }

    /// <summary>
    /// The path Explorer should launch. Prefers <see cref="Environment.ProcessPath"/>
    /// (set by the runtime when the host is the actual exe); falls back
    /// to the executing assembly location if needed.
    /// </summary>
    private static string? ResolveExePath()
    {
        var p = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(p) && File.Exists(p)) return p;
        var asm = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrWhiteSpace(asm) && File.Exists(asm)) return asm;
        return null;
    }
}
