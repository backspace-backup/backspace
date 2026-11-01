using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

[Collection(ScenarioCollection.Name)]
public sealed class S65_FileAccessWait : ScenarioBase
{
    protected override string ScenarioName => "S65-file-access-wait";
    [AvaloniaFact] public async Task Run_S65() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        if (!OperatingSystem.IsWindows()) { ctx.Skip("Requires actual Windows replacement locks."); return; }
        App.QuitRequested = false; App.ForceQuitRequested = false;
        ServiceLocator.Config.Update(c => { c.Preferences.ShowTrayIcon = false; c.Preferences.MinimizeToTrayOnClose = false; });
        var stateRoot = Path.Combine(ctx.RootDir, "pause-settings");
        var tasks = new SessionTaskControl(stateRoot, new ScenarioSession());
        tasks.PauseForCurrentSession(); tasks.ResumeOnGuiLaunch();
        var path = Directory.GetFiles(stateRoot, "task-pause-*.json").Single();
        var original = File.ReadAllBytes(path);
        using var main = new MainWindowViewModel();
        var window = ctx.Driver.Show(new MainWindow { DataContext = main, Width = 1024, Height = 700, SessionTasks = tasks });
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            await ctx.Driver.PumpAsync(100);
            Click(window, window.FindControl<Button>("CloseBtn")!);
            for (var n = 0; n < 160 && !FileAccessRetry.Snapshot().Any(x => x.Path == path); n++) await ctx.Driver.PumpAsync(50);
            Assert.Single(FileAccessRetry.Snapshot(), x => x.Path == path);
            await ctx.Driver.PumpAsync(100);
            Assert.True(window.IsVisible); Assert.False(App.QuitRequested);
            Assert.Equal(original, File.ReadAllBytes(path));
            var stop = Button(window, "Cancel quit");
            Assert.True(stop.IsEnabled);
            ctx.Snapshot(window, "quit-waits-and-explains", "Closing the actual window encounters a real Windows file lock. Backspace stays open and explains that Cancel quit keeps the previous task settings.");
            Click(window, Button(window, "Keep waiting")); await ctx.Driver.PumpAsync(100);
            Assert.True(Button(window, "Cancel quit").IsEnabled);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && b.Content?.ToString() == "Keep waiting");
            ctx.Snapshot(window, "keep-waiting-still-allows-cancel", "Keep waiting collapses the explanation but leaves Cancel quit visible. The rest of the app remains usable.");
            // Exercise navigation while the actual close operation is waiting.
            main.SelectedNav = NavItem.Logs;
            await ctx.Driver.PumpAsync(100);
            Assert.Equal(NavItem.Logs, main.SelectedNav);
            Click(window, Button(window, "Show details")); await ctx.Driver.PumpAsync(100);
            Assert.True(Button(window, "Keep waiting").IsEffectivelyVisible);
            Click(window, Button(window, "Cancel quit"));
            for (var n = 0; n < 60 && (FileAccessRetry.Snapshot().Any(x => x.Path == path) || ServiceLocator.Maintenance.IsBlocked); n++) await ctx.Driver.PumpAsync(50);
            Assert.DoesNotContain(FileAccessRetry.Snapshot(), x => x.Path == path);
            Assert.False(ServiceLocator.Maintenance.IsBlocked);
            Assert.True(window.IsVisible); Assert.False(App.QuitRequested);
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.True(tasks.TryBeginUnattended(out var allowed)); allowed!.Dispose();
            ctx.Snapshot(window, "cancel-quit-keeps-app-open", "Cancel quit removed the waiting notice. The window is still open and the previously saved task state remains unchanged.");
            held.Dispose();
            await CheckRestoreWaitAsync(ctx, window);
            Click(window, window.FindControl<Button>("CloseBtn")!);
            for (var n = 0; n < 60 && window.IsVisible; n++) await ctx.Driver.PumpAsync(50);
            Assert.False(window.IsVisible); Assert.True(App.QuitRequested);
            Assert.False(tasks.TryBeginUnattended(out _));
            ctx.Report.RecordNote("Checks and limits", "Actual Close, Keep waiting, Show details and Cancel quit buttons; actual Windows file handle; unlocked retry then closes and saves the pause. No task registration, reboot or Credential Manager access. Headless Avalonia does not establish native tray-menu interaction.");
        }
        finally
        {
            held.Dispose();
            foreach (var notice in FileAccessRetry.Snapshot().Where(x => x.Path == path)) notice.StopCommand.Execute(null);
            await ctx.Driver.PumpAsync(100);
            var admission = typeof(MainWindow).GetField("_shutdownAdmission", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            (admission!.GetValue(window) as IDisposable)?.Dispose();
            App.QuitRequested = true; window.Close();
        }
    }

    private static async Task CheckRestoreWaitAsync(ScenarioContext ctx, Window window)
    {
        var target = Path.Combine(ctx.RootDir, "restore-wait-target");
        Directory.CreateDirectory(target);
        var staged = Path.Combine(ctx.RootDir, "downloaded-restore.txt");
        var output = Path.Combine(target, "document.txt");
        File.WriteAllText(staged, "RESTORED"); File.WriteAllText(output, "ORIGINAL");
        using var held = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var stop = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
        var transaction = new RestoreOutputTransaction();
        var publish = Task.Run(() => transaction.Publish(staged, output, target, true, stop.Token));
        try
        {
            for (var n = 0; n < 200 && !FileAccessRetry.Snapshot().Any(x => x.Path == staged); n++) await ctx.Driver.PumpAsync(50);
            Assert.Single(FileAccessRetry.Snapshot(), x => x.Path == staged);
            await ctx.Driver.PumpAsync(100);
            Assert.True(Button(window, "Stop restore").IsEnabled);
            ctx.Snapshot(window, "restore-lock-offers-a-choice", "A real locked recovered file keeps restore waiting. The notice explains the delay and offers Keep waiting or Stop restore.");
            Click(window, Button(window, "Keep waiting")); await ctx.Driver.PumpAsync(100);
            Assert.False(publish.IsCompleted);
            Assert.True(Button(window, "Stop restore").IsEnabled);
            ctx.Snapshot(window, "restore-wait-can-still-be-stopped", "Keep waiting collapses the explanation without hiding Stop restore.");
            Click(window, Button(window, "Stop restore"));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publish.WaitAsync(TimeSpan.FromSeconds(3)));
            transaction.Rollback();
            Assert.Equal("ORIGINAL", File.ReadAllText(output));
            Assert.Equal("RESTORED", File.ReadAllText(staged));
            await ctx.Driver.PumpAsync(100);
            Assert.DoesNotContain(FileAccessRetry.Snapshot(), x => x.Path == staged);
        }
        finally
        {
            held.Dispose(); stop.Cancel();
            try { await publish; } catch (OperationCanceledException) { }
        }
    }

    private static Button Button(Window window, string label) => window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && b.Content?.ToString() == label);
    private static void Click(Window window, Control control)
    {
        window.UpdateLayout(); Screenshots.ScreenshotDiffTests.SettleInitialTransitions(window);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
    }
    private sealed class ScenarioSession : IInteractiveSessionReader
    {
        public bool Supported => true;
        public string AccountId => "S65-disposable-session";
        public InteractiveSessionIdentity? Current() => new(1, 1000, "Scenario account");
        public InteractiveSessionIdentity? Read(int id) => id == 1 ? Current() : null;
    }
}
