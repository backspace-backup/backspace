using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

[Collection(ScenarioCollection.Name)]
public sealed class UiDriverCleanupTests
{
    [AvaloniaFact]
    public void Cleanup_closes_hidden_main_window_without_changing_user_close_behavior()
    {
        using var context = new ScenarioContext("Harness-main-window-cleanup", new SecretsLoader());
        var priorTray = ServiceLocator.Config.Current.Preferences.MinimizeToTrayOnClose;
        var priorShowTray = ServiceLocator.Config.Current.Preferences.ShowTrayIcon;
        var priorQuit = Backspace.App.QuitRequested;
        try
        {
            ServiceLocator.Config.Update(c => { c.Preferences.MinimizeToTrayOnClose = true; c.Preferences.ShowTrayIcon = true; });
            Backspace.App.QuitRequested = false;
            var window = context.Driver.Show(new MainWindow { DataContext = new MainWindowViewModel() });
            var closed = false; window.Closed += (_, _) => closed = true;
            window.Close(); context.Driver.Pump();
            Assert.False(window.IsVisible); Assert.False(closed); // User action still minimizes to tray.
            context.Dispose();
            Assert.True(closed); // Teardown actually releases window subscriptions/resources.
            Assert.False(Backspace.App.QuitRequested);
            context.Driver.Dispose(); // Idempotent.
        }
        finally
        {
            Backspace.App.QuitRequested = priorQuit;
            ServiceLocator.Config.Update(c => { c.Preferences.MinimizeToTrayOnClose = priorTray; c.Preferences.ShowTrayIcon = priorShowTray; });
        }
    }

    [AvaloniaFact]
    public void Cleanup_closes_untracked_owned_dialog_and_completes_its_modal_task()
    {
        using var context = new ScenarioContext("Harness-owned-dialog-cleanup", new SecretsLoader());
        var owner = context.Driver.Show(new Window { Width = 300, Height = 200 });
        var dialog = new Window { Width = 200, Height = 100 };
        var closed = false; dialog.Closed += (_, _) => closed = true;
        var modal = dialog.ShowDialog(owner);
        context.Dispose();
        Assert.True(closed); Assert.True(modal.IsCompletedSuccessfully);
        Assert.False(owner.IsVisible);
    }

    [AvaloniaFact]
    public void Cleanup_rejects_active_restore_without_cancelling_its_backend()
    {
        using var context = new ScenarioContext("Harness-active-operation-cleanup", new SecretsLoader());
        var backup = new Backup { Name = "cleanup ownership fixture" };
        var cancelCalls = 0;
        var session = ServiceLocator.ActiveRestores.Begin(backup, new RestoreRequest
        {
            Backup = backup, Destination = new Destination(), SourcePath = context.SourcesDir,
            StorageName = "default", Revision = 1, TargetPath = context.RestoredDir
        }, () => cancelCalls++);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => context.Driver.Dispose());
            Assert.Contains("teardown will not terminate a backend", exception.Message);
            Assert.Equal(0, cancelCalls); Assert.True(ServiceLocator.ActiveRestores.HasRunning);
        }
        finally
        {
            // The scenario, not UiDriver, acknowledges operation completion.
            ServiceLocator.ActiveRestores.End(session, RestoreTerminalStatus.Cancelled, "fixture completed");
            ServiceLocator.ActiveRestores.Dismiss(session);
            context.Driver.Pump();
            context.Dispose();
        }
    }
}
