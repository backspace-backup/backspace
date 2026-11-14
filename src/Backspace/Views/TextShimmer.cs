using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Backspace.Views;

/// <summary>
/// U239 (owner, 13 November 2026: "the accent color moving through the text
/// font color from left to right (in a gradient so it looks like a smooth
/// animation (progress loop), like the text is pulsing)"): while something
/// runs, a band of the accent colour sweeps through the text, then starts
/// again. Its own colour stays at both ends, so it reads as the same word.
/// One shared timer, 25 times a second, only while a shimmering text is on
/// screen; nothing moves when all is quiet.
/// </summary>
public static class TextShimmer
{
    public static readonly AttachedProperty<bool> IsActiveProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, bool>("IsActive", typeof(TextShimmer));

    public static bool GetIsActive(TextBlock text) => text.GetValue(IsActiveProperty);
    public static void SetIsActive(TextBlock text, bool value) => text.SetValue(IsActiveProperty, value);

    private sealed record Sweep(LinearGradientBrush Brush);

    internal static readonly TimeSpan Step = TimeSpan.FromMilliseconds(40);
    /// <summary>One sweep across the word, then a short rest.</summary>
    internal static readonly TimeSpan Cycle = TimeSpan.FromMilliseconds(2000);
    /// <summary>The band's width, as a share of the gradient (twice the word).</summary>
    private const double Band = 0.3;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, object> Hooked = new();
    private static readonly Dictionary<TextBlock, Sweep> Moving = new();
    private static DispatcherTimer? _timer;
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Tests hold the sweep at a given place (0 to 1) for screenshots.</summary>
    internal static double? HeldAt;

    static TextShimmer()
    {
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
        if (!GetIsActive(text) || TopLevel.GetTopLevel(text) is null) { Stop(text); return; }
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
                new GradientStop(baseColor, 0), new GradientStop(baseColor, 0.5 - Band / 2),
                new GradientStop(accent, 0.5), new GradientStop(baseColor, 0.5 + Band / 2), new GradientStop(baseColor, 1),
            },
        };
        Moving[text] = new Sweep(brush);
        Place(Moving[text]);
        text.Foreground = brush;
        _timer ??= new DispatcherTimer(Step, DispatcherPriority.Background, (_, _) => { foreach (var m in Moving.Values) Place(m); });
        _timer.Start();
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
        if (Moving.Count == 0) _timer?.Stop();
    }

    /// <summary>Where the band is now: from just before the word to just
    /// after it over most of the cycle, then a rest off the word.</summary>
    internal static double Position() => HeldAt ?? Math.Min(1, (Clock.Elapsed.TotalMilliseconds % Cycle.TotalMilliseconds) / (Cycle.TotalMilliseconds * 0.8));

    private static void Place(Sweep m)
    {
        // The band's centre travels from just left of the word to just right of it.
        var c = -0.4 + Position() * 1.8;
        m.Brush.StartPoint = new RelativePoint(c - 1, 0.5, RelativeUnit.Relative);
        m.Brush.EndPoint = new RelativePoint(c + 1, 0.5, RelativeUnit.Relative);
    }
}
