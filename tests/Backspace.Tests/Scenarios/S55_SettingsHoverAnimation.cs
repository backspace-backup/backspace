using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

[Collection(ScenarioCollection.Name)]
public sealed class S55_SettingsHoverAnimation : ScenarioBase
{
    protected override string ScenarioName => "S55-settings-hover-animation";
    [AvaloniaFact] public Task Run_S55() => Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        foreach (var (theme, label) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
        {
            var window = ctx.Driver.Show(new Window { Width = 1100, Height = 820,
                RequestedThemeVariant = theme, Content = new SettingsView { DataContext = new SettingsViewModel() } });
            var expander = window.GetVisualDescendants().OfType<Expander>()
                .Single(e => e.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Startup and tray"));
            var header = expander.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "ExpanderHeader");
            var wash = header.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ToggleButtonBackground");
            var glyph = header.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("section-glyph"));
            Assert.NotNull(wash.Transitions);
            Assert.Contains(wash.Transitions!, t => t is BrushTransition);

            window.MouseMove(new Point(3, 3));
            await Frames(15);
            var rest = ColorOf(wash);
            Assert.Equal(0, rest.A);
            if (theme == ThemeVariant.Dark)
                Assert.True(rest.R < 80 && rest.G < 80 && rest.B < 80, "Dark hover must start with a dark transparent hue, not transparent white.");

            // Keep the actual pointer-triggered production transition, but
            // advance its clock explicitly. A busy suite can otherwise spend
            // the whole 180 ms fade between MouseMove and the first sample.
            var clock = new TransitionClock(wash);
            var point = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point); ctx.Driver.Pump();
            Assert.True(header.IsPointerOver);
            var colors = new List<Color>();
            for (var frame = 0; frame < 18; frame++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(16));
                ctx.Driver.Pump();
                var color = ColorOf(wash); colors.Add(color);
                // The fade must change alpha only. Any white-tinted middle
                // frame would violate this even if its final brush is correct.
                Assert.InRange((int)color.R, Math.Max(0, rest.R - 1), Math.Min(255, rest.R + 1));
                Assert.InRange((int)color.G, Math.Max(0, rest.G - 1), Math.Min(255, rest.G + 1));
                Assert.InRange((int)color.B, Math.Max(0, rest.B - 1), Math.Min(255, rest.B + 1));
                if (frame is 1 or 5)
                    ctx.Snapshot(window, $"{label}-hover-frame-{frame + 1}",
                        $"Real pointer-over with the production transition advanced by a controlled clock; brush alpha {color.A}/255. RGB stays on the theme's neutral hue instead of passing through white.");
            }
            Assert.Contains(colors, c => c.A > 0 && c.A < 255); // Prove animation actually ran.
            Assert.Equal(255, colors[^1].A);
            Assert.Equal(8, wash.CornerRadius.TopLeft);
            var washLeft = wash.TranslatePoint(default, window)!.Value.X;
            var glyphLeft = glyph.TranslatePoint(default, window)!.Value.X;
            Assert.True(washLeft <= glyphLeft - 4, "Hover wash must extend visibly to the left of the section icon.");
            var chevron = header.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "ExpandCollapseChevronBorder");
            if (chevron?.Background is ISolidColorBrush chevronBrush) Assert.Equal(0, chevronBrush.Color.A);
            ctx.Snapshot(window, $"{label}-hover-settled", "The final rounded wash extends left of the icon; the chevron has no separate dark block.");

            window.MouseMove(new Point(3, 3));
            ctx.Driver.Pump();
            for (var frame = 0; frame < 18; frame++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(16));
                ctx.Driver.Pump();
            }
            Assert.False(header.IsPointerOver);
            Assert.Equal(0, ColorOf(wash).A);
            ctx.Report.RecordNote(label + " transition samples", "Production transition alpha samples at controlled 16 ms intervals: " + string.Join(", ", colors.Select(c => c.A)) + ". Real pointer events; original duration/easing and strict intermediate-alpha and no-white-flash checks. This tests animation values, not native frame timing.");
            window.Close();
        }

        async Task Frames(int count)
        {
            for (var i = 0; i < count; i++)
            {
                await Task.Delay(16);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                ctx.Driver.Pump();
            }
        }
    }

    private static Color ColorOf(Border border) => Assert.IsAssignableFrom<ISolidColorBrush>(border.Background).Color;

    /// <summary>
    /// Avalonia 12 keeps its per-control clock internal. Reflection is confined
    /// to this test adapter and fails explicitly if that API changes. No shared
    /// clock or application setting is changed, and the production transition
    /// itself is neither replaced nor disabled.
    /// </summary>
    private sealed class TransitionClock
    {
        private readonly object _clock;
        private readonly MethodInfo _pulse;
        private TimeSpan _elapsed;

        public TransitionClock(Animatable target)
        {
            var clockType = typeof(Animatable).Assembly.GetType("Avalonia.Animation.ClockBase", throwOnError: true)!;
            _clock = Activator.CreateInstance(clockType, nonPublic: true)!;
            _pulse = clockType.GetMethod("Pulse", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.NotNull(_pulse);
            var field = typeof(Animatable).GetField("ClockProperty", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(field);
            var property = Assert.IsAssignableFrom<AvaloniaProperty>(field.GetValue(null));
            target.SetValue(property, _clock);
            Advance(TimeSpan.Zero);
        }

        public void Advance(TimeSpan interval)
        {
            _elapsed += interval;
            _pulse.Invoke(_clock, [_elapsed]);
        }
    }
}
