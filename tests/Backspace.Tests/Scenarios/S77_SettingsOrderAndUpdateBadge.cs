using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// U086-U090: Settings sections in the requested order (rare ones last, App
/// updates at the end), "Watchdog" without the provider's name, and the
/// sidebar version that shows whether an update is waiting and opens App
/// updates. U076: a one-line notice is centred in its box.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S77_SettingsOrderAndUpdateBadge : ScenarioBase
{
    protected override string ScenarioName => "S77-settings-order-and-update-badge";

    [AvaloniaFact] public async Task Run_S77() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var previous = (ServiceLocator.Config.Current.Preferences.AvailableUpdateVersion, ServiceLocator.Config.Current.Preferences.LastUpdateCheckUtc);
        try
        {
            ServiceLocator.Config.Update(c => { c.Preferences.AvailableUpdateVersion = "99.0.0"; c.Preferences.LastUpdateCheckUtc = DateTime.UtcNow; });
            using var main = new MainWindowViewModel();
            main.Settings.Updates.RaiseFooter();
            var host = ctx.Driver.Show(new MainWindow { Width = 1100, Height = 900, DataContext = main });
            await ctx.Driver.PumpAsync(200);
            var badge = host.FindControl<Border>("FooterUpdateBadge")!;
            var mark = host.FindControl<PathIcon>("FooterUpToDateMark")!;
            Assert.True(badge.IsEffectivelyVisible, "A newer version must show the Update badge beside the version.");
            Assert.False(mark.IsEffectivelyVisible);
            Assert.Contains("99.0.0 is available", ToolTip.GetTip(host.FindControl<Button>("FooterVersionButton")!) as string);
            ctx.Snapshot(host, "update available badge", "Bottom left: the version with an accent 'Update' badge; hovering says 99.0.0 is available.");

            // Clicking the version opens Settings at App updates.
            host.FindControl<Button>("FooterVersionButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await ctx.Driver.PumpAsync(300);
            Assert.Equal(NavItem.Settings, main.SelectedNav);
            var settings = host.GetVisualDescendants().OfType<SettingsView>().Single();
            var titles = settings.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Classes.Contains("section-title") && t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Equal(new[] { "Appearance", "Startup and tray", "Simultaneous backups", "Notifications", "Watchdog", "Saved passwords", "Logs & diagnostics",
                "Cloud OAuth apps", "Import existing backups", "Remove Backspace from this PC", "App updates" }, titles.Where(t => t != "Administrator access").ToArray());
            var card = settings.FindControl<Border>("AppUpdatesCard")!;
            var scroller = settings.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.True(scroller.Offset.Y > 0, "Settings should have scrolled down to App updates.");
            ctx.Snapshot(host, "version opens app updates", "Clicking the version opened Settings scrolled to App updates, the last section.");

            // U202: the limits are "Simultaneous backups", per destination and in total.
            var limits = settings.FindControl<Expander>("BackupsAtOnceSection")!;
            limits.IsExpanded = true;
            await ctx.Driver.PumpAsync(200);
            limits.BringIntoView();
            await ctx.Driver.PumpAsync(200);
            var labels = limits.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains("PER DESTINATION", labels);
            Assert.Contains("IN TOTAL", labels);
            Assert.Contains(labels, t => t?.Contains("Restores and test restores you start only read", StringComparison.Ordinal) == true);
            ctx.Snapshot(host, "simultaneous backups", "Settings › Simultaneous backups: Per destination and In total; the note says checks wait for each other and restores you start aren't counted.");
            limits.IsExpanded = false;

            // U208: the Healthchecks key's result is green when it works, red when not.
            var watchdog = settings.GetVisualDescendants().OfType<Expander>()
                .Single(e => e.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Watchdog"));
            watchdog.IsExpanded = true;
            foreach (var (ok, text, label) in new[] {
                (true, "The key works. Created a Healthchecks.io check for 4 backups.", "healthchecks key works"),
                (false, "Checks could not be created: Healthchecks.io refused the API key. Use the project's regular API key, not the read-only one.", "healthchecks key refused") })
            {
                main.Settings.HealthchecksStatusOk = ok;
                main.Settings.HealthchecksStatus = text;
                await ctx.Driver.PumpAsync(200);
                // U208: the same notice as the Backups page, green or red, its
                // round glyph centred on the text.
                var row = settings.FindControl<Border>("HealthchecksStatusRow")!;
                var shown = settings.FindControl<TextBlock>("HealthchecksStatusText")!;
                Assert.True(row.IsEffectivelyVisible);
                Assert.Equal(ok, row.Classes.Contains("ok")); Assert.Equal(!ok, row.Classes.Contains("fail"));
                Assert.True(host.TryFindResource(ok ? "BS.Brush.BandOkBg" : "BS.Brush.BandFailBg", host.ActualThemeVariant, out var brush));
                Assert.Equal(((Avalonia.Media.ISolidColorBrush)brush!).Color, ((Avalonia.Media.ISolidColorBrush)row.Background!).Color);
                var glyph = row.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("notice-glyph"));
                Assert.True(Math.Abs((glyph.Bounds.Center.Y) - (shown.Bounds.Center.Y)) <= 1, "The glyph must be centred on the text.");
                shown.BringIntoView();
                await ctx.Driver.PumpAsync(200);
                ctx.Snapshot(host, label, ok
                    ? "Settings › Watchdog: a green notice, its round check centred on \"The key works. Created a Healthchecks.io check for 4 backups.\", like the Backups page's notices."
                    : "Settings › Watchdog: a red notice, its round cross centred on the refused key's message.");
            }
            main.Settings.HealthchecksStatus = "";
            watchdog.IsExpanded = false;

            // Up to date: a small check instead of the badge.
            ServiceLocator.Config.Update(c => { c.Preferences.AvailableUpdateVersion = ""; c.Preferences.LastUpdateCheckUtc = DateTime.UtcNow; });
            main.Settings.Updates.RaiseFooter();
            await ctx.Driver.PumpAsync(100);
            Assert.False(badge.IsEffectivelyVisible);
            Assert.True(mark.IsEffectivelyVisible);

            // U076: a one-line notice sits centred in its box.
            if (main.Settings.BackgroundBackups.Supported)
            {
                main.Settings.BackgroundBackups.Error = "Windows approval was cancelled. Your saved settings and credentials have been preserved.";
                scroller.Offset = new Avalonia.Vector(0, 0);
                await ctx.Driver.PumpAsync(150);
                var text = settings.FindControl<TextBlock>("ScheduledBackupsError")!;
                var box = (Border)text.Parent!;
                var top = text.Bounds.Top; var bottom = box.Bounds.Height - text.Bounds.Bottom;
                Assert.True(Math.Abs(top - bottom) <= 2, $"Notice text is not centred: {top:F1} above, {bottom:F1} below.");
                ctx.Snapshot(host, "notice centred", "Administrator access: the orange notice has the same space above and below its line.");
                main.Settings.BackgroundBackups.Error = "";
            }
        }
        finally
        {
            ServiceLocator.Config.Update(c => { c.Preferences.AvailableUpdateVersion = previous.Item1; c.Preferences.LastUpdateCheckUtc = previous.Item2; });
        }
    }
}
