using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Backspace.Services;
using Backspace.Tests.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// U238 (owner, 13 November 2026): "I hit Download and restart but it doesn't
/// do anything because a backup is running ... make it clear if it can't
/// download and restart why with more visual feedback. If issue detected then
/// maybe offer in a popup to stop ongoing things". The update says what runs
/// and asks: update when it finishes (the default: Backspace restarts by
/// itself then), stop it and update now, or not now.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S109_UpdateWhileWorkRuns : ScenarioBase
{
    protected override string ScenarioName => "S109-update-while-work-runs";
    [AvaloniaFact] public Task Run_S109() => Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        App.QuitRequested = false;
        var running = new List<string> { "«local_c» backup in the background" };
        var restarts = 0;
        var restartAllowed = false;
        using var http = new HttpClient(new WindowsUpdateTests.ReplyHandler(WindowsUpdateTests.ReleaseJson()));
        using var updates = new WindowsUpdateViewModel(http, _ => { restarts++; return restartAllowed; }, true);
        var asked = new List<(IReadOnlyList<string> Running, string Version)>();
        var choice = WindowsUpdateViewModel.BusyChoice.WhenFinished;
        var stops = 0;
        updates.RunningWork = () => running;
        updates.AskWhenBusy = (r, v) => { asked.Add((r, v)); return Task.FromResult(choice); };
        updates.StopWork = () => { stops++; running.Clear(); restartAllowed = true; return Task.CompletedTask; };
        var poll = WindowsUpdateViewModel.WaitPoll;
        WindowsUpdateViewModel.WaitPoll = TimeSpan.FromMilliseconds(100);
        try
        {
            using var settings = new SettingsViewModel(updates);
            var view = new SettingsView { DataContext = settings };
            var window = ctx.Driver.Show(new Window { Width = 920, Height = 760, Content = view, RequestedThemeVariant = ThemeVariant.Light });
            await ctx.Driver.PumpAsync(100);
            await updates.CheckNowCommand.ExecuteAsync(null);
            Assert.True(updates.HasUpdate);
            updates.UsePrepared(new WindowsUpdatePackage.Plan());

            // A backup runs: asked, then it waits for it, saying so in amber.
            await updates.RestartWhenPossibleAsync("99.0.0");
            var question = Assert.Single(asked);
            Assert.Equal("«local_c» backup in the background", Assert.Single(question.Running));
            Assert.True(updates.IsWaitingForWork);
            Assert.True(updates.StatusIsWarning);
            Assert.Equal("Waiting for «local_c» backup in the background to finish. Backspace then restarts to 99.0.0 by itself.", updates.Status);
            Assert.False(updates.CanDownload);
            var card = view.FindControl<Border>("AppUpdatesCard")!;
            card.BringIntoView(); await ctx.Driver.PumpAsync(200);
            Assert.True(view.FindControl<Border>("UpdateStatus")!.Classes.Contains("warn"));
            ctx.Snapshot(window, "waiting for the backup",
                "Settings › App updates: an amber notice \"Waiting for «local_c» backup in the background to finish. Backspace then restarts to 99.0.0 by itself.\" with a moving bar, Stop it now and update, and Don't wait.");

            // The backup ends: it restarts by itself.
            running.Clear(); restartAllowed = true;
            Assert.True(ctx.Driver.WaitFor(() => !updates.IsWaitingForWork, 5000), "It did not restart once the backup ended.");
            var before = restarts;
            await Task.Delay(400);
            Assert.Equal(before, restarts); // stopped asking once restarted

            // Stop it and update now: stops the work, then restarts.
            running.Add("«local_d» check in the background"); restartAllowed = false; asked.Clear(); restarts = 0;
            choice = WindowsUpdateViewModel.BusyChoice.StopNow;
            await updates.RestartWhenPossibleAsync("99.0.0");
            Assert.Equal(1, stops);
            Assert.Equal(2, restarts);

            // Not now: nothing waits; it says what still runs.
            running.Add("«local_c» backup"); restartAllowed = false; choice = WindowsUpdateViewModel.BusyChoice.NotNow;
            await updates.RestartWhenPossibleAsync("99.0.0");
            Assert.False(updates.IsWaitingForWork);
            Assert.Equal("Backspace 99.0.0 is downloaded. «local_c» backup is still running: choose Download and restart when you're ready; it won't download again.", updates.Status);

            // The question itself.
            var dialog = new DriveChoiceWindow("Update to 99.0.0 when it finishes?",
                "Running now: «local_c» backup in the background. Backspace can restart to 99.0.0 by itself as soon as it finishes, or stop it now. A stopped backup runs again at its next time and only sends what changed.",
                "Update when it finishes", "Stop it and update now");
            ctx.Driver.Show(dialog);
            await ctx.Driver.PumpAsync(150);
            ctx.Snapshot(dialog, "the question", "Asked when Download and restart meets a running backup: Update when it finishes (default), Stop it and update now, Not now.");
            dialog.Close();
        }
        finally { WindowsUpdateViewModel.WaitPoll = poll; }
    }
}
