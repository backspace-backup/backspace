using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

[Collection(ScenarioCollection.Name)]
public sealed class S58_LaptopWorkingAreas : ScenarioBase
{
    protected override string ScenarioName => "S58-laptop-working-areas";
    [AvaloniaFact] public async Task Run_S58() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var destination = new Destination { Name = "Family archive", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("archive") };
        var backup = new Backup { Name = "Family photographs & documents", LastRunStatus = BackupRunStatus.Success,
            LastRunEndUtc = DateTime.UtcNow.AddMinutes(-20), LastRunSummary = "Backup completed" };
        backup.SourcePaths.Add(ctx.SourcesDir);
        backup.Targets.Add(new BackupTarget { DestinationId = destination.Id, StorageName = "archive" });
        ServiceLocator.Config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(destination); });
        var vm = new MainWindowViewModel();
        var main = ctx.Driver.Show(new MainWindow { DataContext = vm });
        await vm.RefreshExternalStateAsync();

        foreach (var (width, height, label) in new[] {
            (920, 540, "minimum-920x540"),
            (1076, 558, "1366x768-at125-percent"),
            (1264, 672, "1920x1080-at150-percent") })
        {
            main.Width = width; main.Height = height;
            main.RequestedThemeVariant = label.StartsWith("1366") ? ThemeVariant.Dark : ThemeVariant.Light;
            vm.SelectedNav = NavItem.Backups;
            Settle(ctx, main);
            Assert.Equal(width, main.Bounds.Width);
            Assert.Equal(height, main.Bounds.Height);
            var card = main.GetVisualDescendants().OfType<BackupCard>().Single();
            var title = main.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Text == "Your backups");
            var cardBounds = At(card, main);
            var titleBounds = At(title, main);
            Assert.InRange(cardBounds.Left - titleBounds.Left, -1, 1);
            Assert.True(cardBounds.Right <= main.Bounds.Width - 31,
                "Backup cards must retain the page's 32px side gutters when the window is resized.");
            var shelf = card.FindControl<Border>("ActionShelf")!;
            var actions = shelf.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToArray();
            Assert.Contains(actions, b => Label(b) == "Run now");
            Assert.Contains(actions, b => Label(b) == "Restore files");
            foreach (var action in actions) AssertInside(action, shelf, Label(action));
            AssertNoOverlap(actions, shelf);
            var reviewActions = new[] { "ActivityHistoryButton" }
                .Select(name => card.FindControl<Button>(name)!).ToArray();
            Assert.All(reviewActions, button =>
            {
                Assert.NotNull(button);
                AssertInside(button, card, Label(button));
                foreach (var content in button.GetVisualDescendants().OfType<Control>()
                    .Where(c => c.IsEffectivelyVisible && c is TextBlock or PathIcon))
                    AssertInside(content, button, Label(button) + " content");
            });
            AssertNoOverlap(actions.Concat(reviewActions).ToArray(), card);
            var scroller = card.GetVisualAncestors().OfType<ScrollViewer>().First();
            scroller.Offset = new Vector(0, 0);
            Settle(ctx, main);
            ctx.Snapshot(main, label + "-initial-overview", "The backup page before scrolling at "
                + width + " × " + height + " logical pixels. The heading and card share the same side gutters; scroll the card area to reach its lower actions.");
            scroller.Offset = new Vector(0, scroller.Extent.Height);
            Settle(ctx, main);
            ctx.Snapshot(main, label + "-resize-check", "Window resize, card action wrapping and viewport containment check.");
            AssertInside(shelf, scroller, "Backup actions at maximum scroll");
            AssertFixedControlsInside(main);
            ctx.Snapshot(main, label + "-backups", "A populated backup at " + width + " × " + height
                + " logical pixels. Every backup action stays inside the card and is reachable by scrolling; the sidebar and window controls remain visible.");
        }

        main.Width = 920; main.Height = 540; main.RequestedThemeVariant = ThemeVariant.Light;
        foreach (var route in new[] { NavItem.Destinations, NavItem.Settings, NavItem.Logs })
        {
            vm.SelectedNav = route;
            Settle(ctx, main);
            AssertFixedControlsInside(main);
            ctx.Snapshot(main, "minimum-" + route.ToString().ToLowerInvariant(),
                "The " + route + " page at the 920 × 540 logical-pixel minimum. Fixed navigation and window controls stay inside the window; page content scrolls.");
        }

        // A 1366×768 desktop at125% with a50px taskbar leaves574 logical
        // vertical pixels. These simulated native frame insets leave519px
        // of client height after the8px outer margins. Save stays pinned.
        foreach (var (window, label) in new (Window, string)[] {
            (new BackupEditorWindow(backup, false), "advanced-editor"),
            (new OnboardingWindow(backup, false, isEdit: true), "easy-editor"),
            (new DestinationEditorWindow(destination, false), "destination-editor"),
            (new SourceTreePickerWindow(Array.Empty<string>()), "source-picker") })
        {
            ctx.Driver.Show(window);
            WindowPlacement.Apply(window, new PixelRect(0, 0, 1366, 718), 1.25, new Size(16, 39));
            Settle(ctx, window);
            Assert.InRange(window.Bounds.Height, 1, 519);
            AssertFixedControlsInside(window);
            var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.IsEffectivelyVisible && s.Viewport.Height > 0);
            Assert.NotNull(scroll);
            Assert.True(scroll!.Viewport.Height >= 100, "The form has no useful scrolling viewport.");
            ctx.Snapshot(window, "scaled-laptop-" + label,
                "The " + label + " fitted to a1366 × 768 laptop at125% scaling, including taskbar and native frame allowance. Footer controls remain on screen while the form scrolls.");
            window.Close();
        }
        ctx.Report.RecordNote("Proof boundary", "Headless bounds and screenshots use the logical working areas derived from the stated screen/scaling combinations, plus native frame allowance for dialogs. The placement tests also drive real window move and restore events with simulated monitor information. They check same-scale monitor changes, preserve ordinary dragging and handle a smaller display after restoring a maximized window. This does not establish a market-share percentage or replace checks on physical monitors and Windows scaling.");
    }

    private static void Settle(ScenarioContext ctx, Window window)
    {
        ctx.Driver.Pump(); window.UpdateLayout();
        Screenshots.ScreenshotDiffTests.SettleInitialTransitions(window);
    }
    private static string Label(Control control) => control is ContentControl { Content: string text }
        ? text : control.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => !string.IsNullOrEmpty(t.Text))?.Text ?? control.GetType().Name;
    private static Rect At(Visual child, Visual parent) => new(
        child.TranslatePoint(new Point(), parent) ?? throw new InvalidOperationException("Disconnected visual"), child.Bounds.Size);
    private static void AssertInside(Visual child, Visual parent, string label)
    {
        var b = At(child, parent);
        Assert.True(b.Left >= -1 && b.Top >= -1 && b.Right <= parent.Bounds.Width + 1 && b.Bottom <= parent.Bounds.Height + 1,
            label + " escapes: " + b + " parent: " + parent.Bounds.Size);
    }
    private static void AssertFixedControlsInside(Window window)
    {
        foreach (var control in window.GetVisualDescendants().OfType<Button>().Where(c => c.IsEffectivelyVisible
            && c.Bounds.Width > 0 && !c.GetVisualAncestors().OfType<ScrollViewer>().Any()))
            AssertInside(control, window, Label(control));
        foreach (var route in window.GetVisualDescendants().OfType<RadioButton>().Where(c => c.IsEffectivelyVisible && c.Classes.Contains("nav")))
            AssertInside(route, window, Label(route));
    }
    private static void AssertNoOverlap(Button[] buttons, Visual ancestor)
    {
        for (var i = 0; i < buttons.Length; i++)
            for (var j = i + 1; j < buttons.Length; j++)
            {
                var a = At(buttons[i], ancestor); var b = At(buttons[j], ancestor);
                Assert.False(Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left) > 0.5
                    && Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) > 0.5,
                    Label(buttons[i]) + " overlaps " + Label(buttons[j]));
            }
    }
}
