using System;
using System.Runtime.InteropServices;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// Backspace.exe is a Windows (GUI) program, so text it writes goes nowhere
/// when it is started from Command Prompt or PowerShell: the owner ran
/// <c>Backspace.exe --check local_d</c> and saw nothing (U180, 10 November
/// 2026). For the command-line modes it attaches to the console it was
/// started from, so --run, --check, --prune, --run-all, --version and --help
/// print there. Output that is already redirected (a pipe or a file) is left
/// as it is.
/// <para>A Windows program started from a console has no standard handles
/// at all, and .NET counts a missing handle as redirected: the first version
/// of this check therefore never attached, and 1.1.30 still printed nothing
/// (U180, 11 November 2026). Only a real file or pipe counts as redirected
/// here, and the console (<c>CONOUT$</c>) becomes the standard output.</para>
/// </summary>
internal static class CliConsole
{
    private const int AttachParentProcess = -1;
    private const int StdOutput = -11, StdError = -12;
    private const uint FileTypeDisk = 1, FileTypePipe = 3;

    internal static bool IsCommand(string[] args) => args.Length > 0
        && args[0] is "--run" or "--run-all" or "--check" or "--prune" or "--version" or "-v" or "--help" or "-h" or "/?";

    internal static void AttachIfStartedFromConsole(string[] args)
    {
        if (!OperatingSystem.IsWindows() || !IsCommand(args)) return;
        try
        {
            var outRedirected = Redirected(StdOutput);
            var errorRedirected = Redirected(StdError);
            if (outRedirected && errorRedirected) return;
            if (!AttachConsole(AttachParentProcess)) return;
            // The console as standard output and error: .NET then writes to
            // it as to any console (Unicode included). Nothing has read the
            // standard handles before this point.
            var console = CreateFile("CONOUT$", 0xC0000000 /* GENERIC_READ | GENERIC_WRITE */, 0x3 /* FILE_SHARE_READ | FILE_SHARE_WRITE */,
                IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            if (console == IntPtr.Zero || console == new IntPtr(-1)) return;
            if (!outRedirected) SetStdHandle(StdOutput, console);
            if (!errorRedirected) SetStdHandle(StdError, console);
            // The shell has already shown its prompt: start on a fresh line.
            if (!outRedirected) Console.Out.WriteLine();
        }
        catch (Exception) { /* No console to print to: the command still runs. */ }
    }

    /// <summary>True for output going to a file or a pipe; false for none
    /// (a Windows program started from a console) or a console.</summary>
    internal static bool Redirected(int which)
    {
        var handle = GetStdHandle(which);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
        return GetFileType(handle) is FileTypeDisk or FileTypePipe;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int which, IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
