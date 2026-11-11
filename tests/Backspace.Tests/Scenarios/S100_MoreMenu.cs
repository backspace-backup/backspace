using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// U191 and U192 (11 November 2026). The card's More menu looks like a macOS
/// menu: a rounded panel, compact rows, an inset rounded highlight in the
/// accent colour, disabled items clearly faded. While the backup runs, every
/// item in it waits for the run, so More itself is greyed out and says why
/// on hover instead of opening a menu of greyed-out items.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S100_MoreMenu : ScenarioBase
{
    protected override string ScenarioName => "S100-more-menu";

    [AvaloniaFact] public async Task Run_S100() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var dest = new Destination { Name = "Google Drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("repo") };
        var backup = new Backup { Name = "local_d", SourcePaths = { ctx.MakeSource("d", targetBytes: 4096) },
            LastRunStatus = BackupRunStatus.Success, LastRunEndUtc = DateTime.UtcNow.AddHours(-1) };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(backup); });

        using var main = new MainWindowViewModel();
        var host = ctx.Driver.Show(new MainWindow { Width = 1100, Height = 820, DataContext = main });
        await ctx.Driver.PumpAsync(200);
        var cardView = host.GetVisualDescendants().OfType<BackupCard>().Single();
        var card = (BackupCardViewModel)cardView.DataContext!;
        var more = cardView.FindControl<Button>("MoreActionsButton")!;
        Assert.True(more.IsEnabled);
        Assert.Equal("Edit settings or remove this backup.", ToolTip.GetTip(more));

        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            host.RequestedThemeVariant = theme;
            Screenshots.ScreenshotDiffTests.SettleInitialTransitions(host);
            more.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            ctx.Driver.Pump();
            var menu = Assert.IsType<MenuFlyout>(more.Flyout);
            Assert.True(menu.IsOpen);
            var items = menu.Items.OfType<MenuItem>().ToArray();
            var presenter = items[0].FindAncestorOfType<MenuFlyoutPresenter>()!;
            Assert.NotNull(presenter);
            Assert.Equal(new object[] { "Edit backup settings", "Remove backup…" }, items.Select(i => i.Header).ToArray());
            Assert.All(items, i => Assert.True(i.IsEnabled));
            Assert.Equal(new CornerRadius(10), presenter.CornerRadius);

            // Hover the first row: the rounded accent highlight with white text.
            var popupRoot = TopLevel.GetTopLevel(presenter)!;
            var first = items[0];
            popupRoot.MouseMove(first.TranslatePoint(new Point(first.Bounds.Width / 2, first.Bounds.Height / 2), popupRoot)!.Value);
            for (var i = 0; i < 20 && !first.IsPointerOver; i++) { await Task.Delay(16); ctx.Driver.Pump(); }
            Assert.True(first.IsPointerOver);
            Assert.Equal(Avalonia.Media.Colors.White, Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(first.Foreground).Color);
            SnapshotPopup(ctx, popupRoot, $"more menu, first item hovered, {theme}",
                $"{theme} theme: a rounded panel with a hairline border, two compact rows and a thin separator; the hovered \"Edit backup settings\" row has an inset rounded highlight in the accent colour with white text.");
            menu.Hide();
            ctx.Driver.Pump();
        }

        // A disabled item fades clearly: shown in the log console's menu style
        // by the same theme, checked here on the item itself.
        var disabled = new MenuItem { Header = "Disabled", IsEnabled = false };
        var probe = new MenuFlyout { Items = { disabled } };
        probe.ShowAt(more);
        ctx.Driver.Pump();
        Assert.Equal(0.35, disabled.Opacity, 2);
        probe.Hide();
        ctx.Driver.Pump();

        // While the backup runs, More is greyed out with the reason on hover.
        host.RequestedThemeVariant = ThemeVariant.Light;
        card.SetRunning(DateTime.UtcNow.AddMinutes(-3));
        ctx.Driver.Pump();
        Assert.False(more.IsEnabled);
        Assert.True(ToolTip.GetShowOnDisabled(more));
        Assert.Equal("Editing and removing this backup are available once it is no longer running.", ToolTip.GetTip(more));
        // A real click on the greyed-out button opens nothing.
        var at = more.TranslatePoint(new Point(more.Bounds.Width / 2, more.Bounds.Height / 2), host)!.Value;
        host.MouseDown(at, Avalonia.Input.MouseButton.Left);
        host.MouseUp(at, Avalonia.Input.MouseButton.Left);
        ctx.Driver.Pump();
        Assert.False(more.Flyout?.IsOpen ?? false);
        host.MouseMove(new Point(2, 2));
        ctx.Snapshot(host, "more greyed out while running",
            "The card is running: More is greyed out like the other actions that wait for the run; its hover says editing and removing are available once the backup is no longer running.");
        host.Close();
    }

    private static void SnapshotPopup(ScenarioContext ctx, TopLevel root, string label, string note)
    {
        for (var i = 0; i < 6; i++) { Dispatcher.UIThread.RunJobs(); root.UpdateLayout(); }
        var size = new PixelSize(Math.Max(1, (int)Math.Ceiling(root.Bounds.Width)), Math.Max(1, (int)Math.Ceiling(root.Bounds.Height)));
        var safe = new string(label.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        var path = Path.Combine(ctx.ScreenshotsDir, "popup-" + safe + ".png");
        using (var bmp = new RenderTargetBitmap(size, new Vector(96, 96)))
        {
            bmp.Render(root);
            bmp.Save(path);
        }
        ctx.Report.RecordStep(label, path, note);
    }
}
