using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Owner, 12 November 2026: "Since when do we use orange as the checkbox
/// colors and underline for Schedule?" Check boxes, the selected tab's
/// underline and other standard controls followed Windows' accent colour
/// (the owner's is orange-red), not Backspace's. They now take Backspace's
/// accent, and follow it when it changes in Settings.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S108_StandardControlsTakeTheAccent : ScenarioBase
{
    protected override string ScenarioName => "S108-standard-controls-take-the-accent";
    [AvaloniaFact] public async Task Run_S108() => await Run();

    private static Color SystemAccent(ThemeVariant variant) =>
        Application.Current!.TryGetResource("SystemAccentColor", variant, out var value) && value is Color c ? c : default;

    // The headless app has no desktop lifetime, so it applies the accent only when asked.
    private static void Apply() => ((App)Application.Current!).ApplyAccentFromConfig();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        try
        {
            ServiceLocator.Config.Update(c => c.Preferences.Accent = AccentColor.Ocean);
            Apply();
            await ctx.Driver.PumpAsync(100);
            Assert.Equal(Color.Parse("#0061FF"), SystemAccent(ThemeVariant.Light));
            Assert.Equal(Color.Parse("#3B82F6"), SystemAccent(ThemeVariant.Dark));

            var backup = new Backup { Name = "local_c", SourcePaths = { ctx.MakeSource("c", targetBytes: 4096) } };
            var window = new BackupEditorWindow(backup, isNew: false) { Width = 1100, Height = 800 };
            ctx.Driver.Show(window);
            window.GetLogicalDescendants().OfType<TabControl>().First().SelectedIndex = 1;
            await ctx.Driver.PumpAsync(300);
            var box = window.GetLogicalDescendants().OfType<CheckBox>().First(c => c.IsChecked == true && c.IsEffectivelyVisible);
            Assert.True(box.TryFindResource("CheckBoxCheckBackgroundFillChecked", window.ActualThemeVariant, out var fill));
            var fillColor = Assert.IsAssignableFrom<ISolidColorBrush>(fill).Color;
            // Blue: the accent's family, not Windows' orange-red.
            Assert.True(fillColor.B > fillColor.R, $"The ticked box is {fillColor}, not the accent's blue.");
            ctx.Snapshot(window, "schedule with the app's accent",
                "Edit «local_c» › Schedule: the ticked boxes and the Schedule tab's underline in Backspace's blue, whatever Windows' accent colour is.");
            window.Close();

            // Another accent in Settings: the standard controls follow.
            ServiceLocator.Config.Update(c => c.Preferences.Accent = AccentColor.Emerald);
            Apply();
            await ctx.Driver.PumpAsync(100);
            Assert.Equal(Color.Parse("#059669"), SystemAccent(ThemeVariant.Light));
        }
        finally
        {
            ServiceLocator.Config.Update(c => c.Preferences.Accent = AccentColor.Ocean);
            Apply();
            await ctx.Driver.PumpAsync(50);
        }
    }
}
