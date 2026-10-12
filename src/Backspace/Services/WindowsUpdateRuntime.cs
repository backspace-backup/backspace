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
        try { return new FileStream(Path.Combine(root, GateName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
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
            var probe = Path.Combine(root, ".backspace-write-test-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe)) { }
            File.Delete(probe);
            reason = ""; return true;
        }
        catch { reason = "Backspace cannot write to its folder. Move the portable app to a writable folder or update it manually."; return false; }
    }

    internal static void ApplyPendingAfterExit(bool enginesStopped)
    {
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

    internal static void Launch(string exe, string[]? args = null)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)!, CreateNoWindow = true };
        foreach (var arg in args ?? Array.Empty<string>()) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Windows did not start Backspace.");
    }

    // The current plan retains the entire immediately preceding bundle, so
    // an older completed plan is redundant. Never sweep unknown directories
    // or a journal that may represent an interrupted installation.
    internal static void PruneOlderCompletedUpdates(WindowsUpdatePackage.Plan current)
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
                    if (old.State is not ("Installed" or "RolledBack")) continue;
                    WindowsUpdatePackage.SafeRemoveWork(current.Root, work);
                    if (!Directory.Exists(work))
                    {
                        var launcher = Path.Combine(current.Root, "Backspace-recovery-" + Path.GetFileName(work) + ".cmd");
                        if (File.Exists(launcher)) { WindowsUpdatePackage.RejectReparse(launcher); File.Delete(launcher); }
                    }
                }
                catch { /* Retain anything we cannot establish as an owned completed update. */ }
            }
        }
        catch { /* Cleanup must never turn a completed update into a failure. */ }
    }

    internal static void WriteRecoveryLauncher(WindowsUpdatePackage.Plan plan)
    {
        WindowsUpdatePackage.ValidateLocation(plan);
        // Encoded PowerShell keeps Unicode and cmd metacharacters in paths
        // out of the batch parser. No execution-policy change is required.
        static string Literal(string s) => "'" + s.Replace("'", "''") + "'";
        var previous = Path.Combine(plan.Work, "previous", plan.ExeName);
        var current = Path.Combine(plan.Root, plan.ExeName);
        var record = Path.Combine(plan.Work, "plan.json");
        var command = "$exe = " + Literal(previous) + "; if (-not [IO.File]::Exists($exe)) { $exe = " + Literal(current)
            + " }; & $exe '--recover-update' " + Literal(record) + "; exit $LASTEXITCODE";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));
        var script = "@echo off\r\nsetlocal DisableDelayedExpansion\r\n\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -EncodedCommand "
            + encoded + "\r\nif errorlevel 1 pause\r\n";
        File.WriteAllText(Path.Combine(plan.Root, "Backspace-recovery-" + Path.GetFileName(plan.Work) + ".cmd"), script, System.Text.Encoding.ASCII);
    }

    internal static int Recover(string planPath)
    {
        try
        {
            var plan = ReadPlan(planPath);
            using var gate = TryExclusive(plan.Root) ?? throw new IOException("Close other Backspace processes before recovery.");
            if (plan.State is "Installing" or "Installed") WindowsUpdatePackage.RollBack(plan);
            WriteResult(plan.Root, "The previous application files were restored. Your settings were not changed.", false);
            Launch(Path.Combine(plan.Root, plan.ExeName));
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

    internal static void WriteResult(string root, string message, bool success)
    {
        try
        {
            var parent = Path.Combine(root, ".backspace-updates"); Directory.CreateDirectory(parent);
            WindowsUpdatePackage.RejectReparse(parent);
            File.WriteAllText(Path.Combine(parent, "result.json"), JsonSerializer.Serialize(new { message, success, at = DateTime.UtcNow }));
        }
        catch { /* The recovery launcher and previous files remain available. */ }
    }
}
