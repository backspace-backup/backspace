using System;
using System.Threading;
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
    public async Task Cleanup_closes_hidden_main_window_without_changing_user_close_behavior()
    {
        using var context = new ScenarioContext("Harness-main-window-cleanup", new SecretsLoader());
        await context.PrepareAsync();
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
    public async Task Cleanup_closes_untracked_owned_dialog_and_completes_its_modal_task()
    {
        using var context = new ScenarioContext("Harness-owned-dialog-cleanup", new SecretsLoader());
        await context.PrepareAsync();
        var owner = context.Driver.Show(new Window { Width = 300, Height = 200 });
        var dialog = new Window { Width = 200, Height = 100 };
        var closed = false; dialog.Closed += (_, _) => closed = true;
        var modal = dialog.ShowDialog(owner);
        context.Dispose();
        Assert.True(closed); Assert.True(modal.IsCompletedSuccessfully);
        Assert.False(owner.IsVisible);
    }

    [AvaloniaFact]
    public async Task Cleanup_rejects_active_restore_without_cancelling_its_backend()
    {
        using var context = new ScenarioContext("Harness-active-operation-cleanup", new SecretsLoader());
        await context.PrepareAsync();
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

    [AvaloniaFact]
    public async Task Preparation_waits_for_an_existing_journal_dismissal_before_the_next_window()
    {
        using var context = new ScenarioContext("Harness-result-dismissal", new SecretsLoader());
        await context.PrepareAsync();
        var backup = new Backup { Name = "Previous scenario result" };
        var session = ServiceLocator.ActiveRestores.BeginTest(backup, "Saved result", DateTime.UtcNow, null);
        ServiceLocator.ActiveRestores.End(session, RestoreTerminalStatus.Success, null);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.DismissAction = () => { entered.SetResult(); release.Wait(); };
        var dismiss = ServiceLocator.ActiveRestores.DismissAsync(session);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var preparation = context.PrepareAsync();
        try
        {
            await context.Driver.PumpAsync(100);
            Assert.False(preparation.IsCompleted);
            Assert.Contains(session, ServiceLocator.ActiveRestores.Sessions);
        }
        finally { release.Set(); }
        await preparation;
        await dismiss;
        Assert.Empty(ServiceLocator.ActiveRestores.Sessions);
    }

    [AvaloniaFact]
    public async Task Preparation_reports_failed_dismissal_instead_of_erasing_the_result()
    {
        using var context = new ScenarioContext("Harness-failed-dismissal", new SecretsLoader());
        await context.PrepareAsync();
        var backup = new Backup { Name = "Failed journal write" };
        var session = ServiceLocator.ActiveRestores.BeginTest(backup, "Saved result", DateTime.UtcNow, null);
        ServiceLocator.ActiveRestores.End(session, RestoreTerminalStatus.Success, null);
        session.DismissAction = () => throw new UnauthorizedAccessException("Fixture journal is not writable");
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.PrepareAsync());
            Assert.Contains("Fixture journal is not writable", error.Message);
            Assert.Contains(session, ServiceLocator.ActiveRestores.Sessions);
        }
        finally
        {
            session.DismissAction = null;
            await ServiceLocator.ActiveRestores.DismissAsync(session);
        }
    }
}
