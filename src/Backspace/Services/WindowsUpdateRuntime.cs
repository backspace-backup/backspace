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
    internal const string GateName = ".backspace-runtime.lock";

    internal static FileStream? AcquireShared(string root, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            try { return new FileStream(Path.Combine(root, GateName), FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read); }
            catch (UnauthorizedAccessException) { return null; } // Read-only installs cannot self-update.
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(100); }
        } while (DateTime.UtcNow < deadline);
        throw new IOException("Backspace is being updated. Please try opening it again in a moment.");
    }

    internal static FileStream? TryExclusive(string root)
    {
        // Exclusivity comes from FileShare.None. No lock-file content is ever
        // written, including after service removal restores a read-only gate.
        try { return new FileStream(Path.Combine(root, GateName), FileMode.OpenOrCreate, FileAccess.Read, FileShare.None); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static bool CanInstall(out string reason)
    {
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
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
#if WINDOWS
            if (Platform.Windows.ServiceProgramUpdateDesktop.Applies(root)) { reason = ""; return true; }
#endif
            var probe = Path.Combine(root, ".backspace-write-test-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe)) { }
            File.Delete(probe);
            reason = ""; return true;
        }
        catch { reason = "Backspace cannot write to its folder. Move the portable app to a writable folder or update it manually."; return false; }
    }

    internal static void ApplyPendingAfterExit(bool enginesStopped)
    {
#if WINDOWS
        if (Platform.Windows.ServiceProgramUpdateDesktop.Pending is { } serviceUpdate)
        {
            Platform.Windows.ServiceProgramUpdateDesktop.Pending = null;
            Pending = null;
            Platform.Windows.ServiceProgramUpdateDesktop.ApplyAfterExit(serviceUpdate, enginesStopped);
            return;
        }
#endif
        var plan = Pending; Pending = null;
        if (plan is null) return;
        bool installed = false;
        string message;
        try
        {
            if (!enginesStopped) throw new IOException("The backup engine could not stop safely. The update was not installed.");
            using var gate = TryExclusive(plan.Root);
            if (gate is null) throw new IOException("Another Backspace process is still using this app. The update was not installed. Try again after its backup finishes.");
            WriteRecoveryLauncher(plan);
            WindowsUpdatePackage.Install(plan, _ => { });
            PruneOlderCompletedUpdates(plan);
            installed = true;
            message = "Updated to Backspace " + plan.Version + ". Your settings and backups were preserved.";
        }
        catch (Exception ex) { message = ex.Message; }
        if (plan.State == "Installing")
        {
            WriteResult(plan.Root, "The update could not finish or restore every file. Open the Backspace recovery launcher in this folder. " + message, false);
            return; // Never start a partially replaced bundle.
        }
        WriteResult(plan.Root, message, installed);
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
                catch (Exception rollback) { WriteResult(plan.Root, "Open the Backspace recovery launcher in this folder. " + rollback.Message, false); }
            }
        }
    }

    internal static void Launch(string exe, string[]? args = null, bool separateFromRecoveryConsole = false)
    {
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
            var archive = Path.Combine(current.Work, "download.zip");
            if (File.Exists(archive)) { WindowsUpdatePackage.RejectReparse(archive); File.Delete(archive); }
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
                    if (!current.ServiceUpdate && !Directory.Exists(work))
                    {
                        var launcher = Path.Combine(current.Root, "Backspace-recovery-" + Path.GetFileName(work) + ".cmd");
                        if (File.Exists(launcher)) { WindowsUpdatePackage.RejectReparse(launcher); File.Delete(launcher); }
                    }
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
        var launcherPath = plan.ServiceUpdate ? Path.Combine(plan.Work, "Recover Backspace.cmd")
            : Path.Combine(plan.Root, "Backspace-recovery-" + Path.GetFileName(plan.Work) + ".cmd");
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
        var parent = Path.Combine(root, ".backspace-updates");
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

    internal static void WriteResult(string root, string message, bool success, bool portableData = false)
    {
        try
        {
            var parent = Path.Combine(portableData ? Path.Combine(root, AppPaths.AppFolderName) : root, ".backspace-updates"); Directory.CreateDirectory(parent);
            WindowsUpdatePackage.RejectReparse(parent);
            File.WriteAllText(Path.Combine(parent, "result.json"), JsonSerializer.Serialize(new { message, success, at = DateTime.UtcNow }));
        }
        catch { /* The recovery launcher and previous files remain available. */ }
    }
}
