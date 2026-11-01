using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>U043 (31 October 2026): Backspace starts with Windows in the tray,
/// like Dropbox, and keeps its schedule while it runs. Settings → Startup and
/// tray turns the sign-in start on and off and says what Quit does. The test
/// entry stands in for Windows' Run key.</summary>
[Collection(ScenarioCollection.Name)]
public sealed class S74_StartupAndTray : ScenarioBase
{
    protected override string ScenarioName => "S74-startup-and-tray";

    [AvaloniaFact] public async Task Run_S74() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        var entry = RecordingSignInStart.Shared;
        var previousEntry = entry.Executable;
        var previousPreference = ServiceLocator.Config.Current.Preferences.StartAtSignIn;
        var executable = Environment.ProcessPath!;
        try
        {
            ServiceLocator.Config.Update(c => c.Preferences.StartAtSignIn = true);
            entry.Executable = executable;
            var vm = new SettingsViewModel();
            var view = new SettingsView { DataContext = vm };
            var host = new Window { Width = 1100, Height = 900, Content = view };
            ctx.Driver.Show(host);
            var section = host.GetVisualDescendants().OfType<Expander>()
                .Single(e => e.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Startup and tray"));
            section.IsExpanded = true;
            ctx.Driver.Pump();
            var box = host.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "StartAtSignInBox");
            section.BringIntoView(); ctx.Driver.Pump();
            Assert.True(box.IsChecked);
            Assert.Equal("Scheduled backups run while Backspace is running, so it starts when you sign in.", vm.StartAtSignInDetail);
            Assert.Equal("Quit stops automatic backups until Backspace starts again.", vm.QuitBehaviorTitle);
            Assert.StartsWith("Backspace starts again when you next sign in", vm.QuitBehaviorExplanation);
            ctx.Snapshot(host, "starts at sign-in",
                "Startup and tray: 'Start Backspace when I sign in' is ticked and says schedules run while Backspace runs. " +
                "Below, closing to the tray keeps them running, and Quit stops them until Backspace starts again at the next sign-in.");

            box.IsChecked = false; ctx.Driver.Pump();
            Assert.Null(entry.Executable);
            Assert.False(ServiceLocator.Config.Current.Preferences.StartAtSignIn);
            Assert.Equal("Scheduled backups run only after you open Backspace.", vm.StartAtSignInDetail);
            Assert.Equal("Nothing runs until you open Backspace again.", vm.QuitBehaviorExplanation);
            ctx.Snapshot(host, "sign-in start turned off",
                "Unticked: the sign-in entry is removed; both notes now say nothing runs until you open Backspace.");

            box.IsChecked = true; ctx.Driver.Pump();
            Assert.Equal(executable, entry.Executable);
            host.Close();

            // Another portable copy owns the entry: say so, never replace it silently.
            var other = typeof(S74_StartupAndTray).Assembly.Location;
            entry.Executable = other;
            var second = new SettingsViewModel();
            var secondHost = new Window { Width = 1100, Height = 900, Content = new SettingsView { DataContext = second } };
            ctx.Driver.Show(secondHost);
            var secondSection = secondHost.GetVisualDescendants().OfType<Expander>()
                .Single(e => e.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Startup and tray"));
            secondSection.IsExpanded = true; ctx.Driver.Pump();
            secondSection.BringIntoView(); ctx.Driver.Pump();
            Assert.StartsWith("Another copy of Backspace starts when you sign in", second.StartAtSignInDetail);
            Assert.Equal(other, entry.Executable);
            ctx.Snapshot(secondHost, "another copy starts at sign-in",
                "Another copy is registered: the note names it and explains how to start this copy instead. Nothing was replaced.");
            secondHost.Close();
            second.Dispose();
            vm.Dispose();
        }
        finally
        {
            entry.Executable = previousEntry;
            ServiceLocator.Config.Update(c => c.Preferences.StartAtSignIn = previousPreference);
        }
        return Task.CompletedTask;
    }
}
