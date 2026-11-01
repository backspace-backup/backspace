using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Backspace.Views;

/// <summary>Keeps a resizable window inside its monitor's usable area, in logical pixels.</summary>
internal static class WindowPlacement
{
    internal readonly record struct Placement(Size ClientSize, Size MinimumSize, PixelPoint Position);
    internal sealed record MonitorGeometry(PixelRect Bounds, PixelRect WorkingArea, double Scaling);

    internal static void Attach(Window window) =>
        Attach(window, w => (w.Screens.ScreenFromWindow(w) ?? w.Screens.Primary) is { } screen
            ? new MonitorGeometry(screen.Bounds, screen.WorkingArea, screen.Scaling) : null);

    // The screen lookup is injectable so tests can drive the real window events
    // with two monitor geometries without changing the desktop's display setup.
    internal static void Attach(Window window, Func<Window, MonitorGeometry?> screenForWindow)
    {
        var minimum = new Size(window.MinWidth, window.MinHeight);
        MonitorGeometry? fittedMonitor = null;
        var fitting = false;
        var closed = false;
        void Fit(object? sender, EventArgs args)
        {
            if (closed || fitting || !window.IsVisible || window.WindowState != WindowState.Normal) return;
            var screen = screenForWindow(window);
            if (screen is null) return;
            fitting = true;
            try
            {
                fittedMonitor = new MonitorGeometry(screen.Bounds, screen.WorkingArea, screen.Scaling);
                var frame = window.FrameSize ?? window.ClientSize;
                Apply(window, screen.WorkingArea, screen.Scaling,
                    new Size(Math.Max(0, frame.Width - window.ClientSize.Width),
                        Math.Max(0, frame.Height - window.ClientSize.Height)), minimum);
            }
            finally { fitting = false; }
        }
        void OnPositionChanged(object? sender, PixelPointEventArgs args)
        {
            if (closed || fitting || fittedMonitor is null || !window.IsVisible || window.WindowState != WindowState.Normal) return;
            var screen = screenForWindow(window);
            if (screen is null) return;
            // Moving within the current monitor is the user's choice. Only
            // crossing to a different monitor/work area warrants another fit.
            // In particular, this must not pull a dragged window back on-screen
            // for every position update or trigger a recursive move loop.
            if (fittedMonitor != new MonitorGeometry(screen.Bounds, screen.WorkingArea, screen.Scaling))
                Fit(sender, args);
        }
        void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property != Window.WindowStateProperty || window.WindowState != WindowState.Normal) return;
            // A display may have changed while the window was maximized or
            // minimized. Wait until its restored native bounds have settled.
            Dispatcher.UIThread.Post(() => Fit(window, EventArgs.Empty), DispatcherPriority.Loaded);
        }
        window.Opened += Fit;
        window.ScalingChanged += Fit;
        window.Screens.Changed += Fit;
        window.PositionChanged += OnPositionChanged;
        window.PropertyChanged += OnPropertyChanged;
        window.Closed += (_, _) =>
        {
            closed = true;
            window.Opened -= Fit;
            window.ScalingChanged -= Fit;
            window.Screens.Changed -= Fit;
            window.PositionChanged -= OnPositionChanged;
            window.PropertyChanged -= OnPropertyChanged;
        };
    }

    internal static void Apply(Window window, PixelRect workingArea, double scaling,
        Size frameInsets, Size? requestedMinimum = null)
    {
        // Startup position/scaling events can precede the first layout. ClientSize
        // may still be the platform's default; using it would overwrite an explicit
        // editor or main-window size even on a spacious display. Width/Height
        // are client dimensions and Avalonia updates them after user resizes.
        var requested = new Size(
            double.IsFinite(window.Width) && window.Width > 0 ? window.Width : window.ClientSize.Width,
            double.IsFinite(window.Height) && window.Height > 0 ? window.Height : window.ClientSize.Height);
        var placement = Calculate(workingArea, scaling, requested,
            requestedMinimum ?? new Size(window.MinWidth, window.MinHeight), frameInsets, window.Position);
        // Lower the native minimum first: otherwise the OS can reject a smaller
        // size and leave Save/Cancel below a scaled laptop's taskbar.
        window.MinWidth = placement.MinimumSize.Width;
        window.MinHeight = placement.MinimumSize.Height;
        window.Width = placement.ClientSize.Width;
        window.Height = placement.ClientSize.Height;
        window.Position = placement.Position;
    }

    internal static Placement Calculate(PixelRect workingArea, double scaling, Size requested,
        Size minimum, Size frameInsets, PixelPoint position)
    {
        if (!double.IsFinite(scaling) || scaling <= 0) throw new ArgumentOutOfRangeException(nameof(scaling));
        // Work areas already exclude the taskbar. A little breathing room keeps
        // the resize border away from it without assuming a taskbar height.
        const double edge = 8;
        var available = new Size(
            Math.Max(1, Math.Floor(workingArea.Width / scaling - edge * 2 - frameInsets.Width)),
            Math.Max(1, Math.Floor(workingArea.Height / scaling - edge * 2 - frameInsets.Height)));
        var safeMinimum = new Size(Math.Min(minimum.Width, available.Width), Math.Min(minimum.Height, available.Height));
        var client = new Size(
            Math.Clamp(requested.Width, safeMinimum.Width, available.Width),
            Math.Clamp(requested.Height, safeMinimum.Height, available.Height));
        var outerWidth = (int)Math.Ceiling((client.Width + frameInsets.Width) * scaling);
        var outerHeight = (int)Math.Ceiling((client.Height + frameInsets.Height) * scaling);
        var margin = (int)Math.Floor(edge * scaling);
        var left = workingArea.X + margin;
        var top = workingArea.Y + margin;
        var right = Math.Max(left, workingArea.Right - margin - outerWidth);
        var bottom = Math.Max(top, workingArea.Bottom - margin - outerHeight);
        return new Placement(client, safeMinimum,
            new PixelPoint(Math.Clamp(position.X, left, right), Math.Clamp(position.Y, top, bottom)));
    }
}
