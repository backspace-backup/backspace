using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Diagnostics;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Backspace.Tests.Scenarios;

[Collection(ScenarioCollection.Name)]
public sealed class S59_DropdownAffordance : ScenarioBase
{
    protected override string ScenarioName => "S59-dropdown-affordance";
    [AvaloniaFact] public Task Run_S59() => Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var previousTheme = ServiceLocator.Config.Current.Preferences.Theme;
        try
        {
            foreach (var (theme, label) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                using var vm = new SettingsViewModel { Theme = AppTheme.System };
                var window = ctx.Driver.Show(new Window { Width = 1000, Height = 660,
                    RequestedThemeVariant = theme, Content = new SettingsView { DataContext = vm } });
                await Settle();
                var picker = window.GetVisualDescendants().OfType<ComboBox>()
                    .Single(c => ReferenceEquals(c.ItemsSource, vm.AvailableThemes));
                var glyph = picker.GetVisualDescendants().OfType<PathIcon>().Single(p => p.Name == "DropDownGlyph");
                Assert.True(glyph.IsEffectivelyVisible);
                var screenshot = ctx.Snapshot(window, label + "-closed-dropdowns",
                    "Theme and accent fields have clearly visible down chevrons, distinguishing them from text entry fields.");
                var opaquePixels = CountVisibleChevronPixels(glyph, window, screenshot);
                Assert.True(opaquePixels >= 10,
                    $"A displayed dropdown must paint at least ten clearly visible chevron pixels; found {opaquePixels}, bounds {glyph.Bounds}, template geometry bounds {glyph.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single().Data?.Bounds}, stroke {glyph.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single().StrokeThickness}. The old scaled stroke was effectively invisible.");

                // Click the actual chevron region rather than setting IsDropDownOpen.
                var click = glyph.TranslatePoint(new Point(glyph.Bounds.Width / 2, glyph.Bounds.Height / 2), window)!.Value;
                window.MouseDown(click, MouseButton.Left); window.MouseUp(click, MouseButton.Left);
                await Settle();
                Assert.True(picker.IsDropDownOpen);
                var popup = picker.GetVisualDescendants().OfType<Popup>().Single();
                Assert.IsAssignableFrom<Control>(((IPopupHostProvider)popup).PopupHost);
                ctx.Snapshot(window, label + "-open-theme-choices",
                    "Clicking the chevron opens the actual System, Light and Dark chooser over Settings; its existing popup and selection behavior are retained.");
                var option = popup.Child!.GetVisualDescendants().OfType<ComboBoxItem>()
                    .Single(i => Equals(i.Content, AppTheme.Light));
                var popupTop = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(option));
                var optionPoint = option.TranslatePoint(new Point(option.Bounds.Width / 2, option.Bounds.Height / 2), popupTop)!.Value;
                popupTop.MouseDown(optionPoint, MouseButton.Left); popupTop.MouseUp(optionPoint, MouseButton.Left);
                await Settle();
                Assert.False(picker.IsDropDownOpen);
                Assert.Equal(AppTheme.Light, vm.Theme);
                Assert.Equal(AppTheme.Light, ServiceLocator.Config.Current.Preferences.Theme);

                // Keyboard users retain the native Alt+Down / Escape / arrow behavior.
                picker.Focus();
                Press(window, PhysicalKey.ArrowDown, RawInputModifiers.Alt);
                await Settle();
                Assert.True(picker.IsDropDownOpen);
                Press(window, PhysicalKey.Escape);
                await Settle();
                Assert.False(picker.IsDropDownOpen);
                Assert.Equal(AppTheme.Light, vm.Theme); // Escape must not change the value.
                picker.Focus(); Press(window, PhysicalKey.ArrowDown);
                await Settle();
                Assert.Equal(AppTheme.Dark, vm.Theme);
                Assert.False(picker.IsDropDownOpen);
                ctx.Snapshot(window, label + "-keyboard-selection",
                    "The original dropdown still accepts keyboard selection. Escape dismissed the list without changing Light; Down then selected Dark and persisted it.");

                // A disabled picker must keep its affordance without opening or changing data.
                picker.IsEnabled = false;
                await Settle();
                window.MouseDown(click, MouseButton.Left); window.MouseUp(click, MouseButton.Left);
                await Settle();
                Assert.False(picker.IsDropDownOpen);
                Assert.Equal(AppTheme.Dark, vm.Theme);
                window.Close();
            }
        }
        finally { ServiceLocator.Config.Update(c => c.Preferences.Theme = previousTheme); }
        ctx.Report.RecordNote("Scope and negative cases", "Real SettingsView controls, actual pointer and keyboard events, persisted selection, both themes, Escape cancellation and disabled non-interaction. The headless platform places the real popup in its overlay surface, captured with the whole window. No ComboBox template or event handling was replaced.");

        async Task Settle()
        {
            for (var i = 0; i < 14; i++)
            {
                await Task.Delay(16); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); ctx.Driver.Pump();
            }
        }

    }

    private static void Press(TopLevel window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPressQwerty(key, modifiers); window.KeyReleaseQwerty(key, modifiers); }

    private static int CountVisibleChevronPixels(Control glyph, Window window, string screenshot)
    {
        // Inspect the actual screenshot, not an isolated control render:
        // templated glyphs retain a nonzero parent-relative layout offset.
        using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(screenshot);
        var origin = glyph.TranslatePoint(default, window)!.Value;
        var left = (int)Math.Floor(origin.X);
        var top = (int)Math.Floor(origin.Y);
        var background = image[left, top];
        var count = 0;
        for (var y = top; y < top + 12; y++)
            for (var x = left; x < left + 12; x++)
            {
                var pixel = image[x, y];
                if (Math.Abs(pixel.R - background.R) + Math.Abs(pixel.G - background.G) + Math.Abs(pixel.B - background.B) > 120) count++;
            }
        return count;
    }
}
