using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;

namespace Backspace.Services;

/// <summary>Coordinates portable replacement with GUI, scheduler and helper
/// process lifetimes. Never kills a process to make an update fit.</summary>
internal static class WindowsUpdateRuntime
{
    internal static WindowsUpdatePackage.Plan? Pending { get; set; }

    /// <summary>The folder holding the program and Backspace.config: beside
    /// Backspace.app on macOS (U245), the program's own folder elsewhere.</summary>
    internal static string Root => OperatingSystem.IsMacOS() ? AppPaths.ExeDirectory : AppContext.BaseDirectory;

    /// <summary>What an update replaces: Backspace.exe, the Linux program, or
    /// Backspace.app on macOS.</summary>
    internal static string? ProgramPath
    {
        get
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !OperatingSystem.IsMacOS()) return exe;
            var bundle = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(exe)));
            return bundle is not null && bundle.EndsWith(".app", StringComparison.Ordinal) ? bundle : null;
        }
    }
    /// <summary>Where updates are prepared, with their record and recovery
    /// launcher: inside Backspace.config, never beside the app (U218, owner,
    /// 12 November 2026: "No clutter, even temporarily").</summary>
    internal static string UpdatesFolder(string root) => Path.Combine(root, AppPaths.AppFolderName, "updates");

    /// <summary>The gate every Backspace process of this copy holds while it
    /// runs; an update or a recovery takes it alone. Throws when an update
    /// holds it past the timeout.</summary>
    internal static IDisposable? AcquireShared(string root, TimeSpan timeout) => RuntimeGate.Shared(root, timeout);

    /// <summary>The gate alone, or null while another process of this copy runs.</summary>
    /// <summary>A process that has just ended (the background service an update
    /// stops, or one that crashed) can hold it for a moment more, so it is
    /// asked again for up to <paramref name="wait"/> (5 seconds by default).</summary>
    internal static IDisposable? TryExclusive(string root, TimeSpan? wait = null)
    {
        var deadline = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            if (RuntimeGate.Exclusive(root) is { } gate) return gate;
            if (DateTime.UtcNow >= deadline) return null;
            Thread.Sleep(100);
        }
    }

    internal static bool CanInstall(out string reason)
    {
        if (!OperatingSystem.IsWindows()) return CanInstallUnix(out reason);
        if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
        { reason = "Automatic installation is available for the Windows x64 release. Other builds can use the release page."; return false; }
        if (!string.IsNullOrEmpty(typeof(WindowsUpdateRuntime).Assembly.Location))
        { reason = "This is a development build. Download the portable Windows release to use automatic installation."; return false; }
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe))
        { reason = "The running application could not be located. Use the manual download instead."; return false; }
        var root = Path.GetDirectoryName(exe)!;
        try
        {
            WindowsUpdatePackage.RejectReparse(root); WindowsUpdatePackage.RejectReparse(exe);
            // Asks Windows whether files may be added and removed here, the
            // way a replacement needs, without writing a probe beside the
            // app (U218: only Backspace.exe and Backspace.config, ever).
            if ((new FileInfo(exe).Attributes & FileAttributes.ReadOnly) != 0 || !MayReplaceIn(root)) throw new IOException();
            reason = ""; return true;
        }
        catch { reason = "Backspace cannot write to its folder. Move the portable app to a writable folder or update it manually."; return false; }
    }

    /// <summary>Linux and macOS (U245): a published build (not a development
    /// one), run from a folder this account may change.</summary>
    private static bool CanInstallUnix(out string reason)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        { reason = "Automatic installation isn't available on this system. Use the release page."; return false; }
        if (!string.IsNullOrEmpty(typeof(WindowsUpdateRuntime).Assembly.Location))
        { reason = "This is a development build. Download a release to use automatic installation."; return false; }
        if (ProgramPath is not { } program || !(File.Exists(program) || Directory.Exists(program)))
        { reason = "The running application could not be located. Use the manual download instead."; return false; }
        if (AppPaths.IsTranslocated(program))
        { reason = "Move Backspace out of Downloads with Finder (to Applications or any folder) to update it from here."; return false; }
        var root = Path.GetDirectoryName(program.TrimEnd('/'))!;
        try
        {
            WindowsUpdatePackage.RejectReparse(root); WindowsUpdatePackage.RejectReparse(program);
            if (Writable(root) == false) throw new IOException();
            reason = ""; return true;
        }
        catch { reason = "Backspace cannot write to its folder. Move it to a folder you can change, or update it manually."; return false; }
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "access", SetLastError = true)]
    private static extern int UnixAccess(string path, int mode);

    /// <summary>Asks the system whether this account may add and remove files
    /// in <paramref name="folder"/>, writing nothing; null when it can't say.</summary>
    private static bool? Writable(string folder)
    {
        try { return UnixAccess(folder, 2 /* W_OK */ | 1 /* X_OK */) == 0; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    /// <summary>Opens the folder asking for the rights to add files and remove
    /// them (FILE_ADD_FILE | FILE_ADD_SUBDIRECTORY | FILE_DELETE_CHILD). Windows
    /// checks the folder's access and creates nothing.</summary>
    internal static bool MayReplaceIn(string folder)
    {
        if (!OperatingSystem.IsWindows()) return true;
        using var handle = CreateFileW(folder, 0x2 | 0x4 | 0x40, 0x7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        return !handle.IsInvalid;
    }

    internal static void ApplyPendingAfterExit(bool enginesStopped)
    {
        var plan = Pending; Pending = null;
        if (plan is null) return;
        bool installed = false;
        string message;
#if WINDOWS
        // The background service runs this Backspace.exe (U100): the owner may
        // stop it without a prompt, and it starts again on the new version.
        Platform.Windows.SystemService.Registration? system = null;
        try { system = Platform.Windows.SystemService.ForCopy(plan.Root); }
        catch (Exception ex) { AppLogger.For(nameof(WindowsUpdateRuntime)).Warning(ex, "Could not look up the background service before updating"); }
#endif
        try
        {
            if (!enginesStopped) throw new IOException("The backup engine could not stop safely. The update was not installed.");
#if WINDOWS
            if (system is not null) Platform.Windows.SystemService.StopAndWait(system.ServiceName, TimeSpan.FromSeconds(90));
#endif
            using var gate = TryExclusive(plan.Root);
            if (gate is null) throw new IOException("Another Backspace process is still using this app. The update was not installed. Try again after its backup finishes.");
            // Windows' recovery launcher; on Linux and macOS a failed start rolls back below.
            if (OperatingSystem.IsWindows()) WriteRecoveryLauncher(plan);
            WindowsUpdatePackage.Install(plan, _ => { });
            PruneOlderCompletedUpdates(plan);
            installed = true;
            message = "Updated to Backspace " + plan.Version + ". Your settings and backups were preserved.";
        }
        catch (Exception ex) { message = ex.Message; }
        if (plan.State == "Installing")
        {
            WriteResult(plan.Root, "The update could not finish or restore every file. Open \"Recover Backspace\" in " + plan.Work + ". " + message, false);
            return; // Never start a partially replaced bundle.
        }
        WriteResult(plan.Root, message, installed);
#if WINDOWS
        if (system is not null)
            try { Platform.Windows.SystemService.Start(system.ServiceName, TimeSpan.FromSeconds(60)); }
            catch (Exception ex) { AppLogger.For(nameof(WindowsUpdateRuntime)).Warning(ex, "The background service starts with the reopened app instead"); }
#endif
        try { Launch(Path.Combine(plan.Root, plan.ExeName)); }
        catch (Exception ex)
        {
            if (installed)
            {
                try
                {
                    using var gate = TryExclusive(plan.Root);
                    if (gate is null) throw new IOException("Another process started before rollback could begin.");
                    WindowsUpdatePackage.RollBack(plan);
                    WriteResult(plan.Root, "The updated app could not start. The previous version was restored. " + ex.Message, false);
                    Launch(Path.Combine(plan.Root, plan.ExeName));
                }
                catch (Exception rollback) { WriteResult(plan.Root, "Open \"Recover Backspace\" in " + plan.Work + ". " + rollback.Message, false); }
            }
        }
    }

    internal static void Launch(string exe, string[]? args = null, bool separateFromRecoveryConsole = false)
    {
        if (OperatingSystem.IsMacOS() && Directory.Exists(exe))
        {
            // Backspace.app opens through macOS, like a double-click (U245).
            var open = new ProcessStartInfo("open") { UseShellExecute = false };
            open.ArgumentList.Add("-n"); open.ArgumentList.Add(exe);
            if (args is { Length: > 0 }) { open.ArgumentList.Add("--args"); foreach (var arg in args) open.ArgumentList.Add(arg); }
            using var opened = Process.Start(open) ?? throw new IOException("macOS did not open Backspace.");
            opened.WaitForExit(30_000);
            if (opened.HasExited && opened.ExitCode != 0) throw new IOException("macOS could not open the updated Backspace.");
            return;
        }
        // The recovery launcher captures its child's output. Start the
        // restored GUI through the shell so it cannot inherit those pipes
        // and keep the launcher waiting until the user closes the app.
        var start = new ProcessStartInfo(exe) { UseShellExecute = separateFromRecoveryConsole, WorkingDirectory = Path.GetDirectoryName(exe)!, CreateNoWindow = true };
        foreach (var arg in args ?? Array.Empty<string>()) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Windows did not start Backspace.");
    }

    // The current plan retains the entire immediately preceding bundle, so
    // an older completed plan is redundant. Never sweep unknown directories
    // or a journal that may represent an interrupted installation.
    internal static void PruneOlderCompletedUpdates(WindowsUpdatePackage.Plan current, Action<Exception>? onFailure = null)
    {
        try
        {
            WindowsUpdatePackage.ValidateLocation(current);
            if (current.State != "Installed") return;
            foreach (var download in new[] { "download.zip", "download.exe" })
            {
                var archive = Path.Combine(current.Work, download);
                if (File.Exists(archive)) { WindowsUpdatePackage.RejectReparse(archive); File.Delete(archive); }
            }
            foreach (var work in Directory.EnumerateDirectories(Path.GetDirectoryName(current.Work)!))
            {
                if (work.Equals(current.Work, StringComparison.OrdinalIgnoreCase)
                    || !Guid.TryParseExact(Path.GetFileName(work), "N", out _)) continue;
                try
                {
                    var old = ReadPlan(Path.Combine(work, "plan.json"));
                    if (old.ServiceUpdate != current.ServiceUpdate
                        || !old.Root.Equals(current.Root, StringComparison.OrdinalIgnoreCase)
                        || !old.Work.Equals(work, StringComparison.OrdinalIgnoreCase)) continue;
                    if (old.State is not ("Installed" or "RolledBack")) continue;
                    WindowsUpdatePackage.SafeRemoveWork(current.Root, work, serviceUpdate: current.ServiceUpdate, onFailure: onFailure);
                }
                catch (Exception ex) { onFailure?.Invoke(ex); /* Retain unverified work. */ }
            }
        }
        catch (Exception ex) { onFailure?.Invoke(ex); /* Cleanup must never turn a completed update into a failure. */ }
    }

    internal static string CopyRecoveryRunner(WindowsUpdatePackage.Plan plan)
    {
        WindowsUpdatePackage.ValidateLocation(plan);
        var current = Path.Combine(plan.Root, plan.ExeName);
        // Rollback must not move the executable that is running recovery.
        // Keep a separate copy with this update's other recovery data.
        var runner = Path.Combine(plan.Work, "recovery-runner.exe");
        WindowsUpdatePackage.RejectReparse(current);
        if (File.Exists(runner))
        {
            WindowsUpdatePackage.RejectReparse(runner);
            if (!WindowsUpdatePackage.Hash(runner).Equals(WindowsUpdatePackage.Hash(current), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The recovery copy does not match the installed application. The update was not installed.");
        }
        else
        {
            var staging = Path.Combine(plan.Work, "recovery-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.Copy(current, staging);
                File.Move(staging, runner);
            }
            finally
            {
                try { if (File.Exists(staging)) File.Delete(staging); }
                catch { /* An incomplete owned copy is never used for recovery. */ }
            }
        }
        return runner;
    }

    internal static void WriteRecoveryLauncher(WindowsUpdatePackage.Plan plan)
    {
        // Encoded PowerShell keeps Unicode and cmd metacharacters in paths
        // out of the batch parser. No execution-policy change is required.
        static string Literal(string s) => "'" + s.Replace("'", "''") + "'";
        var runner = CopyRecoveryRunner(plan);
        var record = Path.Combine(plan.Work, "plan.json");
        // PowerShell's call operator does not wait for a WinExe. Wait for
        // the actual child to preserve failures as well as successful exits.
        // Windows filenames cannot contain a quote; quote the whole plan
        // path once for ProcessStartInfo's native argument parser.
        // Windows PowerShell's .NET Framework Process.Start still rejects long
        // executable paths. Pass an explicit application name to CreateProcessW
        // instead; inherit this launcher's standard handles and wait for exit.
        const string nativeLauncher = "using System;using System.Text;using System.Runtime.InteropServices;"
            + "public static class BackspaceRecovery{"
            + "[StructLayout(LayoutKind.Sequential)]struct S{public int cb;public IntPtr a,b,c;public int d,e,f,g,h,i,j,flags;public short show,res;public IntPtr reserved,input,output,error;}"
            + "[StructLayout(LayoutKind.Sequential)]struct P{public IntPtr process,thread;public int pid,tid;}"
            + "[DllImport(\"kernel32.dll\",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool CreateProcessW(string app,StringBuilder cmd,IntPtr pa,IntPtr ta,bool inherit,int flags,IntPtr env,string dir,ref S si,out P pi);"
            + "[DllImport(\"kernel32.dll\")]static extern IntPtr GetStdHandle(int n);"
            + "[DllImport(\"kernel32.dll\")]static extern uint WaitForSingleObject(IntPtr h,uint ms);"
            + "[DllImport(\"kernel32.dll\",SetLastError=true)]static extern bool GetExitCodeProcess(IntPtr h,out int code);"
            + "[DllImport(\"kernel32.dll\")]static extern bool CloseHandle(IntPtr h);"
            + "public static int Run(string app,string plan){var s=new S();s.cb=Marshal.SizeOf(s);s.flags=256;s.input=GetStdHandle(-10);s.output=GetStdHandle(-11);s.error=GetStdHandle(-12);P p;"
            + "if(!CreateProcessW(app,new StringBuilder(\"\\\"\"+app+\"\\\" --recover-update \\\"\"+plan+\"\\\"\"),IntPtr.Zero,IntPtr.Zero,true,0x08000000,IntPtr.Zero,null,ref s,out p))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());"
            + "try{if(WaitForSingleObject(p.process,0xffffffff)!=0)throw new Exception(\"Recovery wait failed.\");int code;if(!GetExitCodeProcess(p.process,out code))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());return code;}finally{CloseHandle(p.thread);CloseHandle(p.process);}}}";
        var launcherSource = plan.ServiceUpdate ? nativeLauncher.Replace("--recover-update", "--service-recover-update", StringComparison.Ordinal) : nativeLauncher;
        var command = "$ErrorActionPreference = 'Stop'; try { Add-Type -TypeDefinition " + Literal(launcherSource)
            + "; exit [BackspaceRecovery]::Run(" + Literal(runner) + "," + Literal(record)
            + ") } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));
        var script = "@echo off\r\nsetlocal DisableDelayedExpansion\r\n\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -EncodedCommand "
            + encoded + "\r\nset \"backspaceRecoveryExit=%ERRORLEVEL%\"\r\n"
            + "if not \"%backspaceRecoveryExit%\"==\"0\" pause\r\nexit /b %backspaceRecoveryExit%\r\n";
        // With the update's other recovery data, never beside the app (U218).
        var launcherPath = Path.Combine(plan.Work, "Recover Backspace.cmd");
        File.WriteAllText(launcherPath, script, System.Text.Encoding.ASCII);
    }

    internal static int Recover(string planPath)
    {
        try
        {
            var plan = ReadPlan(planPath);
            if (plan.ServiceUpdate) throw new IOException("This update needs background-service recovery so its registration and file permissions stay consistent.");
            using var gate = TryExclusive(plan.Root) ?? throw new IOException("Close other Backspace processes before recovery.");
            if (plan.State is "Installing" or "Installed") WindowsUpdatePackage.RollBack(plan);
            WriteResult(plan.Root, "The previous application files were restored. Your settings were not changed.", false);
            Launch(Path.Combine(plan.Root, plan.ExeName), separateFromRecoveryConsole: true);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Update recovery could not finish: " + ex.Message);
            return 1;
        }
    }

    internal static WindowsUpdatePackage.Plan ReadPlan(string path)
    {
        var plan = JsonSerializer.Deserialize<WindowsUpdatePackage.Plan>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Missing update record.");
        WindowsUpdatePackage.ValidateLocation(plan);
        if (!Path.GetFullPath(path).Equals(Path.Combine(plan.Work, "plan.json"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update record location does not match.");
        return plan;
    }

    internal static bool RecoverInterruptedBeforeStartup(string root, string[] args)
    {
        var parent = UpdatesFolder(root);
        if (!Directory.Exists(parent)) return false;
        WindowsUpdatePackage.RejectReparse(parent);
        foreach (var work in Directory.EnumerateDirectories(parent))
        {
            if (!Guid.TryParseExact(Path.GetFileName(work), "N", out _)) continue;
            var path = Path.Combine(work, "plan.json");
            if (!File.Exists(path)) continue;
            // Finished and merely downloaded updates need no startup recovery.
            // Their absolute paths may legitimately be stale after the portable
            // application folder moves. Only unfinished replacement records may
            // authorize file changes, and those still receive full validation.
            WindowsUpdatePackage.RejectReparse(work);
            WindowsUpdatePackage.RejectReparse(path);
            using (var record = JsonDocument.Parse(File.ReadAllText(path)))
            {
                if (record.RootElement.TryGetProperty("State", out var state)
                    && state.GetString() is "Prepared" or "Installed" or "RolledBack") continue;
            }
            var plan = ReadPlan(path);
            if (plan.State != "Installing") continue;
            using var gate = TryExclusive(root);
            if (gate is null) return false; // The installer still owns the gate.
            // Read again after acquiring the gate: it may just have committed.
            plan = ReadPlan(path);
            if (plan.State != "Installing") continue;
            WindowsUpdatePackage.RollBack(plan);
            WriteResult(root, "An interrupted update was rolled back. Your settings and backups were preserved.", false);
            Launch(Path.Combine(root, plan.ExeName), args);
            return true;
        }
        return false;
    }

    internal static void WriteResult(string root, string message, bool success)
    {
        try
        {
            var parent = UpdatesFolder(root); Directory.CreateDirectory(parent);
            WindowsUpdatePackage.RejectReparse(parent);
            File.WriteAllText(Path.Combine(parent, "result.json"), JsonSerializer.Serialize(new { message, success, at = DateTime.UtcNow }));
        }
        catch { /* The recovery launcher and previous files remain available. */ }
    }
}
