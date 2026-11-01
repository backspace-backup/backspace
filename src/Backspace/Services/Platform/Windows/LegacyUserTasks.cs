using System;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.TaskScheduler;
using Serilog;

namespace Backspace.Services.Platform.Windows;

/// <summary>Versions before U043 (31 October 2026) registered one Task
/// Scheduler entry per backup, check and prune, running as the signed-in
/// person. The tray app now keeps the schedule, so they are removed on start;
/// the person's own tasks need no administrator approval. The remaining
/// helpers recognise those tasks exactly (see <see cref="ServiceLegacyTasks"/>).</summary>
[SupportedOSPlatform("windows")]
internal static class LegacyUserTasks
{
    internal const string FolderPath = @"\MyTasks\Backspace";
    private static ILogger _log => AppLogger.For(nameof(LegacyUserTasks));

    /// <summary>Removes this person's earlier Backspace tasks: in Backspace's
    /// own folder, written by Backspace, named "Backspace — …" and running as
    /// the current user. Returns how many were removed.</summary>
    internal static int RemoveForCurrentUser()
    {
        using var scheduler = new TaskService();
        using var folder = scheduler.GetFolder(FolderPath);
        if (folder is null) return 0;
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var removed = 0;
        foreach (var task in folder.Tasks)
        {
            using (task)
            {
                using var definition = task.Definition;
                if (!task.Name.StartsWith("Backspace — ", StringComparison.Ordinal)
                    || definition.RegistrationInfo.Author != "Backspace"
                    || definition.Principal.LogonType is not (TaskLogonType.InteractiveToken or TaskLogonType.S4U)
                    || !SameWindowsUser(definition.Principal.UserId, user)) continue;
                try
                {
                    folder.DeleteTask(task.Name, exceptionOnNotExists: false);
                    removed++;
                }
                catch (Exception ex) { _log.Warning(ex, "Could not remove the earlier Windows task {Task}", task.Name); }
            }
        }
        if (removed > 0) _log.Information("Removed {Count} earlier Windows task(s); Backspace now keeps the schedule itself", removed);
        try { if (folder.Tasks.Count == 0 && folder.SubFolders.Count == 0) scheduler.GetFolder(@"\MyTasks")?.DeleteFolder("Backspace", exceptionOnNotExists: false); }
        catch (Exception ex) { _log.Debug(ex, "The empty task folder was left in place"); }
        return removed;
    }

    internal static bool SameWindowsUser(string actual, string expected)
    {
        // Windows can return the account as either a name or a SID.
        static string Sid(string value) => value.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
            ? value : new System.Security.Principal.NTAccount(value)
                .Translate(typeof(System.Security.Principal.SecurityIdentifier)).Value;
        try { return string.Equals(Sid(actual), Sid(expected), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>The action earlier versions registered: Windows PowerShell
    /// starts the main executable, or the missing-executable helper. Kept only
    /// to recognise those tasks; nothing registers it any more.</summary>
    internal static ExecAction BuildExecAction(string mainExe, string stubExe, string backupName,
        ScheduledTaskKind kind = ScheduledTaskKind.Backup)
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var powershell = System.IO.Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return new ExecAction(powershell,
            BuildPowerShellWrapperArguments(mainExe, stubExe, backupName, kind), systemDirectory);
    }

    internal static string BuildPowerShellWrapperArguments(string mainExe, string stubExe, string backupName,
        ScheduledTaskKind kind = ScheduledTaskKind.Backup)
    {
        var flag = kind switch
        {
            ScheduledTaskKind.Prune => "--prune",
            ScheduledTaskKind.Check => "--check",
            _ => "--run",
        };
        var childArguments = flag + " " + QuoteWindowsArgument(backupName);
        var script = """
            $ErrorActionPreference = 'Stop'
            try {
                $main = MAIN_LITERAL
                $stub = STUB_LITERAL
                $info = New-Object System.Diagnostics.ProcessStartInfo
                $info.UseShellExecute = $false
                if ([System.IO.File]::Exists($main)) {
                    $info.FileName = $main
                    $info.Arguments = ARGS_LITERAL
                } else {
                    $info.FileName = $stub
                }
                $info.WorkingDirectory = [System.IO.Path]::GetDirectoryName($info.FileName)
                $child = [System.Diagnostics.Process]::Start($info)
                try { $child.WaitForExit(); $code = $child.ExitCode }
                finally { $child.Dispose() }
                exit $code
            } catch {
                [Console]::Error.WriteLine($_.Exception.Message)
                exit 1
            }
            """;
        // Replace placeholders in one pass, exactly as the registered bytes were made.
        script = System.Text.RegularExpressions.Regex.Replace(script, "MAIN_LITERAL|STUB_LITERAL|ARGS_LITERAL",
            match => PowerShellLiteral(match.Value switch
            {
                "MAIN_LITERAL" => mainExe,
                "STUB_LITERAL" => stubExe,
                _ => childArguments,
            }));
        return "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " +
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    private static string PowerShellLiteral(string value) => "'" + value.Replace("'", "''") + "'";

    internal static string QuoteWindowsArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c);
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }
}
