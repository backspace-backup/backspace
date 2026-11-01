using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.WebDav;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

[Collection(ScenarioCollection.Name)]
public sealed class S49_ReviewUiRepairs : ScenarioBase
{
    protected override string ScenarioName => "S49-review-ui-repairs";
    [AvaloniaFact] public async Task Run_S49() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var registry = new MountFixture();
        var backup = new Backup { Name = "Travel documents" };
        using var card = new BackupCardViewModel(backup, true, false, registry);
        var cardView = new BackupCard { DataContext = card };
        var host = ctx.Driver.Show(new Window { Width = 1100, Height = 540, Content = cardView });
        ClickButton(host, "Mount"); ctx.Driver.Pump();
        Assert.Equal("Unmount", card.MountActionLabel);
        Assert.Equal("Z:", card.MountedDrive);
        ctx.Snapshot(host, "mounted-card-offers-unmount", "A successful mount changes its action to Unmount; the tooltip names drive Z:.");
        ClickButton(host, "Unmount"); ctx.Driver.Pump();
        Assert.Equal("Mount", card.MountActionLabel);
        Assert.Null(registry.GetMountedDrive(backup.Id));
        Assert.Equal(1, registry.MountCalls);
        Assert.Equal(1, registry.UnmountCalls);
        ctx.Snapshot(host, "unmounted-card-offers-mount", "Unmount releases this backup's drive and restores the Mount action.");
        host.Close();

        using var editor = new BackupEditorViewModel(new Backup { Name = "Every setting stays reachable", CheckSchedule = new BackupSchedule { Frequency = ScheduleFrequency.Monthly } }, false);
        var window = ctx.Driver.Show(new BackupEditorWindow { DataContext = editor, Width = 980, Height = 640 });
        window.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 1;
        ctx.Driver.Pump(); window.UpdateLayout();
        var label = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "BACKUP CHECKS");
        var scroller = label.GetVisualAncestors().OfType<ScrollViewer>().First();
        scroller.Offset = new Vector(0, scroller.Extent.Height); ctx.Driver.Pump(); window.UpdateLayout();
        var policy = label.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("surface"));
        var top = policy.TranslatePoint(new Point(0, 0), scroller)!.Value.Y;
        Assert.True(top + policy.Bounds.Height <= scroller.Viewport.Height + 1,
            $"Policy card bounds {top}..{top + policy.Bounds.Height}, viewport {scroller.Viewport.Height}");
        ctx.Snapshot(window, "check-policy-at-minimum-size", "The bottom of the check choices is reachable at maximum scroll in a 980 × 640 window. Earlier settings remain reachable by scrolling upward.");

        window.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 2; ctx.Driver.Pump();
        window.GetVisualDescendants().OfType<TabControl>().First(t => t.Items.OfType<TabItem>().Any(x => x.Header?.ToString() == "Advanced")).SelectedIndex = 1;
        ctx.Driver.Pump(); window.UpdateLayout();
        // The pointer hit-test scene is updated on a render tick, not by
        // layout alone. Let the newly selected tab actually render before
        // clicking it; do not bypass the click by calling Focus().
        Screenshots.ScreenshotDiffTests.SettleInitialTransitions(window);
        var text = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.IsEffectivelyVisible && t.AcceptsReturn && !t.IsReadOnly);
        var point = text.TranslatePoint(new Point(16, 16), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); ctx.Driver.Pump();
        Assert.True(text.IsFocused, $"Filter editor pointer {point}, text bounds {text.Bounds}, window {window.Bounds}; hit {window.InputHitTest(point)?.GetType().Name} did not focus the text box."); Assert.NotNull(text.Cursor);
        ctx.Snapshot(window, "editing-cursor-after-pointer-click", "A real pointer click focuses the filter editor; its text-editing cursor and accent caret are enabled.");
        window.Close();

        var request = new RestoreRequest { Backup = backup, Destination = new Destination { Name = "Archive" }, StorageName = "default", SourcePath = "C:\\fixture", TargetPath = "C:\\restored-fixture", Revision = 1 };
        bool cancelled = false;
        var session = ServiceLocator.ActiveRestores.Begin(backup, request, () => cancelled = true);
        try
        {
            Assert.True(MainWindow.HasAnyRunningOperation());
            Backspace.App.QuitRequested = false;
            var main = ctx.Driver.Show(new MainWindow { DataContext = new MainWindowViewModel() });
            main.RequestQuit(); ctx.Driver.Pump();
            var dialog = main.OwnedWindows.OfType<DestructiveConfirmWindow>().Single();
            ctx.Snapshot(dialog, "tray-quit-protects-running-restore", "Tray Quit explains active restores will stop safely and scheduled backups remain enabled; Keep running cancels quitting.");
            ClickButton(dialog, "Keep running"); ctx.Driver.Pump();
            Assert.True(main.IsVisible); Assert.False(cancelled); Assert.False(Backspace.App.QuitRequested);
            MainWindow.CancelEverything();
            Assert.True(cancelled);
            Assert.True(MainWindow.HasAnyRunningOperation()); // request is not completion
            ServiceLocator.ActiveRestores.End(session, RestoreTerminalStatus.Cancelled, null);
            Assert.False(MainWindow.HasAnyRunningOperation());
            Backspace.App.QuitRequested = true; main.Close(); Backspace.App.QuitRequested = false;
        }
        finally { ServiceLocator.ActiveRestores.End(session, RestoreTerminalStatus.Cancelled, null); ServiceLocator.ActiveRestores.Dismiss(session); Backspace.App.QuitRequested = false; }
        await Task.CompletedTask;
    }

    private static void ClickButton(Window window, string label)
    {
        window.UpdateLayout();
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible &&
            (b.Content?.ToString() == label || b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == label)));
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
    }

    private sealed class MountFixture : ISnapshotMountRegistry
    {
        public event Action<string>? Changed;
        public bool HasPendingOperations => false;
        public void CancelPending() { }
        public string? Drive;
        public int MountCalls, UnmountCalls;
        public string? GetMountedDrive(string id) => Drive;
        public Task<MountResult> MountForAsync(Backup backup, CancellationToken ct = default)
        { MountCalls++; Drive = "Z:"; Changed?.Invoke(backup.Id); return Task.FromResult(MountResult.Ok(Drive)); }
        public Task UnmountAsync(string id, CancellationToken ct = default)
        { UnmountCalls++; Drive = null; Changed?.Invoke(id); return Task.CompletedTask; }
        public Task DisposeAllAsync() => Task.CompletedTask;
    }
}
