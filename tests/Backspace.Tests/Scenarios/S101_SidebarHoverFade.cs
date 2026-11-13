using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// U194 (11 November 2026): hovering a sidebar item flashed. The row faded
/// its hover wash in from Transparent (#00FFFFFF), so on the dark sidebar
/// the fade passed through whitish tones. It now rests on the hover hue at
/// alpha 0, so every frame of the fade has the hover colour and only its
/// opacity changes. Sampled frame by frame in both themes.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S101_SidebarHoverFade : ScenarioBase
{
    protected override string ScenarioName => "S101-sidebar-hover-fade";

    [AvaloniaFact] public async Task Run_S101() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        using var main = new MainWindowViewModel();
        var host = ctx.Driver.Show(new MainWindow { Width = 1100, Height = 820, DataContext = main });
        await ctx.Driver.PumpAsync(200);
        // One hover first, unmeasured: the first animation of a fresh window
        // pays for loading and compiling, and on a slow machine (Linux CI)
        // that alone outlasted the fade.
        {
            var warm = host.GetVisualDescendants().OfType<RadioButton>()
                .Single(r => r.Classes.Contains("nav") && r.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Destinations"));
            host.MouseMove(warm.TranslatePoint(new Point(warm.Bounds.Width / 2, warm.Bounds.Height / 2), host)!.Value);
            for (var i = 0; i < 40; i++) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); ctx.Driver.Pump(); await Task.Delay(10); }
            host.MouseMove(new Point(600, 700));
            await Settle(ctx);
        }
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            host.RequestedThemeVariant = theme;
            Screenshots.ScreenshotDiffTests.SettleInitialTransitions(host);
            host.MouseMove(new Point(600, 700));
            await Settle(ctx);
            // Destinations: not the selected page, so it rests unhighlighted.
            var row = host.GetVisualDescendants().OfType<RadioButton>()
                .Single(r => r.Classes.Contains("nav") && r.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Destinations"));
            Assert.False(row.IsChecked);
            var surface = row.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "NavSurface");
            Assert.True(host.TryFindResource("BS.Brush.NeutralHover", theme, out var hoverBrush));
            var hover = ((ISolidColorBrush)hoverBrush!).Color;

            host.MouseMove(row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), host)!.Value);
            var frames = new List<Color>();
            var deadline = DateTime.UtcNow.AddSeconds(1);
            while (DateTime.UtcNow < deadline)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                ctx.Driver.Pump();
                if (surface.Background is ISolidColorBrush b) frames.Add(b.Color);
                await Task.Delay(8);
            }
            Assert.True(row.IsPointerOver);
            Assert.Equal(hover, frames[^1]);
            // The fade really ran (several partial frames), and every frame is
            // the hover colour at some opacity: never a lighter tone.
            Assert.True(frames.Count(c => c.A is > 0 and < 255) >= 3, $"{theme}: the fade was not sampled ({frames.Count} frames; alpha seen: {string.Join(", ", frames.Select(c => c.A).Distinct())}).");
            var off = frames.Where(c => c.A > 0 && (c.R, c.G, c.B) != (hover.R, hover.G, hover.B)).ToArray();
            Assert.True(off.Length == 0, $"{theme}: frames off the hover hue: {string.Join(", ", off.Select(c => c.ToString()))}");
            ctx.Snapshot(host, $"destinations hovered {theme}",
                $"{theme} theme: Destinations under the pointer with its grey hover wash. {frames.Count} frames sampled during the fade, all the hover colour {hover} at rising opacity, none lighter.");
            host.MouseMove(new Point(600, 700));
            await Settle(ctx);
        }
        host.Close();
    }

    private static async Task Settle(ScenarioContext ctx)
    {
        for (var i = 0; i < 40; i++) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); ctx.Driver.Pump(); await Task.Delay(8); }
    }
}
