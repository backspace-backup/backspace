using System;
using System.IO;
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
/// </summary>
internal static class CliConsole
{
    private const int AttachParentProcess = -1;

    internal static bool IsCommand(string[] args) => args.Length > 0
        && args[0] is "--run" or "--run-all" or "--check" or "--prune" or "--version" or "-v" or "--help" or "-h" or "/?";

    internal static void AttachIfStartedFromConsole(string[] args)
    {
        if (!OperatingSystem.IsWindows() || !IsCommand(args)) return;
        try
        {
            if (Console.IsOutputRedirected && Console.IsErrorRedirected) return;
            if (!AttachConsole(AttachParentProcess)) return;
            if (!Console.IsOutputRedirected)
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            if (!Console.IsErrorRedirected)
                Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
            // The shell has already shown its prompt: start on a fresh line.
            Console.Out.WriteLine();
        }
        catch (Exception) { /* No console to print to: the command still runs. */ }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
