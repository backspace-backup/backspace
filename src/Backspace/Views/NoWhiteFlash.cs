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
