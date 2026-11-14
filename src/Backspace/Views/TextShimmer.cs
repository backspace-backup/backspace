using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Backspace.Views;

/// <summary>
/// U239 (owner, 13 November 2026: "the accent color moving through the text
/// font color from left to right (in a gradient so it looks like a smooth
/// animation (progress loop), like the text is pulsing)"): while something
/// runs, a band of the accent colour sweeps through the text, then starts
/// again. Its own colour stays at both ends, so it reads as the same word.
/// It moves with the window's shared frame clock (<see cref="FrameLoop"/>), eased
/// in and out, and only while a shimmering text is on screen; nothing moves
/// when all is quiet.
/// </summary>
public static class TextShimmer
{
    public static readonly AttachedProperty<bool> IsActiveProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, bool>("IsActive", typeof(TextShimmer));

    public static bool GetIsActive(TextBlock text) => text.GetValue(IsActiveProperty);
    public static void SetIsActive(TextBlock text, bool value) => text.SetValue(IsActiveProperty, value);

    private sealed record Gradient(LinearGradientBrush Brush);

    /// <summary>One sweep across the word, then a short rest (owner, 14 November
    /// 2026: less time between two sweeps, a slower, calmer sweep). Sweep and
    /// rest last 3 s, two turns of the running icon, so they keep in step.</summary>
    internal static readonly TimeSpan Sweep = TimeSpan.FromMilliseconds(2850);
    internal static readonly TimeSpan Rest = TimeSpan.FromMilliseconds(150);
    /// <summary>The band's width, as a share of the gradient (twice the word).</summary>
    private const double Band = 0.3;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, object> Hooked = new();
    private static readonly Dictionary<TextBlock, Gradient> Moving = new();
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Tests hold the sweep at a given place (0 to 1) for screenshots.</summary>
    internal static double? HeldAt;

    static TextShimmer()
    {
        FrameLoop.Register(
            top => { foreach (var t in Moving.Keys) if (TopLevel.GetTopLevel(t) == top) return true; return false; },
            top => { foreach (var (t, m) in Moving) if (TopLevel.GetTopLevel(t) == top) Place(m); });
        IsActiveProperty.Changed.AddClassHandler<TextBlock>((text, _) =>
        {
            if (Hooked.TryAdd(text, Hooked))
            {
                text.AttachedToVisualTree += (_, _) => Refresh(text);
                text.DetachedFromVisualTree += (_, _) => Stop(text);
                text.ActualThemeVariantChanged += (_, _) => Restart(text);
                // Its own colour comes from the item around it (selected or not).
                text.AttachedToVisualTree += (_, _) =>
                {
                    if (text.FindAncestorOfType<Avalonia.Controls.Primitives.TemplatedControl>() is { } owner)
                        owner.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty) Restart(text); };
                };
            }
            Refresh(text);
        });
    }

    private static void Refresh(TextBlock text)
    {
        if (!GetIsActive(text) || TopLevel.GetTopLevel(text) is not { } top) { Stop(text); return; }
        if (Moving.ContainsKey(text)) return;
        var baseColor = (text.Foreground as ISolidColorBrush)?.Color
            ?? (text.FindAncestorOfType<Avalonia.Controls.Primitives.TemplatedControl>()?.Foreground as ISolidColorBrush)?.Color ?? Colors.Gray;
        var accent = text.TryFindResource("BS.Brush.Accent", text.ActualThemeVariant, out var a) && a is ISolidColorBrush b ? b.Color : Colors.DodgerBlue;
        // Already in the accent colour (the selected page): the band is the
        // ordinary text colour instead, so it still shows.
        if (Math.Abs(baseColor.R - accent.R) + Math.Abs(baseColor.G - accent.G) + Math.Abs(baseColor.B - accent.B) < 60
            && text.TryFindResource("BS.Brush.FgPrimary", text.ActualThemeVariant, out var p) && p is ISolidColorBrush primary)
            accent = primary.Color;
        // The gradient spans twice the word, its accent band in the middle;
        // moving the whole gradient sweeps the band, and outside it the
        // word keeps its own colour.
        var brush = new LinearGradientBrush
        {
            GradientStops =
            {
                // Half-way stops round the band, so it fades in and out softly.
                new GradientStop(baseColor, 0), new GradientStop(baseColor, 0.5 - Band / 2),
                new GradientStop(Mix(baseColor, accent, 0.5), 0.5 - Band / 4),
                new GradientStop(accent, 0.5),
                new GradientStop(Mix(baseColor, accent, 0.5), 0.5 + Band / 4),
                new GradientStop(baseColor, 0.5 + Band / 2), new GradientStop(baseColor, 1),
            },
        };
        Moving[text] = new Gradient(brush);
        Place(Moving[text]);
        text.Foreground = brush;
        FrameLoop.Ensure(top);
    }

    private static void Restart(TextBlock text)
    {
        if (!Moving.ContainsKey(text)) return;
        Stop(text); Refresh(text);
    }

    private static void Stop(TextBlock text)
    {
        if (!Moving.Remove(text)) return;
        text.ClearValue(TextBlock.ForegroundProperty);
    }

    /// <summary>Where the band is now: from just before the word to just
    /// after it over most of the cycle, easing in and out, then a rest off
    /// the word.</summary>
    internal static double Position()
    {
        if (HeldAt is { } held) return held;
        var cycle = Sweep.TotalMilliseconds + Rest.TotalMilliseconds;
        var t = Math.Min(1, Clock.Elapsed.TotalMilliseconds % cycle / Sweep.TotalMilliseconds);
        // Half eased, half steady: it glides in and out without lingering off the word.
        return 0.5 * t + 0.5 * t * t * (3 - 2 * t);
    }

    private static Color Mix(Color a, Color b, double share) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * share), (byte)(a.R + (b.R - a.R) * share),
        (byte)(a.G + (b.G - a.G) * share), (byte)(a.B + (b.B - a.B) * share));

    private static void Place(Gradient m)
    {
        // The band's centre travels from just left of the word to just right of
        // it: the band is 0.6 of the word wide, so it starts and ends just off it.
        var c = -0.3 + Position() * 1.6;
        m.Brush.StartPoint = new RelativePoint(c - 1, 0.5, RelativeUnit.Relative);
        m.Brush.EndPoint = new RelativePoint(c + 1, 0.5, RelativeUnit.Relative);
    }
}
