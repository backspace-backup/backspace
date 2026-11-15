#if WINDOWS
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Backspace.Services;
using Serilog;

namespace Backspace.Views;

/// <summary>
/// Drawing on the processor without redrawing the whole window (U239, owner
/// 15 November 2026: "a very small number of pixels on screen ... Why would
/// it take so much CPU?").
///
/// Avalonia 12.1's Windows processor surface (Avalonia.Win32
/// FramebufferManager) keeps one bitmap for the window, but tells the
/// compositor it does not keep the previous frame. So for every frame,
/// however little changed, the compositor redraws what changed into a layer,
/// clears the whole bitmap, copies the whole layer into it, and the surface
/// then copies the whole bitmap to the window: three full-window copies (11
/// MB each in a 2088x1342 window) per frame.
///
/// Avalonia already supports surfaces that keep the previous frame (no
/// platform declares it yet). This gives the window such a surface: its own
/// bitmap, kept between frames, so the compositor draws only the changed
/// rectangles into it, and only those are copied to the window. Installed
/// through reflection, because Avalonia does not let an application supply
/// the surface; every step is checked, and if anything is not as expected
/// (another Avalonia version, drawing on the graphics card), the window keeps
/// Avalonia's own surface. To remove once Avalonia's Windows surface keeps
/// its frames and copies only what changed. BACKSPACE_FULL_FRAMES=1 turns it
/// off, for comparison.
/// </summary>
internal static class RetainedFrames
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly ILogger Log = AppLogger.For(nameof(RetainedFrames));
    private static readonly ConditionalWeakTable<object, Surface> Installed = new();
    private static readonly ConditionalWeakTable<TopLevel, object> Targets = new();
    private static bool _disabled = Environment.GetEnvironmentVariable("BACKSPACE_FULL_FRAMES") == "1";

    /// <summary>Frames drawn and pixels sent to the window since the last
    /// <see cref="TakeStats"/>, for measurements and tests.</summary>
    internal static long Frames, FullFrames, PixelsSent;

    internal static (long Frames, long FullFrames, long PixelsSent) TakeStats()
    {
        var r = (Frames, FullFrames, PixelsSent);
        Frames = FullFrames = PixelsSent = 0;
        return r;
    }

    /// <summary>True once the window draws through a kept frame.</summary>
    internal static bool IsActive(TopLevel top) => Target(top) is { } t && RenderTargetOf(t) is { } rt
        && Field(rt, "_renderTarget")?.GetValue(rt) is { } inner && Installed.TryGetValue(inner, out _);

    /// <summary>Gives the window a kept frame, if it draws on the processor
    /// and its render target exists. Cheap when already done.</summary>
    internal static void Ensure(TopLevel top)
    {
        if (_disabled) return;
        try
        {
            if (Target(top) is not { } target || RenderTargetOf(target) is not { } rt) return;
            // The processor render target (Avalonia.Skia FramebufferRenderTarget).
            if (rt.GetType().Name != "FramebufferRenderTarget") return;
            var innerField = Field(rt, "_renderTarget");
            if (innerField?.GetValue(rt) is not IFramebufferRenderTarget inner) return;
            if (Installed.TryGetValue(inner, out _)) return;
            if (inner.GetType() != typeof(FuncFramebufferRenderTarget)) return;
            if (top.TryGetPlatformHandle() is not { } handle || handle.Handle == IntPtr.Zero) return;
            if (Surface.Create(handle.Handle, target) is not { } surface) { Disable("the compositor has changed"); return; }
            var replacement = (IFramebufferRenderTarget)Activator.CreateInstance(typeof(FuncFramebufferRenderTarget), LockDelegate(surface), true)!;
            Installed.Add(replacement, surface);
            innerField.SetValue(rt, replacement);
            inner.Dispose();
            Log.Information("The window now keeps its frame and sends only what changed to Windows");
        }
        catch (Exception ex) { Disable(ex.Message); }
    }

    /// <summary>Once the window has drawn its first frames (its render
    /// target is made on the render thread when it first draws).</summary>
    internal static void EnsureSoon(TopLevel top)
    {
        if (_disabled) return;
        var tries = 0;
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            Ensure(top);
            if (_disabled || IsActive(top) || ++tries >= 20) timer.Stop();
        };
        timer.Start();
        // Measurements (U239): frames, full frames and pixels sent, every 2 s.
        if (Environment.GetEnvironmentVariable("BACKSPACE_FRAME_STATS") == "1")
        {
            var stats = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            stats.Tick += (_, _) =>
            {
                var (frames, full, pixels) = TakeStats();
                Log.Information("Frames: {Frames} ({Full} full), {Pixels:N0} pixels sent, kept frame {Active}", frames, full, pixels, IsActive(top));
            };
            stats.Start();
        }
    }

    /// <summary>Avalonia's LockFramebufferDelegate, calling the surface:
    /// (sceneInfo, out properties) => { var fb = surface.Lock(); properties =
    /// new(surface.Kept); return fb; }. Its parameter types are not public,
    /// so it is built at run time.</summary>
    private static Delegate LockDelegate(Surface surface)
    {
        var type = typeof(FuncFramebufferRenderTarget).GetNestedType("LockFramebufferDelegate", Any)
            ?? throw new InvalidOperationException("no LockFramebufferDelegate");
        var parameters = type.GetMethod("Invoke")!.GetParameters();
        var sceneInfo = Expression.Parameter(parameters[0].ParameterType, "sceneInfo");
        var properties = Expression.Parameter(parameters[1].ParameterType, "properties");
        var propertiesType = parameters[1].ParameterType.GetElementType()!;
        var create = propertiesType.GetConstructor([typeof(bool)]) ?? throw new InvalidOperationException("no FramebufferLockProperties(bool)");
        var self = Expression.Constant(surface);
        var framebuffer = Expression.Variable(typeof(ILockedFramebuffer), "framebuffer");
        var body = Expression.Block([framebuffer],
            Expression.Assign(framebuffer, Expression.Call(self, typeof(Surface).GetMethod(nameof(Surface.Lock))!)),
            Expression.Assign(properties, Expression.New(create, Expression.Field(self, nameof(Surface.Kept)))),
            framebuffer);
        return Expression.Lambda(type, body, sceneInfo, properties).Compile();
    }

    private static void Disable(string why)
    {
        _disabled = true;
        Log.Warning("Kept frames are not available ({Why}); the window redraws as Avalonia does", why);
    }

    /// <summary>The window's compositor target (Avalonia's ServerCompositionTarget).</summary>
    private static object? Target(TopLevel top)
    {
        if (Targets.TryGetValue(top, out var known)) return known;
        var renderer = Property(top, "Renderer")?.GetValue(top);
        var client = renderer is null ? null : Property(renderer, "CompositionTarget")?.GetValue(renderer);
        var server = client is null ? null : Property(client, "Server")?.GetValue(client);
        if (server is not null) Targets.AddOrUpdate(top, server);
        return server;
    }

    private static object? RenderTargetOf(object target) => Field(target, "_renderTarget")?.GetValue(target);

    /// <summary>The most derived property of that name (a derived class
    /// can hide its base's, as CompositionTarget.Server does).</summary>
    private static PropertyInfo? Property(object o, string name)
    {
        for (var t = o.GetType(); t is not null; t = t.BaseType)
            if (t.GetProperty(name, Any | BindingFlags.DeclaredOnly) is { } p) return p;
        return null;
    }

    private static FieldInfo? Field(object o, string name)
    {
        for (var t = o.GetType(); t is not null; t = t.BaseType)
            if (t.GetField(name, Any) is { } f) return f;
        return null;
    }

    /// <summary>The window's bitmap, kept between frames.</summary>
    internal sealed class Surface
    {
        private readonly IntPtr _hwnd;
        private readonly object _target;
        private readonly FieldInfo _fullRedraw;
        private readonly object _tracker;
        // Avalonia's dirty-rectangle trackers: SingleDirtyRectTracker keeps
        // one rectangle (_rect); MultiDirtyRectTracker (with regions) a few
        // (_regions._dirtyRegions, or the whole window when it gave up).
        // Read before drawing, while they hold this frame's changes.
        private readonly FieldInfo? _single;
        private readonly object? _regions;
        private readonly FieldInfo? _regionArray, _wholeWindow;
        private readonly object _overlays;
        private readonly PropertyInfo _overlaysNeedLayer;
        private IntPtr _bits;
        private PixelSize _size;

        /// <summary>The previous frame is still in the bitmap.</summary>
        public bool Kept;

        private Surface(IntPtr hwnd, object target, FieldInfo fullRedraw, object tracker, FieldInfo? single,
            object? regions, FieldInfo? regionArray, FieldInfo? wholeWindow, object overlays, PropertyInfo overlaysNeedLayer)
        {
            _hwnd = hwnd; _target = target; _fullRedraw = fullRedraw; _tracker = tracker; _single = single;
            _regions = regions; _regionArray = regionArray; _wholeWindow = wholeWindow;
            _overlays = overlays; _overlaysNeedLayer = overlaysNeedLayer;
        }

        /// <summary>Null when the compositor is not as expected.</summary>
        public static Surface? Create(IntPtr hwnd, object target)
        {
            var fullRedraw = Field(target, "_fullRedrawRequested");
            var tracker = Property(target, "DirtyRects")?.GetValue(target);
            var overlays = Field(target, "_overlays")?.GetValue(target);
            var needLayer = overlays is null ? null : Property(overlays, "RequireLayer");
            if (fullRedraw is null || tracker is null || overlays is null || needLayer is null) return null;
            switch (tracker.GetType().Name)
            {
                case "SingleDirtyRectTracker":
                    return Field(tracker, "_rect") is { } single
                        ? new Surface(hwnd, target, fullRedraw, tracker, single, null, null, null, overlays, needLayer) : null;
                case "MultiDirtyRectTracker":
                    var regions = Field(tracker, "_regions")?.GetValue(tracker);
                    if (regions is null || Field(regions, "_dirtyRegions") is not { } array
                        || Field(regions, "_maxSurfaceFallback") is not { } whole) return null;
                    return new Surface(hwnd, target, fullRedraw, tracker, null, regions, array, whole, overlays, needLayer);
                default: return null;
            }
        }

        ~Surface()
        {
            if (_bits != IntPtr.Zero) Marshal.FreeHGlobal(_bits);
        }

        /// <summary>On the render thread, after the compositor has collected
        /// this frame's changes and before it draws them.</summary>
        public ILockedFramebuffer Lock()
        {
            GetClientRect(_hwnd, out var rc);
            var size = new PixelSize(Math.Max(1, rc.Right - rc.Left), Math.Max(1, rc.Bottom - rc.Top));
            var kept = _bits != IntPtr.Zero && size == _size;
            if (!kept)
            {
                if (_bits != IntPtr.Zero) Marshal.FreeHGlobal(_bits);
                _bits = Marshal.AllocHGlobal(size.Width * size.Height * 4);
                _size = size;
            }
            Kept = kept;

            // What the compositor is about to redraw: only that is sent.
            var window = new PixelRect(0, 0, size.Width, size.Height);
            var areas = new List<PixelRect>();
            var full = !kept || (bool)_fullRedraw.GetValue(_target)! || (bool)_overlaysNeedLayer.GetValue(_overlays)!;
            if (!full && _single is not null)
            {
                if (_single.GetValue(_tracker) is { } rect && Pixels(rect, window) is { Width: > 0, Height: > 0 } area) areas.Add(area);
            }
            else if (!full && _regions is not null)
            {
                if ((bool)_wholeWindow!.GetValue(_regions)!) full = true;
                else if (_regionArray!.GetValue(_regions) is Array rects)
                    foreach (var rect in rects)
                        if (rect is not null && Pixels(rect, window) is { Width: > 0, Height: > 0 } area) areas.Add(area);
            }
            if (full) { areas.Clear(); areas.Add(window); }
            var bits = _bits;
            var dpi = GetDpiForWindow(_hwnd);
            return new LockedFramebuffer(bits, size, size.Width * 4, new Vector(dpi, dpi), PixelFormat.Bgra8888, AlphaFormat.Premul,
                () => Send(bits, size, areas, full));
        }

        /// <summary>A dirty rectangle (Avalonia's LtrbRect, in pixels), two
        /// pixels larger for anti-aliasing (the compositor draws one more).</summary>
        private static PixelRect Pixels(object rect, PixelRect window)
        {
            var t = rect.GetType();
            double Edge(string n) => Convert.ToDouble(t.GetField(n, Any)?.GetValue(rect) ?? t.GetProperty(n, Any)!.GetValue(rect));
            if (Edge("Right") <= Edge("Left") || Edge("Bottom") <= Edge("Top")) return default;
            var left = (int)Math.Floor(Edge("Left")) - 2; var top = (int)Math.Floor(Edge("Top")) - 2;
            var right = (int)Math.Ceiling(Edge("Right")) + 2; var bottom = (int)Math.Ceiling(Edge("Bottom")) + 2;
            return right <= left || bottom <= top ? default : new PixelRect(left, top, right - left, bottom - top).Intersect(window);
        }

        private void Send(IntPtr bits, PixelSize size, List<PixelRect> areas, bool full)
        {
            Frames++;
            if (full) FullFrames++;
            if (areas.Count == 0) return;
            var dc = GetDC(_hwnd);
            if (dc == IntPtr.Zero) return;
            try
            {
                var header = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = size.Width, biHeight = -size.Height,
                    biPlanes = 1, biBitCount = 32,
                };
                foreach (var area in areas)
                {
                    PixelsSent += (long)area.Width * area.Height;
                    SetDIBitsToDevice(dc, area.X, area.Y, (uint)area.Width, (uint)area.Height,
                        area.X, area.Y, 0, (uint)size.Height, bits, ref header, 0);
                }
            }
            finally { ReleaseDC(_hwnd, dc); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes; public ushort biBitCount;
        public uint biCompression; public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public uint biClrUsed; public uint biClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")]
    private static extern int SetDIBitsToDevice(IntPtr hdc, int xDest, int yDest, uint w, uint h, int xSrc, int ySrc,
        uint startScan, uint cLines, IntPtr bits, ref BITMAPINFOHEADER bmi, uint colorUse);
}
#endif
