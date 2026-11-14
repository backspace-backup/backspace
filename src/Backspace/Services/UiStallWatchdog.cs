using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Threading;

namespace Backspace.Services;

/// <summary>
/// U247 (owner, 13 November 2026: "it froze after relaunching with 1.1.42
/// update; after 20-30s it showed up normally. Something must have held it"):
/// a hang that clears by itself leaves nothing to look at. This watches the
/// window's thread from outside. When it hasn't answered for
/// <see cref="StallAfter"/>, the app log says so, and once per run a small
/// diagnostic snapshot of the threads (a .NET "triage" dump, no file contents
/// or memory of the backups) is saved in Backspace.config\logs\app, to see
/// what held the window. It is replaced by the next one, never piled up.
/// </summary>
internal static class UiStallWatchdog
{
    internal static TimeSpan StallAfter = TimeSpan.FromSeconds(8);
    private static long _lastBeat = Environment.TickCount64;
    private static int _started;
    private static bool _dumped;

    internal static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        var thread = new Thread(Watch) { IsBackground = true, Name = "UI stall watchdog", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    private static void Watch()
    {
        var reported = false;
        while (true)
        {
            // Asks the window's thread to note the time; a blocked thread can't.
            Dispatcher.UIThread.Post(() => Interlocked.Exchange(ref _lastBeat, Environment.TickCount64), DispatcherPriority.Send);
            Thread.Sleep(1000);
            var silent = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastBeat));
            if (silent < StallAfter)
            {
                if (reported) AppLogger.For(nameof(UiStallWatchdog)).Warning("The window is responding again");
                reported = false;
                continue;
            }
            if (reported) continue;
            reported = true;
            AppLogger.For(nameof(UiStallWatchdog)).Warning("The window has not responded for {Seconds:F0} s", silent.TotalSeconds);
            if (!_dumped) { _dumped = true; TrySnapshot(); }
        }
    }

    /// <summary>The runtime's own createdump, beside the .NET runtime in use.</summary>
    private static void TrySnapshot()
    {
        try
        {
            var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            var tool = Path.Combine(runtime, OperatingSystem.IsWindows() ? "createdump.exe" : "createdump");
            if (!File.Exists(tool)) { AppLogger.For(nameof(UiStallWatchdog)).Information("No thread snapshot: this .NET has no createdump"); return; }
            var folder = AppPaths.AppLogsDir;
            Directory.CreateDirectory(folder);
            foreach (var old in Directory.EnumerateFiles(folder, "window-stall-*.dmp")) { try { File.Delete(old); } catch (IOException) { } }
            var path = Path.Combine(folder, $"window-stall-{DateTime.Now:yyyyMMdd-HHmmss}.dmp");
            var start = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "--triage", "-f", path, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process is null) return;
            _ = process.StandardOutput.ReadToEndAsync(); _ = process.StandardError.ReadToEndAsync();
            process.WaitForExit(60_000);
            AppLogger.For(nameof(UiStallWatchdog)).Warning("Saved a thread snapshot of the stall: {Path}", path);
        }
        catch (Exception ex) { AppLogger.For(nameof(UiStallWatchdog)).Information(ex, "No thread snapshot of the stall"); }
    }
}
