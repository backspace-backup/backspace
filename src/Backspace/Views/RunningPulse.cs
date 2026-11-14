using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Backspace.Views;

/// <summary>
/// The running state's motion (U114): the status icon turns while a backup or
/// check runs. Since U239 (owner, 14 November 2026: "I like the smooth
/// rotating icon ... please update the other places where the same icon is
/// used so it is always smooth") its angle follows the clock, one turn every
/// 1.5 s, placed by the window's shared <see cref="FrameLoop"/> at even
/// intervals (how often depends on how the window is drawn: see there), and
/// only while a turning icon is on screen.
/// </summary>
public static class RunningPulse
{
    public static readonly AttachedProperty<bool> IsActiveProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsActive", typeof(RunningPulse));

    public static bool GetIsActive(Control control) => control.GetValue(IsActiveProperty);
    public static void SetIsActive(Control control, bool value) => control.SetValue(IsActiveProperty, value);

    /// <summary>One full turn.</summary>
    internal static readonly TimeSpan Turn = TimeSpan.FromMilliseconds(1500);
    /// <summary>The test clock's step (<see cref="Advance"/>): a twelfth of a turn.</summary>
    internal const double DegreesPerStep = 30;

    // Weak, so cards rebuilt on every settings change aren't kept alive.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> Hooked = new();
    private static readonly HashSet<Control> Moving = new();
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>True while at least one icon turns.</summary>
    internal static bool IsTicking => Moving.Count > 0;

    /// <summary>Tests hold the motion still for screenshots and step it with <see cref="Advance"/>.</summary>
    internal static bool Paused;
    private static double _heldDegrees;

    static RunningPulse()
    {
        FrameLoop.Register(
            top => { foreach (var c in Moving) if (TopLevel.GetTopLevel(c) == top) return true; return false; },
            top => { foreach (var c in Moving) if (TopLevel.GetTopLevel(c) == top) Apply(c); });
        IsActiveProperty.Changed.AddClassHandler<Control>((control, _) =>
        {
            if (Hooked.TryAdd(control, Hooked))
            {
                control.AttachedToVisualTree += (_, _) => Refresh(control, attached: true);
                control.DetachedFromVisualTree += (_, _) => Refresh(control, attached: false);
            }
            Refresh(control);
        });
    }

    private static void Refresh(Control control, bool? attached = null)
    {
        if (GetIsActive(control) && (attached ?? true) && TopLevel.GetTopLevel(control) is { } top)
        {
            Moving.Add(control);
            Apply(control);
            FrameLoop.Ensure(top);
        }
        else
        {
            Moving.Remove(control);
            control.RenderTransform = null;
        }
    }

    /// <summary>Back to the start of a turn, so later screenshots match their baselines.</summary>
    internal static void Restart()
    {
        _heldDegrees = 0;
        foreach (var control in Moving) Apply(control);
    }

    /// <summary>Tests: one twelfth of a turn for every turning icon.</summary>
    internal static void Advance()
    {
        _heldDegrees = (_heldDegrees + DegreesPerStep) % 360;
        foreach (var control in Moving) Apply(control);
    }

    private static double Angle() => Paused
        ? _heldDegrees
        : Clock.Elapsed.TotalMilliseconds % Turn.TotalMilliseconds / Turn.TotalMilliseconds * 360;

    private static void Apply(Control control)
    {
        var angle = Angle();
        if (control.RenderTransform is RotateTransform turning) turning.Angle = angle;
        else control.RenderTransform = new RotateTransform(angle);
    }
}
