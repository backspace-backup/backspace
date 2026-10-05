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
public sealed class S54_EditorialMinimumWidth : ScenarioBase
{
    protected override string ScenarioName => "S54-editorial-minimum-width";
    [AvaloniaFact] public async Task Run_S54() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var destination = new Destination { Name = "Family archive and important documents",
            Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("archive") };
        var backup = new Backup
        {
            Name = "Family photographs & documents", LastRunStatus = BackupRunStatus.Success,
            LastRunEndUtc = DateTime.UtcNow.AddMinutes(-20), LastRunSummary = "Backup completed",
            LastVerifyUtc = DateTime.UtcNow.AddDays(-1), LastVerifyPass = true,
            LastVerifySummary = "All files match their originals"
        };
        backup.SourcePaths.Add(System.IO.Path.Combine(ctx.SourcesDir, "Photographs from family holidays"));
        backup.SourcePaths.Add(System.IO.Path.Combine(ctx.SourcesDir, "Documents and records"));
        backup.Targets.Add(new BackupTarget { DestinationId = destination.Id, StorageName = "archive" });
        ServiceLocator.Config.Update(c => { c.Destinations.Add(destination); c.Backups.Add(backup); });
        var vm = new MainWindowViewModel();
        var window = ctx.Driver.Show(new MainWindow { DataContext = vm, Width = 1180, Height = 720 });
        await vm.RefreshExternalStateAsync();
        // Retained results expose the fifth navigation row without leaving a
        // fake operation running when the real window closes during cleanup.
        var retained = ServiceLocator.ActiveRestores.Begin(backup, new RestoreRequest
        {
            Backup = backup, Destination = destination, SourcePath = backup.SourcePaths[0],
            StorageName = "archive", Revision = 1, Files = new[] { "photo.jpg" },
            TargetPath = ctx.SourcesDir
        });
        ServiceLocator.ActiveRestores.End(retained, RestoreTerminalStatus.Success, null);

        foreach (var (theme, label) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
        {
            window.RequestedThemeVariant = theme;
            ctx.Driver.Pump(); window.UpdateLayout();
            Screenshots.ScreenshotDiffTests.SettleInitialTransitions(window);
            Assert.Equal(1180, window.Bounds.Width);
            Assert.Equal(720, window.Bounds.Height);
            var card = window.GetVisualDescendants().OfType<BackupCard>().Single();
            var cardVm = (BackupCardViewModel)card.DataContext!;
            // Exercise the longer label without starting a real mount; this
            // scenario measures layout only, not drive creation behavior.
            if (cardVm.CanMountSnapshot) cardVm.MountedDrive = "Z:";
            ctx.Driver.Pump(); window.UpdateLayout();

            var shelf = card.FindControl<Border>("ActionShelf")!;
            var cluster = card.FindControl<WrapPanel>("ActionCluster")!;
            var delete = card.FindControl<Button>("DeleteBackupButton")!;
            Assert.True(double.IsFinite(cluster.Bounds.Width) && cluster.Bounds.Width > 0);
            Assert.True(cluster.Bounds.Width < shelf.Bounds.Width - delete.Bounds.Width);
            var actions = shelf.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToArray();
            foreach (var expected in new[] { "Run now", "Edit", "Browse", "Restore files", "Delete" })
                Assert.Contains(actions, b => Label(b) == expected);
            if (cardVm.CanMountSnapshot) Assert.Contains(actions, b => Label(b) == "Unmount");

            var rectangles = actions.Select(b => (Button: b, Rect: BoundsIn(b, shelf))).ToArray();
            foreach (var (button, rectangle) in rectangles)
            {
                Assert.True(rectangle.Width > 0 && rectangle.Height >= 39, $"{Label(button)} has invalid bounds {rectangle}.");
                Assert.True(rectangle.Left >= -0.5 && rectangle.Top >= -0.5
                    && rectangle.Right <= shelf.Bounds.Width + 0.5 && rectangle.Bottom <= shelf.Bounds.Height + 0.5,
                    $"{Label(button)} escapes the shelf: {rectangle}, shelf {shelf.Bounds.Size}.");
                foreach (var content in button.GetVisualDescendants().OfType<Control>()
                    .Where(c => c.IsEffectivelyVisible && c is TextBlock or PathIcon))
                    AssertContained(BoundsIn(content, button), button.Bounds.Size,
                        $"{Label(button)} clips its {content.GetType().Name}");
            }
            for (var i = 0; i < rectangles.Length; i++)
                for (var j = i + 1; j < rectangles.Length; j++)
                {
                    var a = rectangles[i]; var b = rectangles[j];
                    var overlapWidth = Math.Min(a.Rect.Right, b.Rect.Right) - Math.Max(a.Rect.Left, b.Rect.Left);
                    var overlapHeight = Math.Min(a.Rect.Bottom, b.Rect.Bottom) - Math.Max(a.Rect.Top, b.Rect.Top);
                    Assert.False(overlapWidth > 0.5 && overlapHeight > 0.5, $"{Label(a.Button)} overlaps {Label(b.Button)}.");
                }

            var navigation = window.GetVisualDescendants().OfType<RadioButton>()
                .Where(b => b.IsEffectivelyVisible && b.Classes.Contains("nav")).ToArray();
            Assert.Equal(5, navigation.Length);
            var restores = navigation.Single(b => Label(b) == "Restores");
            var settings = navigation.Single(b => Label(b) == "Settings");
            var activity = navigation.Single(b => Label(b) == "Activity & logs");
            var groupGap = BoundsIn(settings, window).Top - BoundsIn(restores, window).Bottom;
            Assert.InRange(groupGap, 10, 56);
            Assert.True(BoundsIn(activity, window).Bottom < 400, "Supporting navigation is stranded in the footer.");
            foreach (var route in navigation)
                AssertContained(BoundsIn(route, window), window.Bounds.Size, $"Navigation {Label(route)} is clipped");
            var footer = window.FindControl<TextBlock>("FooterVersionText")!;
            Assert.True(BoundsIn(footer, window).Top > BoundsIn(activity, window).Bottom + 80);

            var scroller = card.GetVisualAncestors().OfType<ScrollViewer>().First();
            scroller.Offset = new Vector(0, scroller.Extent.Height);
            ctx.Driver.Pump(); window.UpdateLayout();
            var shelfAt = BoundsIn(shelf, scroller);
            AssertContained(shelfAt, scroller.Viewport, "The action shelf is clipped at maximum scroll");
            ctx.Snapshot(window, $"{label}-1180-all-actions-fit",
                $"{label} theme at a 1180 × 720 window: populated backup, every action fits without overlap, supporting navigation is grouped near the top and provenance stays at the bottom.");

            // This wider application window can still fit one compact row.
            // A narrower card forces wrapping, so restoring the former Auto
            // column cannot pass merely because this machine's labels fit.
            card.Width = 600;
            ctx.Driver.Pump(); window.UpdateLayout();
            var actionRows = actions.Where(b => !ReferenceEquals(b, delete))
                .Select(b => Math.Round(BoundsIn(b, cluster).Top)).Distinct().Count();
            Assert.True(actionRows >= 2, "The narrow card must exercise actual action wrapping.");
            foreach (var button in actions)
                AssertContained(BoundsIn(button, shelf), shelf.Bounds.Size,
                    $"Narrow-card action {Label(button)} escapes the shelf");
            scroller.Offset = new Vector(0, scroller.Extent.Height);
            ctx.Driver.Pump(); window.UpdateLayout();
            AssertContained(BoundsIn(shelf, scroller), scroller.Viewport, "The wrapped action shelf is clipped");
            ctx.Snapshot(window, $"{label}-narrow-card-wraps-actions",
                $"A deliberately narrow 600 px card forces actions onto multiple rows; every action stays inside the card and reachable after scrolling.");
            card.Width = double.NaN;
            ctx.Driver.Pump(); window.UpdateLayout();
        }
        ServiceLocator.ActiveRestores.Dismiss(retained);
        ctx.Report.RecordNote("proof boundary", "This is a layout fixture with realistic names and paths. No backup, mount or deletion runs. Both light/dark bounds are measured; S58 separately checks smaller laptop working areas; native Windows review remains necessary for display scaling.");
    }

    private static string Label(Control control) => control is ContentControl { Content: string text }
        ? text : control.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => !string.IsNullOrEmpty(t.Text))?.Text ?? "";
    private static Rect BoundsIn(Visual visual, Visual ancestor) => new(
        visual.TranslatePoint(new Point(0, 0), ancestor) ?? throw new InvalidOperationException("Disconnected visual."), visual.Bounds.Size);
    private static void AssertContained(Rect rectangle, Size container, string message) =>
        Assert.True(rectangle.Left >= -1 && rectangle.Top >= -1
            && rectangle.Right <= container.Width + 1 && rectangle.Bottom <= container.Height + 1,
            $"{message}: {rectangle}, container {container}.");
}
