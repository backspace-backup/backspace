#if WINDOWS
using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Controls;
using Backspace.Services;
using Serilog;

namespace Backspace.Views;

/// <summary>
/// Frames in step with the screen (U239, owner 15 November 2026: "not smooth
/// at all ... normally it should not stutter for such a basic thing").
///
/// When drawing on the processor, Avalonia 12.1 on Windows times its frames
/// with a sleeping thread (SleepLoopRenderTimer, 60 a second) that knows
/// nothing of the screen's refresh, so frames reach the screen at uneven
/// moments (measured on a 165 Hz screen: 6 to 40 ms apart, sometimes 90 ms)
/// and motion stutters. Avalonia's graphics card surfaces wait for the
/// screen's refresh instead (DxgiConnection). Avalonia does not let an
/// application supply its own timer, so this one paces Avalonia's: a thread
/// waits for each refresh of the screen showing the main window
/// (D3DKMTWaitForVerticalBlankEvent) and wakes the timer's thread then
/// (its _wakeEvent); the timer itself is slowed to 24 frames a second, which
/// only matters if the refreshes stop coming. Reached through reflection; if
/// anything is not as expected, Avalonia's timer runs as before. If waiting
/// for the refresh fails, it waits for the desktop's next composition
/// (DwmFlush). To remove once Avalonia times processor frames by the screen.
/// BACKSPACE_SLEEP_FRAMES=1 leaves Avalonia's timer alone, for comparison.
/// </summary>
internal static class DisplayClock
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    /// <summary>Avalonia's own pace while no refresh comes.</summary>
    internal const int FallbackFps = 24;
    private static readonly ILogger Log = AppLogger.For(nameof(DisplayClock));
    private static bool _tried;
    private static IntPtr _window;

    /// <summary>True when frames follow the screen's refresh.</summary>
    internal static bool IsActive { get; private set; }

    /// <summary>Paces Avalonia's render timer by the screen, once.</summary>
    internal static void Install(TopLevel top)
    {
        if (_tried) return;
        _tried = true;
        if (Environment.GetEnvironmentVariable("BACKSPACE_SLEEP_FRAMES") == "1") return;
        // Only a real Windows window: the test harness draws without one, and
        // its frames must stay its own.
        if (top.TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } handle || handle.Handle == IntPtr.Zero) { _tried = false; return; }
        try
        {
            var renderer = typeof(TopLevel).GetProperty("Renderer", Any)?.GetValue(top) ?? throw new InvalidOperationException("no renderer");
            var compositor = Field(renderer, "_compositor") ?? throw new InvalidOperationException("no compositor");
            var loop = compositor.GetType().GetProperty("Loop", Any)?.GetValue(compositor) ?? throw new InvalidOperationException("no render loop");
            if (Field(loop, "_timer") is not Avalonia.Rendering.SleepLoopRenderTimer timer)
                throw new InvalidOperationException("the render timer is not SleepLoopRenderTimer");
            var wake = Field(timer, "_wakeEvent") as AutoResetEvent ?? throw new InvalidOperationException("no _wakeEvent");
            var stopped = typeof(Avalonia.Rendering.SleepLoopRenderTimer).GetField("_stopped", Any) ?? throw new InvalidOperationException("no _stopped");
            if (stopped.FieldType != typeof(bool)) throw new InvalidOperationException("_stopped is not a bool");
            _window = top.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            (typeof(Avalonia.Rendering.SleepLoopRenderTimer).GetProperty("DesiredFps", Any) ?? throw new InvalidOperationException("no DesiredFps")).SetValue(timer, FallbackFps);
            new Thread(() => Run(timer, wake, stopped)) { IsBackground = true, Name = "Display clock", Priority = ThreadPriority.AboveNormal }.Start();
            IsActive = true;
            Log.Information("Frames now follow the screen's refresh");
        }
        catch (Exception ex)
        {
            Log.Warning("Frames keep Avalonia's own timing ({Why})", ex.Message);
        }
    }

    private static object? Field(object o, string name)
    {
        for (var t = o.GetType(); t != null; t = t.BaseType)
            if (t.GetField(name, Any | BindingFlags.DeclaredOnly) is { } f) return f.GetValue(o);
        return null;
    }

    private static void Run(Avalonia.Rendering.SleepLoopRenderTimer timer, AutoResetEvent wake, FieldInfo stopped)
    {
        var waiter = new BlankWaiter();
        var sinceScreenCheck = Stopwatch.StartNew();
        waiter.Follow(_window);
        var idle = 0;
        while (true)
        {
            // Avalonia pauses its timer between frames when nothing waits to be
            // drawn, often for a moment only. Keep following the refreshes
            // through such pauses, so the next frame still lands on one; after
            // half a second with nothing to draw, look only ten times a second
            // (Avalonia's thread draws the first frame at once when needed).
            if ((bool)stopped.GetValue(timer)!)
            {
                if (++idle > 50) { Thread.Sleep(100); continue; }
            }
            else idle = 0;
            // Follow the window to another screen.
            if (sinceScreenCheck.Elapsed > TimeSpan.FromSeconds(2)) { waiter.Follow(_window); sinceScreenCheck.Restart(); }
            // A fast screen (120 Hz and more) gets a frame on every second or
            // fourth refresh: 60 to 110 a second, always on a refresh.
            if (waiter.Wait() % waiter.Every == 0 && !(bool)stopped.GetValue(timer)!) wake.Set();
        }
    }

    /// <summary>Waits for the next refresh of one screen.</summary>
    private sealed class BlankWaiter
    {
        private IntPtr _monitor;
        private uint _adapter;
        private uint _source;
        private bool _open;
        private bool _blankFailed;
        private readonly Stopwatch _since = Stopwatch.StartNew();

        /// <summary>Opens the screen showing the window; called every 2 s, so
        /// waiting for the refresh is also tried again after it failed (a
        /// locked or sleeping screen refuses it).</summary>
        public void Follow(IntPtr window)
        {
            _blankFailed = false;
            _quick = 0;
            var monitor = window != IntPtr.Zero ? MonitorFromWindow(window, 2) : MonitorFromWindow(IntPtr.Zero, 1);
            if (monitor == _monitor && _open) return;
            Close();
            _monitor = monitor;
            Every = 1; _gapSum = 0; _gaps = 0; _count = 0;
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(monitor, ref info)) return;
            var dc = CreateDC(info.szDevice, null, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) return;
            try
            {
                var open = new D3DKMT_OPENADAPTERFROMHDC { hDc = dc };
                if (D3DKMTOpenAdapterFromHdc(ref open) != 0) return;
                _adapter = open.hAdapter;
                _source = open.VidPnSourceId;
                _open = true;
            }
            finally { DeleteDC(dc); }
        }

        /// <summary>Refreshes per frame: every refresh below 120 Hz, every
        /// second on 120-165 Hz, every fourth on 240 Hz (60 to 110 frames a second;
        /// 1.1.52 drew only 48 a second on a 96 Hz screen).</summary>
        public int Every { get; private set; } = 1;
        private long _count;
        private double _gapSum;
        private int _gaps;
        private TimeSpan _lastBlank;

        /// <summary>Waits for the next refresh; returns a running count of them.</summary>
        public long Wait()
        {
            var started = _since.Elapsed;
            if (_open && !_blankFailed)
            {
                var wait = new D3DKMT_WAITFORVERTICALBLANKEVENT { hAdapter = _adapter, VidPnSourceId = _source };
                if (D3DKMTWaitForVerticalBlankEvent(ref wait) != 0) _blankFailed = true;
                // A wait that keeps returning at once is not waiting.
                else if (_since.Elapsed - started > TimeSpan.FromMilliseconds(0.5))
                {
                    _quick = 0;
                    var now = _since.Elapsed;
                    var gap = (now - _lastBlank).TotalMilliseconds;
                    _lastBlank = now;
                    // Learn the refresh interval from consecutive refreshes.
                    if (gap is > 2 and < 40 && _gaps < 60)
                    {
                        _gapSum += gap; _gaps++;
                        if (_gaps == 60)
                        {
                            Every = Math.Max(1, (int)Math.Floor(17.0 / (_gapSum / _gaps)));
                            Log.Information("Screen refresh {Hz:F0} Hz: a frame every {Every} refreshes", 1000 / (_gapSum / _gaps), Every);
                        }
                    }
                    return ++_count;
                }
                else if (++_quick > 20) _blankFailed = true;
                else return ++_count;
                if (!_warned) { _warned = true; Log.Information("The screen's refresh is not available now (locked or asleep?); frames follow the desktop's composition until it is"); }
            }
            started = _since.Elapsed;
            // The desktop composes nothing while the screen is locked or off.
            if (DwmFlush() == 0 && _since.Elapsed - started > TimeSpan.FromMilliseconds(0.5)) return _count = 0;
            Thread.Sleep(16);
            return _count = 0;
        }

        private int _quick;
        private bool _warned;

        private void Close()
        {
            if (!_open) return;
            var close = new D3DKMT_CLOSEADAPTER { hAdapter = _adapter };
            D3DKMTCloseAdapter(ref close);
            _open = false;
        }
    }

    // LUID is two 32-bit halves (4-byte aligned), not one 64-bit field.
    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_OPENADAPTERFROMHDC { public IntPtr hDc; public uint hAdapter; public uint LuidLow; public int LuidHigh; public uint VidPnSourceId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_WAITFORVERTICALBLANKEVENT { public uint hAdapter; public uint hDevice; public uint VidPnSourceId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize; public int l, t, r, b; public int wl, wt, wr, wb; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("gdi32.dll")] private static extern int D3DKMTOpenAdapterFromHdc(ref D3DKMT_OPENADAPTERFROMHDC data);
    [DllImport("gdi32.dll")] private static extern int D3DKMTWaitForVerticalBlankEvent(ref D3DKMT_WAITFORVERTICALBLANKEVENT data);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER data);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr init);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
#endif
