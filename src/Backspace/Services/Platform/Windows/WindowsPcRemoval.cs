using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// "Remove Backspace from this PC" (U101, 2 November 2026): undoes
/// everything Backspace added to Windows outside its own folder, so deleting
/// that folder afterwards leaves nothing behind. Your settings, saved
/// sign-ins and backups are not deleted: the background service hands the
/// sign-ins back first, and Backspace.exe with Backspace.config stays where
/// it is.
/// </summary>
internal static class WindowsPcRemoval
{
    internal const string ForgetArgument = "--forget-local-files";

    internal static string LocalFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Backspace");

    /// <summary>What is removed, for the confirmation.</summary>
    internal const string Summary =
        "Backspace removes what it added to Windows:\n" +
        "• the background service (Windows asks once), after it hands your saved sign-ins back\n" +
        "• the entry that starts Backspace when you sign in\n" +
        "• \"Restore an older version\" in Explorer's right-click menu\n" +
        "• its engines and caches in %LOCALAPPDATA%\\Backspace, and tasks left by older versions\n\n" +
        "Backspace then quits. Backspace.exe, your settings (Backspace.config) and your backups stay where they are: " +
        "open Backspace.exe again to keep using it, or delete its folder to remove it completely.";

    /// <summary>Everything but the local files, which the helper deletes
    /// once this process has exited (it is still using them).</summary>
    internal static async Task RunAsync(string installation, bool uninstallWinFsp, IBackgroundBackupSetup background, Action<string> progress)
    {
        progress("Turning administrator access off…");
        var state = (await background.InspectAsync().ConfigureAwait(false)).State;
        if (state is not (BackgroundBackupState.Off or BackgroundBackupState.Unavailable))
            await background.RemoveAsync().ConfigureAwait(false);
        progress("Removing the sign-in entry and Explorer menu…");
        var signIn = SignInStart.Create();
        var executable = Path.Combine(installation, "Backspace.exe");
        if (SignInStart.StartsThisCopy(signIn, executable)) signIn.Unregister();
        WindowsShellIntegration.Uninstall();
        try { LegacyUserTasks.RemoveForCurrentUser(); }
        catch (Exception ex) { AppLogger.For(nameof(WindowsPcRemoval)).Warning(ex, "Tasks left by older versions could not all be removed"); }
        if (uninstallWinFsp)
        {
            progress("Uninstalling WinFsp…");
            UninstallWinFsp();
        }
    }

    /// <summary>Started as Backspace quits: waits for it, then deletes
    /// %LOCALAPPDATA%\Backspace (engines, caches, locks). Backspace
    /// recreates it when opened again.</summary>
    internal static void StartForgettingLocalFiles()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(ForgetArgument);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var _ = Process.Start(start);
    }

    internal static int ForgetLocalFiles(int parentId)
    {
        try { using var parent = Process.GetProcessById(parentId); parent.WaitForExit(120_000); }
        catch (ArgumentException) { /* Already gone. */ }
        var folder = LocalFolder;
        for (var attempt = 0; attempt < 20 && Directory.Exists(folder); attempt++)
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(1000); }
        }
        return Directory.Exists(folder) ? 4 : 0;
    }

    /// <summary>The WinFsp driver for mounting backups as drives. Other apps
    /// may use it too, so it is removed only when asked.</summary>
    internal static void UninstallWinFsp()
    {
        var code = WinFspProductCode();
        if (code is null) return;
        var start = new ProcessStartInfo("msiexec.exe") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "/x", code, "/qn", "/norestart" }) start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("Windows did not start the WinFsp uninstaller.");
            process.WaitForExit();
            if (process.ExitCode is not (0 or 1605 or 3010))
                throw new IOException($"WinFsp could not be uninstalled (Windows Installer code {process.ExitCode}). Remove it in Windows Settings → Apps.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { throw new OperationCanceledException("Windows approval was cancelled. WinFsp stays installed; everything else was removed.", ex); }
    }

    internal static string? WinFspProductCode()
    {
        foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var uninstall = Registry.LocalMachine.OpenSubKey(root);
            if (uninstall is null) continue;
            foreach (var name in uninstall.GetSubKeyNames().Where(n => n.StartsWith('{') && n.EndsWith('}')))
            {
                using var entry = uninstall.OpenSubKey(name);
                if (entry?.GetValue("DisplayName") is string display && display.StartsWith("WinFsp", StringComparison.OrdinalIgnoreCase))
                    return name;
            }
        }
        return null;
    }
}
