using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Backspace.Services.Platform;
using Backspace.Services.Platform.Windows;
using Xunit;

namespace Backspace.Tests.Services;

// Historical filename retained; the broken cmd wrapper was replaced by a
// literal-data PowerShell launcher. Actual executable proofs are local reports.
public class TaskSchedulerCmdWrapperTests
{
    internal static string DecodeScript(string arguments)
    {
        const string prefix = "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand ";
        Assert.StartsWith(prefix, arguments);
        return Encoding.Unicode.GetString(Convert.FromBase64String(arguments[prefix.Length..]));
    }

    [Theory]
    [InlineData(ScheduledTaskKind.Backup, "--run")]
    [InlineData(ScheduledTaskKind.Prune, "--prune")]
    [InlineData(ScheduledTaskKind.Check, "--check")]
    public void Action_uses_stock_shell_waits_and_preserves_exit_code(ScheduledTaskKind kind, string flag)
    {
        using var action = WindowsTaskScheduler.BuildExecAction(
            @"C:\missing old folder\Backspace.exe", @"C:\stub folder\stub.exe", "my backup", kind);
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        Assert.Equal(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"), action.Path);
        Assert.Equal(system, action.WorkingDirectory);
        var script = DecodeScript(action.Arguments);
        Assert.Contains("$info.Arguments = '" + flag + " \"my backup\"'", script);
        Assert.Contains("if ([System.IO.File]::Exists($main))", script);
        Assert.Contains("$info.FileName = $stub", script);
        Assert.Contains("$child.WaitForExit(); $code = $child.ExitCode", script);
        Assert.Contains("exit $code", script);
        Assert.Contains("exit 1", script);
        Assert.DoesNotContain("ExecutionPolicy", action.Arguments);
    }

    [Fact]
    public void Paths_and_backup_names_remain_literal_including_placeholder_and_script_text()
    {
        var script = DecodeScript(WindowsTaskScheduler.BuildPowerShellWrapperArguments(
            @"C:\O'Brien $HOME; & %PATH%\MAIN_LITERAL.exe",
            @"C:\STUB_LITERAL folder\stub.exe", "ARGS_LITERAL'; exit 99; # $HOME ` & %PATH% !"));
        Assert.Contains("$main = 'C:\\O''Brien $HOME; & %PATH%\\MAIN_LITERAL.exe'", script);
        Assert.Contains("$stub = 'C:\\STUB_LITERAL folder\\stub.exe'", script);
        Assert.Contains("$info.Arguments = '--run \"ARGS_LITERAL''; exit 99; # $HOME ` & %PATH% !\"'", script);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ordinary")]
    [InlineData("a backup with spaces")]
    [InlineData("-h")]
    [InlineData("O'Brien & %PATH% ! ; $(exit) ` 雪")]
    [InlineData("quote\"inside")]
    [InlineData("slash\\\"quote")]
    [InlineData("ends in slash\\")]
    [InlineData("two slashes\\\\\"and quote")]
    public void Native_windows_argument_parser_round_trips_exact_data(string name)
    {
        var command = "fixture.exe --run " + WindowsTaskScheduler.QuoteWindowsArgument(name);
        var pointer = CommandLineToArgvW(command, out var count);
        Assert.NotEqual(IntPtr.Zero, pointer);
        try
        {
            Assert.Equal(3, count);
            Assert.Equal(name, Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, 2 * IntPtr.Size)));
        }
        finally { LocalFree(pointer); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr pointer);
}
