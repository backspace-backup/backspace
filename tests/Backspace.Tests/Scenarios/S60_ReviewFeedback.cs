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
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

[Collection(ScenarioCollection.Name)]
public sealed class S60_ReviewFeedback : ScenarioBase
{
    protected override string ScenarioName => "S60-review-feedback";
    [AvaloniaFact] public async Task Run_S60() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var previousQuit = Backspace.App.QuitRequested;
        var previousForceQuit = Backspace.App.ForceQuitRequested;
        Backspace.App.QuitRequested = false;
        Backspace.App.ForceQuitRequested = false;
        ServiceLocator.Config.Update(c => { c.Preferences.ShowTrayIcon = false; c.Preferences.MinimizeToTrayOnClose = false; });
        using var main = new MainWindowViewModel();
        main.SelectedNav = NavItem.Settings;
        var window = ctx.Driver.Show(new MainWindow { DataContext = main, Width = 1100, Height = 820,
            SessionTasks = new SessionTaskControl(AppPaths.ConfigRoot, new ScenarioSession()) });
        try
        {
        await ctx.Driver.PumpAsync(100);
        var section = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Startup and tray");
        section.BringIntoView();
        await ctx.Driver.PumpAsync(100);
        Click(window, section); await ctx.Driver.PumpAsync(100);
        var check = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content?.ToString()?.StartsWith("When I close the window") == true);
        check.BringIntoView(); ctx.Driver.Pump();
        Click(window, check);
        Assert.True(ServiceLocator.Config.Current.Preferences.MinimizeToTrayOnClose);
        Assert.True(ServiceLocator.Config.Current.Preferences.ShowTrayIcon);
        // No debounce wait: the very next close must hide instead of exit.
        window.Close();
        Assert.False(window.IsVisible); Assert.False(Backspace.App.QuitRequested);
        window.Show();
        ctx.Snapshot(window, "tray-setting-saved-immediately", "Close-to-tray is enabled. The test clicked it with the tray previously hidden, immediately closed the window, and reopened the same window without quitting.");
        var quit = window.RequestForceQuitAsync(); ctx.Driver.Pump();
        var confirm = window.OwnedWindows.OfType<DestructiveConfirmWindow>().Single();
        ctx.Snapshot(confirm, "force-quit-warning", "Force quit explains interrupted work and incomplete restore files; Keep running cancels the action.");
        Click(window: confirm, confirm.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Keep running"));
        await quit;
        Assert.False(Backspace.App.ForceQuitRequested); Assert.True(window.IsVisible);

        var backup = new Backup { Name = "Travel documents" };
        var request = new RestoreRequest { Backup = backup, Destination = new Destination { Name = "Archive drive" },
            StorageName = "default", SourcePath = "C:\\Documents", TargetPath = "C:\\Recovered documents", Revision = 2 };
        var session = ServiceLocator.ActiveRestores.Begin(backup, request, () => { });
        ServiceLocator.ActiveRestores.End(session, RestoreTerminalStatus.Error, "The archive drive was disconnected. No files were recovered.");
        Assert.DoesNotContain("Preparing", session.ProgressLabel);
        main.SelectedNav = NavItem.ActiveRestores;
        await ctx.Driver.PumpAsync(100);
        var cards = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Command == main.ActiveRestores.OpenSessionCommand).ToArray();
        Assert.Single(cards);
        ctx.Snapshot(window, "restore-list-explicit-results", "One restore appears once, with its source, target, failure and explicit View results action.");
        Click(window, cards[0]); await ctx.Driver.PumpAsync(100);
        Assert.Same(session, main.ActiveRestores.SelectedSession);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && b.Command == main.ActiveRestores.OpenSessionCommand);
        ctx.Snapshot(window, "restore-result-without-duplicate-card", "The selected result replaces the card list. All restores returns to the list; there is no second copy of this restore.");
        Click(window, window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && b.Content?.ToString() == "← All restores"));
        Assert.Null(main.ActiveRestores.SelectedSession);
        ServiceLocator.ActiveRestores.Dismiss(session);
        using (var reader = await ServiceLocator.Maintenance.AcquireReadAsync(CancellationToken.None))
        {
            window.RequestQuit();
            Assert.True(ctx.Driver.WaitFor(() => window.OwnedWindows.OfType<DestructiveConfirmWindow>().Any()), "The quit confirmation never opened.");
            confirm = window.OwnedWindows.OfType<DestructiveConfirmWindow>().Single();
            Click(confirm, confirm.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Stop work and quit"));
            await ctx.Driver.PumpAsync(100);
            Assert.True(window.IsVisible); Assert.False(Backspace.App.QuitRequested);
            // Quit first saves its pause on a worker thread and force quit
            // waits for that; on GitHub's runner it can take longer than a pump
            // (the 1.0.1 rehearsal failed here on 1 November 2026).
            var savingPause = typeof(MainWindow).GetField("_savingQuitPause",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Assert.True(ctx.Driver.WaitFor(() => !(bool)savingPause.GetValue(window)!), "Quit never finished saving its pause.");
            var forceWhileStopping = window.RequestForceQuitAsync();
            // GitHub's runner can need more than one pump to open the dialog.
            Assert.True(ctx.Driver.WaitFor(() => window.OwnedWindows.OfType<DestructiveConfirmWindow>().Any()), "The force-quit confirmation never opened.");
            confirm = window.OwnedWindows.OfType<DestructiveConfirmWindow>().Single();
            ctx.Snapshot(confirm, "force-quit-remains-available-while-stopping", "Force quit remains available while normal Quit waits for an operation. Keep running cancels forced exit.");
            Click(confirm, confirm.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Keep running"));
            await forceWhileStopping;
            Assert.False(Backspace.App.ForceQuitRequested);
        }
        for (var attempt = 0; attempt < 30 && window.IsVisible; attempt++) await ctx.Driver.PumpAsync(50);
        Assert.False(window.IsVisible);
        ctx.Report.RecordNote("Scope", "Real settings checkbox, window close handler, restore cards and pointer clicks. Restore history is seeded for this visual check. Windows tray-menu interaction and a running engine's forced exit still need a native desktop check.");
        Backspace.App.QuitRequested = true; window.Close(); Backspace.App.QuitRequested = false;
        }
        finally
        {
            // Production retains this lease until the process exits. The
            // headless suite continues in the same process after this scenario.
            var admission = typeof(MainWindow).GetField("_shutdownAdmission",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            (admission!.GetValue(window) as IDisposable)?.Dispose();
            Backspace.App.QuitRequested = previousQuit;
            Backspace.App.ForceQuitRequested = previousForceQuit;
        }
    }

    private static void Click(Window window, Control control)
    {
        window.UpdateLayout();
        Screenshots.ScreenshotDiffTests.SettleInitialTransitions(window);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
    }

    // Hosted CI can have no interactive Windows login. Session lifetime is
    // controlled here; SessionTaskControlTests has separate real-Windows checks.
    private sealed class ScenarioSession : IInteractiveSessionReader
    {
        public bool Supported => true;
        public string AccountId => "S60-disposable-session";
        public InteractiveSessionIdentity? Current() => new(1, 1000, "Scenario account");
        public InteractiveSessionIdentity? Read(int id) => id == 1 ? Current() : null;
    }
}
