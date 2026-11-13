using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// Tells when this Windows session is unlocked or reconnected (U224, owner:
/// "I want this to be purely event based"): alerts that waited while the
/// screen was locked are shown then, instead of looking every 30 seconds.
/// Windows sends the news to a hidden message window on its own thread.
/// </summary>
internal static class SessionUnlockWatcher
{
    private static readonly object Gate = new();
    private static Thread? _thread;
    private static WndProc? _proc; // kept alive while Windows may call it
    private static event Action? UnlockedCore;

    /// <summary>Raised on that window's thread; handlers must be quick.</summary>
    internal static event Action Unlocked
    {
        add { Start(); UnlockedCore += value; }
        remove => UnlockedCore -= value;
    }

    /// <summary>Tests: as if the session had been unlocked.</summary>
    internal static void Raise() { try { UnlockedCore?.Invoke(); } catch { /* a listener can't stop the watcher */ } }

    private static void Start()
    {
        lock (Gate)
        {
            if (_thread is not null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "Backspace session watcher" };
            _thread.Start();
        }
    }

    private static void Run()
    {
        try
        {
            _proc = Proc;
            var instance = GetModuleHandle(null);
            var cls = new WNDCLASSEX { cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = _proc, hInstance = instance, lpszClassName = "BackspaceSessionWatcher" };
            if (RegisterClassEx(ref cls) == 0) return;
            var window = CreateWindowEx(0, cls.lpszClassName, "", 0, 0, 0, 0, 0, new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, instance, IntPtr.Zero);
            if (window == IntPtr.Zero || !WTSRegisterSessionNotification(window, 0 /* NOTIFY_FOR_THIS_SESSION */)) return;
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception ex) { AppLogger.For(nameof(SessionUnlockWatcher)).Warning(ex, "Session changes can't be followed; alerts wait for the next run's news"); }
    }

    private static IntPtr Proc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        // WM_WTSSESSION_CHANGE: console or remote connect, logon, unlock.
        if (message == 0x02B1 && wParam.ToInt32() is 1 or 3 or 5 or 8) Raise();
        return DefWindowProc(window, message, wParam, lParam);
    }

    private delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize; public uint style; public WndProc lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
        public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG message);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSRegisterSessionNotification(IntPtr window, int flags);
}
