using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.ViewModels;

public sealed class ServiceHistorySelectionTests
{
    [AvaloniaFact]
    public async Task Downloaded_history_does_not_switch_the_selected_backup_run_or_live_view()
    {
        using var workspace = new TempWorkspace();
        var first = new Backup { Name = "Selected backup" };
        var other = new Backup { Name = "Other background backup" };
        ServiceLocator.Config.Update(c => { c.Backups.Clear(); c.Backups.Add(first); c.Backups.Add(other); });
        // The background service records runs in its history in the portable folder (U138).
        var installation = Path.Combine(workspace.Root, "portable");
        var service = new LogStore(Path.Combine(SystemService.DataDirectoryFor(installation), "logs", "engine"));
        var logs = LogStore.WithServiceOf(installation);
        var refreshed = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        int Count(string id) => refreshed.TryGetValue(id, out var n) ? n : 0;
        async Task Record(RunRecord run)
        {
            var before = Count(run.BackupId);
            service.AppendRun(run);
            for (var n = 0; n < 150 && Count(run.BackupId) == before; n++)
            { logs.RefreshBackgroundHistory(run.BackupId); await Task.Delay(20); }
            Assert.NotEqual(before, Count(run.BackupId));
        }
        var previous = ServiceLocator.Logs;
        var property = typeof(ServiceLocator).GetProperty(nameof(ServiceLocator.Logs))!;
        property.SetValue(null, logs);
        try
        {
            var saved = new RunRecord { BackupId = first.Id, BackupName = first.Name, StartedUtc = DateTime.UtcNow.AddHours(-2), EndedUtc = DateTime.UtcNow.AddHours(-1), Summary = "Previously selected" };
            logs.AppendRun(saved);
            using var vm = new LogsViewModel();
            // After the view's own handler, which posts its reload first.
            logs.HistoryRefreshed += id => refreshed.AddOrUpdate(id, 1, (_, n) => n + 1);
            vm.SelectedBackup = first.Name;
            vm.SelectedRun = vm.History.Single(r => r.Id == saved.Id);
            await Record(new() { BackupId = other.Id, BackupName = other.Name, StartedUtc = DateTime.UtcNow, Summary = "Other result" });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(first.Name, vm.SelectedBackup);
            Assert.Equal(saved.Id, vm.SelectedRun?.Id);

            await Record(new() { BackupId = first.Id, BackupName = first.Name, StartedUtc = DateTime.UtcNow, Summary = "New result" });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(saved.Id, vm.SelectedRun?.Id);
            Assert.Contains(vm.History, r => r.Summary == "New result");

            vm.SelectedRun = null;
            await Record(new() { BackupId = first.Id, BackupName = first.Name, StartedUtc = DateTime.UtcNow.AddSeconds(1), Summary = "Another result" });
            Dispatcher.UIThread.RunJobs();
            Assert.Null(vm.SelectedRun);
            Assert.True(vm.IsShowingLive);
        }
        finally { property.SetValue(null, previous); }
    }

    [Fact]
    public void Open_history_refresh_keeps_its_filter_and_updates_the_empty_state()
    {
        var backup = new Backup();
        var history = new BackupHistoryViewModel(backup, Array.Empty<RunRecord>(), checks: false);
        history.SelectedFilter = "All activity";
        Assert.True(history.IsEmpty);
        history.UpdateRuns(new[] { new RunRecord { BackupId = backup.Id, Kind = RunKind.Check, Summary = "Repository check finished" } });
        Assert.Equal("All activity", history.SelectedFilter);
        Assert.False(history.IsEmpty);
        Assert.Equal("Repository check finished", history.Rows.Single().Summary);
    }
}
