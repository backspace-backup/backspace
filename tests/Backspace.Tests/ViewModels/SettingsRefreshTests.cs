using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.ViewModels;

public sealed class SettingsRefreshTests
{
    [AvaloniaFact]
    public async Task LoadingAndRefreshingSettingsDoNotWriteBackButUserEditsDo()
    {
        ServiceLocator.Config.Update(c =>
        {
            c.Preferences.Accent = AccentColor.Plum;
            c.Logging.MinimumLevel = "Debug";
            c.Logging.RetentionDays = 19;
        });
        var writes = 0;
        EventHandler changed = (_, _) => writes++;
        ServiceLocator.Config.Changed += changed;
        try
        {
            using var vm = new SettingsViewModel();
            Assert.Equal(0, writes);
            Assert.True(vm.LogsVerbose);
            Assert.Equal("Debug", ServiceLocator.Config.Current.Logging.MinimumLevel);

            // Emulate another process/config reload while this view is open.
            await Task.Run(() => ServiceLocator.Config.Update(c =>
            {
                c.Preferences.Accent = AccentColor.Emerald;
                c.Logging.MinimumLevel = "Information";
                c.Logging.RetentionDays = 31;
            }));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, writes); // Only the deliberate update, no feedback write.
            Assert.False(vm.LogsVerbose);
            Assert.Equal(AccentColor.Emerald, vm.Accent);
            Assert.Equal(31, vm.LogRetentionDays);

            // The guard must not disable intentional edits from the controls.
            vm.LogsVerbose = true;
            vm.Accent = AccentColor.Ocean;
            vm.LogRetentionDays = 45;
            Assert.Equal(4, writes);
            Assert.Equal("Verbose", ServiceLocator.Config.Current.Logging.MinimumLevel);
            Assert.Equal(AccentColor.Ocean, ServiceLocator.Config.Current.Preferences.Accent);
            Assert.Equal(45, ServiceLocator.Config.Current.Logging.RetentionDays);

            // A queued refresh must not touch a screen that has since closed.
            // A blocked Task.Run can execute inline on the waiting UI thread.
            // A dedicated worker makes the queued-refresh starting state real.
            Assert.True(Dispatcher.UIThread.CheckAccess());
            var worker = new Thread(() => ServiceLocator.Config.Update(c => c.Logging.MinimumLevel = "Information"));
            worker.Start();
            worker.Join();
            Assert.True(vm.LogsVerbose); // The UI has not processed the queued refresh.
            vm.Dispose();
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.LogsVerbose);
            Assert.Equal(5, writes);
            Assert.Equal("Information", ServiceLocator.Config.Current.Logging.MinimumLevel);
        }
        finally { ServiceLocator.Config.Changed -= changed; }
    }
}
