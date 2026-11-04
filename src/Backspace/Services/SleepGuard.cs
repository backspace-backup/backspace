using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Backspace.Services;

/// <summary>
/// Keeps Windows from going to sleep on its own while a backup runs (U132,
/// 4 November 2026: the PC went into standby, then down, during a backup in
/// the night). It asks only that the system stay on; the screen still turns
/// off, and closing the lid, pressing the power button or a critically low
/// battery still put it to sleep, as the person decides. It can't and
/// doesn't hold back a restart (Windows Update's included): a backup cut
/// short that way resumes afterwards without sending again what it stored.
/// </summary>
internal static class SleepGuard
{
    private static readonly object Gate = new();
    private static int _holders;
    private static IntPtr _request;

    /// <summary>Held until disposed; several backups share one request.</summary>
    internal static IDisposable Hold()
    {
        lock (Gate)
        {
            if (_holders++ == 0 && OperatingSystem.IsWindows()) Set();
        }
        return new Release();
    }

    /// <summary>True while a request is held (for tests).</summary>
    internal static bool Holding { get { lock (Gate) return _holders > 0; } }

    private sealed class Release : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) return;
            lock (Gate)
            {
                if (--_holders == 0 && OperatingSystem.IsWindows()) Clear();
            }
        }
    }

    private static void Set()
    {
        try
        {
            var reason = new ReasonContext { Version = 0, Flags = SimpleString, Reason = "Backspace is backing up your files" };
            _request = PowerCreateRequest(ref reason);
            if (_request == InvalidHandle || _request == IntPtr.Zero) { _request = IntPtr.Zero; return; }
            if (!PowerSetRequest(_request, SystemRequired)) { CloseHandle(_request); _request = IntPtr.Zero; }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _request = IntPtr.Zero;
        }
    }

    private static void Clear()
    {
        if (_request == IntPtr.Zero) return;
        PowerClearRequest(_request, SystemRequired);
        CloseHandle(_request);
        _request = IntPtr.Zero;
    }

    private const uint SimpleString = 0x1;
    private const int SystemRequired = 1;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string Reason;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerSetRequest(IntPtr request, int type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerClearRequest(IntPtr request, int type);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
