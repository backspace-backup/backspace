using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Backspace.Views;

/// <summary>
/// The running state's motion on a backup card (U114, 4 November 2026): the
/// status icon turns and the dots after "Running" cycle. Both move in steps
/// from one shared timer, eight times a second, and only while a running card
/// is on screen. A continuous animation redraws at screen rate, which kept the
/// GPU at 10-12% while a backup ran (U109).
/// </summary>
public static class RunningPulse
{
    public static readonly AttachedProperty<bool> IsActiveProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsActive", typeof(RunningPulse));

    public static bool GetIsActive(Control control) => control.GetValue(IsActiveProperty);
    public static void SetIsActive(Control control, bool value) => control.SetValue(IsActiveProperty, value);

    internal static readonly TimeSpan Step = TimeSpan.FromMilliseconds(125);
    internal const double DegreesPerStep = 30;
    // Starts on "..." so a card that has just started reads like "Running…".
    internal static readonly string[] Dots = ["...", ".", ".."];

    // Weak, so cards rebuilt on every settings change aren't kept alive.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> Hooked = new();
    private static readonly HashSet<Control> Moving = new();
    private static DispatcherTimer? _timer;
    private static int _tick;

    /// <summary>True while at least one control is moving.</summary>
    internal static bool IsTicking => _timer?.IsEnabled == true;

    /// <summary>Tests hold the motion still for screenshots and step it with <see cref="Advance"/>.</summary>
    internal static bool Paused;

    static RunningPulse()
    {
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
        if (GetIsActive(control) && (attached ?? TopLevel.GetTopLevel(control) is not null))
        {
            Moving.Add(control);
            Apply(control);
        }
        else
        {
            Moving.Remove(control);
            if (control is not TextBlock) control.RenderTransform = null;
        }
        if (Moving.Count == 0) { _timer?.Stop(); return; }
        _timer ??= new DispatcherTimer(Step, DispatcherPriority.Background, (_, _) => { if (!Paused) Advance(); });
        _timer.Start();
    }

    /// <summary>Back to the first step, so later screenshots match their baselines.</summary>
    internal static void Restart()
    {
        _tick = 0;
        foreach (var control in Moving) Apply(control);
    }

    /// <summary>One step of every moving control; the timer calls it.</summary>
    internal static void Advance()
    {
        _tick++;
        foreach (var control in Moving) Apply(control);
    }

    private static void Apply(Control control)
    {
        if (control is TextBlock text)
            text.Text = Dots[_tick / 4 % Dots.Length];
        else
            control.RenderTransform = new RotateTransform(_tick * DegreesPerStep % 360);
    }
}
