using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// One Backspace per Windows account (U100, 2 November 2026). Opening another
/// copy (a new download, say) while Backspace is set up elsewhere offers to
/// update that copy, to move Backspace here, or to cancel. Moving keeps the
/// settings, history and saved sign-ins: Backspace.config moves to this
/// folder and the other Backspace.exe is deleted, so only one remains. The
/// background service stops first and starts again from the new place.
/// </summary>
internal static class InstallationHandover
{
    internal const string MoveArgument = "--move-installation-from";

    internal sealed record Other(string Installation, string Version, bool ThisCopyHasSettings);

    /// <summary>Another copy this account uses: the one the background service
    /// runs, or the one that starts at sign-in. Null when there is none, or it
    /// no longer has its settings, or when this copy is itself a set-up
    /// Backspace (its own backups or service): only a fresh copy, such as a
    /// new download, is offered the update or the move.</summary>
    internal static Other? Find(string here)
    {
        if (HasSettings(here) || SystemService.ForCopy(here) is not null) return null;
        var candidates = new[]
        {
            Safe(() => SystemService.ForCurrentUser()?.Installation),
            Safe(() => SignInStart.Create().RegisteredExecutable() is { } exe ? Path.GetDirectoryName(exe) : null),
        };
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate) || SystemService.SamePath(candidate, here)) continue;
            var exe = Path.Combine(candidate, "Backspace.exe");
            if (!File.Exists(exe) || !File.Exists(Path.Combine(ServicePortableLayout.Config(candidate), "config.json"))) continue;
            var version = FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0] ?? "";
            return new(candidate, version, HasSettings(here));
        }
        return null;
    }

    /// <summary>This folder has backups of its own (not just the files this
    /// window created as it opened).</summary>
    internal static bool HasSettings(string installation)
    {
        var config = Path.Combine(ServicePortableLayout.Config(installation), "config.json");
        if (!File.Exists(config)) return false;
        try { return ServiceConfigurationComparison.ReadDesktop(installation).Backups.Count > 0; }
        catch (Exception) { return true; }
    }

    private static T? Safe<T>(Func<T?> read) { try { return read(); } catch (Exception) { return default; } }

    /// <summary>Asks the other copy's window to quit and waits for it.</summary>
    internal static bool QuitOther(string installation, TimeSpan timeout)
    {
        var config = ServicePortableLayout.Config(installation);
        if (!SingleInstanceCoordinator.IsRunning(config)) return true;
        SingleInstanceCoordinator.Forward(config, [SingleInstanceCoordinator.QuitArgument]);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!SingleInstanceCoordinator.IsRunning(config)) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    /// <summary>Replaces the other copy's Backspace.exe with this one and
    /// opens it. Its settings stay where they are.</summary>
    internal static void UpdateOther(Other other)
    {
        if (!QuitOther(other.Installation, TimeSpan.FromSeconds(90)))
            throw new IOException("Backspace is still open from the other folder. Quit it from its tray icon, then try again.");
        // The background service runs that Backspace.exe: stop it while the
        // file is replaced (the owner may), then start it again.
        var system = SystemService.ForCurrentUser() is { } found && SystemService.SamePath(found.Installation, other.Installation) ? found : null;
        if (system is not null) SystemService.StopAndWait(system.ServiceName, TimeSpan.FromSeconds(90));
        try
        {
            var target = Path.Combine(other.Installation, "Backspace.exe");
            FileAccessRetry.Run(() => File.Copy(Environment.ProcessPath!, target, overwrite: true), target);
        }
        finally
        {
            try
            {
                if (system is not null) SystemServiceLifetime.Resume(system, TimeSpan.FromSeconds(60));
            }
            catch (Exception ex) { AppLogger.For(nameof(InstallationHandover)).Warning(ex, "The background service starts when that copy opens"); }
        }
        WindowsUpdateRuntime.Launch(Path.Combine(other.Installation, "Backspace.exe"));
    }

    /// <summary>Starts the move in a helper that waits for this window to
    /// close (it holds this folder's log files open).</summary>
    internal static void StartMove(Other other)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory };
        foreach (var arg in new[] { MoveArgument, other.Installation, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(arg);
        using var _ = Process.Start(start) ?? throw new IOException("Windows did not start the move.");
    }

    /// <summary>The helper: moves Backspace.config here, deletes the other
    /// Backspace.exe, points the sign-in entry and the background service
    /// here (Windows asks once for the service), then opens Backspace.</summary>
    internal static int Move(string from, int parentId)
    {
        var here = AppContext.BaseDirectory.TrimEnd('\\');
        try
        {
            try { using var parent = Process.GetProcessById(parentId); parent.WaitForExit(60_000); }
            catch (ArgumentException) { }
            if (!QuitOther(from, TimeSpan.FromSeconds(90)))
                throw new IOException("Backspace is still open from " + from + ". Quit it from its tray icon, then open this copy again.");
            var system = SystemService.ForCurrentUser() is { } found && SystemService.SamePath(found.Installation, from) ? found : null;
            if (system is not null) SystemService.StopAndWait(system.ServiceName, TimeSpan.FromSeconds(90));
            var target = ServicePortableLayout.Config(here);
            if (Directory.Exists(target))
            {
                if (HasSettings(here)) throw new IOException("This folder already has its own Backspace settings. Nothing was moved.");
                Directory.Delete(target, recursive: true);
            }
            MoveDirectory(ServicePortableLayout.Config(from), target);
            // backrest.exe too: copies from before U175 kept it beside the others.
            foreach (var name in new[] { "Backspace.exe", "backrest.exe", "restic.exe", "rclone.exe", WindowsUpdateRuntime.GateName })
                try { File.Delete(Path.Combine(from, name)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { if (!Directory.EnumerateFileSystemEntries(from).Any()) Directory.Delete(from); } catch (IOException) { }
            var signIn = SignInStart.Create();
            if (signIn.RegisteredExecutable() is { } registered && SystemService.SamePath(Path.GetDirectoryName(registered)!, from))
                signIn.Register(Path.Combine(here, "Backspace.exe"));
            if (system is not null)
            {
                // Quitting the other window recorded a Quit; this is a move.
                SystemServiceLifetime.DeleteRecord(SystemServiceLifetime.PathFor(SystemService.DataDirectoryFor(here)));
                RepointService(here);
            }
            WindowsUpdateRuntime.Launch(Path.Combine(here, "Backspace.exe"));
            return 0;
        }
        catch (Exception ex)
        {
            _ = MessageBoxW(IntPtr.Zero, "Backspace could not be moved here. " + ex.Message, "Backspace", 0x30);
            return 4;
        }
    }

    private static void RepointService(string here)
    {
        var start = new ProcessStartInfo(Path.Combine(here, "Backspace.exe")) { UseShellExecute = true, Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = here };
        foreach (var arg in new[] { "--system-service-move", SystemService.CurrentOwnerSid() }) start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("Windows did not start the background service move.");
            process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException("The background service could not be moved. Open Settings → Administrator access to set it up again.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { throw new IOException("Windows approval was cancelled, so the background service still points at the old folder. Open Settings → Administrator access to finish.", ex); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    /// <summary>Moves the settings folder. A file another program holds
    /// (Dropbox, a virus scanner) is waited out, as long as it takes, and the
    /// original is deleted only after every file was copied whole (U135).</summary>
    private static void MoveDirectory(string from, string to)
    {
        try { FileAccessRetry.Run(() => Directory.Move(from, to), ""); return; }
        catch (IOException) when (!string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase)) { }
        // Another drive: copy everything, then delete the original.
        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(to, Path.GetRelativePath(from, file));
            FileAccessRetry.Run(() => File.Copy(file, copy, overwrite: true), file);
            if (new FileInfo(copy).Length != new FileInfo(file).Length)
                throw new IOException("A settings file was not copied whole. The original folder was kept: " + file);
        }
        FileAccessRetry.Run(() => Directory.Delete(from, recursive: true), from, directoryCleanup: true);
    }
}
