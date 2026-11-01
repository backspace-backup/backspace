using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Backspace.Services;
using Backspace.Tests.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

[Collection(ScenarioCollection.Name)]
public sealed class S62_WindowsUpdates : ScenarioBase
{
    protected override string ScenarioName => "S62-windows-updates";
    [AvaloniaFact] public Task Run_S62() => Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        App.QuitRequested = false;
        using var http = new HttpClient(new WindowsUpdateTests.ReplyHandler(WindowsUpdateTests.ReleaseJson()));
        using var updates = new WindowsUpdateViewModel(http, _ => throw new Exception("No restart expected"), true);
        using var settings = new SettingsViewModel(updates);
        var view = new SettingsView { DataContext = settings };
        var window = ctx.Driver.Show(new Window { Width = 920, Height = 740, Content = view,
            RequestedThemeVariant = ThemeVariant.Light });
        await Settle();
        var card = view.FindControl<Border>("AppUpdatesCard")!;
        card.BringIntoView(); await Settle();
        Click(updates.CheckNowCommand);
        for (var i = 0; i < 100 && updates.IsBusy; i++) await Settle();
        Assert.True(updates.HasUpdate);
        Assert.Equal("Update to 99.0.0", updates.AvailableVersion);
        Assert.True(updates.CanDownload);
        card.BringIntoView(); await Settle();
        ctx.Snapshot(window, "available-update-light", "Settings shows the current version, automatic-check frequency, release notes, and explicit download/restart and skip choices. GitHub response is simulated.");
        Click(updates.SkipVersionCommand); await Settle();
        Assert.Equal("99.0.0", ServiceLocator.Config.Current.Preferences.SkippedUpdateVersion);
        Assert.Contains("Skipped version", updates.Status);
        Assert.True(updates.HasUpdate); // Skipping notifications must not remove manual installation.
        var frequency = card.GetVisualDescendants().OfType<ComboBox>().Single();
        frequency.Focus();
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        await Settle();
        Assert.Equal("Never", ServiceLocator.Config.Current.Preferences.UpdateCheckFrequency);
        window.RequestedThemeVariant = ThemeVariant.Dark;
        await Settle(); card.BringIntoView(); await Settle();
        ctx.Snapshot(window, "skipped-update-dark", "Skipping a version keeps manual installation available. Automatic checks can be disabled, without disabling Check now.");
        Assert.True(updates.CheckNowCommand.CanExecute(null));
        var main = ctx.Driver.Show(new MainWindow());
        for (var i = 0; i < 40 && MainWindow.HasAnyRunningOperation(); i++) await Settle();
        Assert.False(MainWindow.HasAnyRunningOperation());
        bool prepared = false, cancelled = false;
        using var operationTimeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
        using (await ServiceLocator.Maintenance.AcquireReadAsync(operationTimeout.Token, () => cancelled = true))
        {
            Assert.False(main.TryQuitForUpdate(() => prepared = true));
            Assert.False(prepared); Assert.False(cancelled);
            Assert.False(ServiceLocator.Maintenance.IsShuttingDown);
        }
        Assert.Throws<InvalidOperationException>(() => main.TryQuitForUpdate(() => throw new InvalidOperationException("Preparation failed")));
        Assert.False(ServiceLocator.Maintenance.IsShuttingDown);
        Assert.False(App.QuitRequested);
        using (await ServiceLocator.Maintenance.AcquireReadAsync(operationTimeout.Token)) { }
        ctx.Report.RecordNote("Scope", "Actual Settings controls with pointer clicks on Check now and Skip, plus keyboard frequency selection. Simulated GitHub HTTP response; this does not prove a published release download, Windows replacement, restart, or SmartScreen behavior.");

        void Click(System.Windows.Input.ICommand command)
        {
            var button = card.GetVisualDescendants().OfType<Button>().Single(b => b.Command == command);
            button.BringIntoView(); window.UpdateLayout();
            var p = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseDown(p, MouseButton.Left); window.MouseUp(p, MouseButton.Left);
        }
        async Task Settle()
        {
            for (var i = 0; i < 8; i++) { await Task.Delay(16); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); ctx.Driver.Pump(); }
        }
    }
}
