using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace Backspace.Views;

/// <summary>
/// U240 (owner, 13 November 2026: "clicking More > Edit shows a white flash
/// before showing window (dark theme)"). Windows shows a new window before
/// Backspace has drawn it, as a white rectangle for a moment. Every window is
/// hidden from view (DWM "cloaked") while it is laid out, and shown once its
/// first frame is drawn, already painted.
/// </summary>
internal static class NoWhiteFlash
{
    private const int DwmwaCloak = 13;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> Seen = new();

    /// <summary>Hooks every window, once, for the process.</summary>
    internal static void Install()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Raised as Show begins, before Windows shows the window.
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => Cloak(window));
    }

    private static void Cloak(Window window)
    {
        if (!Seen.TryAdd(window, new object()) || window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;
        if (!Set(hwnd, true)) return;
        var shown = false;
        void Show()
        {
            if (shown) return;
            shown = true;
            Set(hwnd, false);
            // U241, U254: the taskbar button could come up with the generic
            // icon, seen after updates (1.1.40, 1.1.52), when the first start of
            // the new version keeps the window busy as Windows asks for its icon.
            // Setting Avalonia's Icon again did not help (the same icon handle is
            // not sent again). Windows takes a new handle of the same icon at
            // once (tried on the owner's window, 15 November 2026), so the icon
            // is handed over again as copies: now, and twice more once the start
            // has settled.
            RefreshTaskbarIcon(hwnd);
            Avalonia.Threading.DispatcherTimer.RunOnce(() => RefreshTaskbarIcon(hwnd), TimeSpan.FromSeconds(3));
            Avalonia.Threading.DispatcherTimer.RunOnce(() => RefreshTaskbarIcon(hwnd), TimeSpan.FromSeconds(20));
        }
        window.Opened += (_, _) =>
        {
            // Two frames: the first lays the window out, the second has drawn it.
            // A cloaked window still renders (only Windows hides it), so these
            // frames come as they would for any open window.
            window.RequestAnimationFrame(_ => window.RequestAnimationFrame(_ => Show()));
        };
        window.Closed += (_, _) => shown = true;
    }

    private const uint WmGetIcon = 0x7F, WmSetIcon = 0x80;

    /// <summary>Sends the window's own icons again, as new handles, so the
    /// taskbar redraws its button with them. The earlier copies are freed.</summary>
    internal static void RefreshTaskbarIcon(IntPtr hwnd)
    {
        try
        {
            foreach (var size in new[] { (IntPtr)1, (IntPtr)0 }) // ICON_BIG, ICON_SMALL
            {
                var current = SendMessage(hwnd, WmGetIcon, size, IntPtr.Zero);
                if (current == IntPtr.Zero) continue;
                var copy = CopyIcon(current);
                if (copy == IntPtr.Zero) continue;
                var previous = SendMessage(hwnd, WmSetIcon, size, copy);
                // Only a copy made here is freed; Avalonia owns its own handles.
                if (previous != IntPtr.Zero && Copies.Remove(previous)) DestroyIcon(previous);
                Copies.Add(copy);
            }
        }
        catch (Exception) { /* the icon is cosmetic */ }
    }

    private static readonly System.Collections.Generic.HashSet<IntPtr> Copies = new();

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr CopyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);

    private static bool Set(IntPtr hwnd, bool cloaked)
    {
        try
        {
            var value = cloaked ? 1 : 0;
            return DwmSetWindowAttribute(hwnd, DwmwaCloak, ref value, sizeof(int)) == 0;
        }
        catch (Exception) { return false; }
    }
}
