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
        // U254 (owner, 16 November 2026: "ensure the icon is baked in so it'll
        // never be late"): the window's class carries Backspace's icon before
        // Windows shows it, so the taskbar button never needs to ask the window.
        BakeIcon(hwnd);
        if (!Set(hwnd, true)) return;
        var shown = false;
        void Show()
        {
            if (shown) return;
            shown = true;
            Set(hwnd, false);
            // U241, U254: the taskbar button of a window shown hidden (cloaked)
            // could keep the generic icon (after updates: 1.1.40, 1.1.52,
            // 1.1.53 for half a second). Windows redraws it with a new handle
            // of the same icon (tried on the owner's window, 15 November 2026).
            RefreshTaskbarIcon(hwnd);
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
    private const int GclpHIcon = -14, GclpHIconSm = -34;
    private static IntPtr _bigIcon, _smallIcon;

    /// <summary>Gives the window, and its window class, Backspace's icon from
    /// Backspace.exe itself, before Windows shows the window: the taskbar
    /// reads a class icon without asking the window, so a window still busy
    /// starting up (or shown hidden) never shows the generic icon. The
    /// window's own icon (the accent colour) still replaces it as usual.</summary>
    internal static void BakeIcon(IntPtr hwnd)
    {
        try
        {
            if (_bigIcon == IntPtr.Zero && Environment.ProcessPath is { } exe)
                ExtractIconEx(exe, 0, out _bigIcon, out _smallIcon, 1);
            if (_bigIcon == IntPtr.Zero) return;
            SetClassLongPtr(hwnd, GclpHIcon, _bigIcon);
            SetClassLongPtr(hwnd, GclpHIconSm, _smallIcon != IntPtr.Zero ? _smallIcon : _bigIcon);
            if (SendMessage(hwnd, WmGetIcon, (IntPtr)1, IntPtr.Zero) == IntPtr.Zero) SendMessage(hwnd, WmSetIcon, (IntPtr)1, _bigIcon);
            if (SendMessage(hwnd, WmGetIcon, IntPtr.Zero, IntPtr.Zero) == IntPtr.Zero && _smallIcon != IntPtr.Zero) SendMessage(hwnd, WmSetIcon, IntPtr.Zero, _smallIcon);
        }
        catch (Exception) { /* the icon is cosmetic */ }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")] private static extern IntPtr SetClassLongPtr(IntPtr hwnd, int index, IntPtr value);

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
