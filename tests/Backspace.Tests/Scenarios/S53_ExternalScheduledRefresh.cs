using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

[Collection(ScenarioCollection.Name)]
public sealed class S53_ExternalScheduledRefresh : ScenarioBase
{
    protected override string ScenarioName => "S53-external-scheduled-refresh";
    [AvaloniaFact] public async Task Run_S53() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var backup = new Backup { Name = "Scheduled family archive", LastRunStatus = BackupRunStatus.Success,
            LastRunSummary = "Earlier backup completed", LastRunEndUtc = DateTime.UtcNow.AddHours(-2) };
        backup.SourcePaths.Add(ctx.SourcesDir);
        var destination = new Destination { Name = "Family archive drive", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("archive") };
        backup.Targets.Add(new BackupTarget { DestinationId = destination.Id, StorageName = "archive" });
        ServiceLocator.Config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(destination); });
        var oldRun = new RunRecord { BackupId = backup.Id, BackupName = backup.Name, Status = BackupRunStatus.Success,
            StartedUtc = DateTime.UtcNow.AddHours(-2), EndedUtc = DateTime.UtcNow.AddHours(-2).AddMinutes(1),
            Summary = "Earlier backup completed" };
        ServiceLocator.Logs.AppendRun(oldRun);

        var vm = new MainWindowViewModel();
        var window = ctx.Driver.Show(new MainWindow { DataContext = vm });
        await vm.RefreshExternalStateAsync();
        SelectNavigation("Activity & logs");
        await Until(() => vm.Logs.History.Any(r => r.Id == oldRun.Id));
        using var draft = new BackupEditorViewModel(backup, isNew: false)
        {
            Name = "Unsaved editor draft", SkipNotificationGraceDays = 42
        };

        // Independent store instances model a scheduler process: their events
        // do not reach the GUI's Config/LogStore subscribers.
        var externalConfig = new ConfigStore(); externalConfig.Load();
        var externalLogs = new LogStore();
        var failedLog = Path.Combine(ctx.RootDir, "scheduled-failure.log");
        File.WriteAllText(failedLog, "Scheduled run completed while Backspace was open.\nArchive access was denied.\n");
        var failedRun = new RunRecord { BackupId = backup.Id, BackupName = backup.Name, Status = BackupRunStatus.Failed,
            StartedUtc = DateTime.UtcNow.AddMinutes(-5), EndedUtc = DateTime.UtcNow.AddMinutes(-4),
            Summary = "Scheduled backup: archive access denied", LogPath = failedLog };
        externalLogs.AppendRun(failedRun);
        externalConfig.Update(c =>
        {
            var b = c.Backups.Single(b => b.Id == backup.Id);
            b.LastRunStatus = failedRun.Status; b.LastRunSummary = failedRun.Summary; b.LastRunEndUtc = failedRun.EndedUtc;
        });
        Assert.Equal(BackupRunStatus.Success, ServiceLocator.Config.Current.Backups.Single(b => b.Id == backup.Id).LastRunStatus);

        SelectNavigation("Backups");
        await Until(() => vm.Backups.Cards.Single(c => c.Id == backup.Id).StatusLabel == "Failed");
        Assert.Contains("archive access denied", vm.Backups.Cards.Single(c => c.Id == backup.Id).LastRunLabel);
        ctx.Snapshot(window, "backups-reopens-with-external-scheduled-result",
            "Returning through the real Backups sidebar control shows the failure written by an independent scheduler store, without restarting the app.");

        SelectNavigation("Activity & logs");
        await Until(() => vm.Logs.History.Any(r => r.Id == failedRun.Id));
        Assert.Equal(2, vm.Logs.History.Count);
        var picker = window.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => c.ItemsSource == vm.Logs.History);
        picker.SelectedItem = vm.Logs.History.Single(r => r.Id == failedRun.Id);
        await Until(() => vm.Logs.DisplayText.Contains("Archive access was denied."));
        ctx.Snapshot(window, "logs-reopens-with-external-scheduled-history",
            "The persistent Logs view reloads the externally appended run and displays its saved failure log.");

        // History-only writes must refresh even when config's content hash is
        // unchanged. Preserve the explicitly selected earlier failure.
        var newest = new RunRecord { BackupId = backup.Id, BackupName = backup.Name, Status = BackupRunStatus.Success,
            StartedUtc = DateTime.UtcNow.AddMinutes(-2), EndedUtc = DateTime.UtcNow.AddMinutes(-1), Summary = "Newer scheduled success" };
        externalLogs.AppendRun(newest);
        SelectNavigation("Backups"); SelectNavigation("Activity & logs");
        await Until(() => vm.Logs.History.Any(r => r.Id == newest.Id));
        Assert.Equal(failedRun.Id, vm.Logs.SelectedRun?.Id);

        // Actual Window.Activate must refresh as well, without a navigation
        // command or a direct call to the refresh method.
        var otherWindow = ctx.Driver.Show(new Window { Width = 240, Height = 120 });
        externalConfig.Update(c =>
        {
            var b = c.Backups.Single(b => b.Id == backup.Id);
            b.LastRunStatus = BackupRunStatus.Success; b.LastRunSummary = "Scheduled success while inactive";
        });
        window.Activate();
        await Until(() => ServiceLocator.Config.Current.Backups.Single(b => b.Id == backup.Id).LastRunSummary == "Scheduled success while inactive");
        otherWindow.Close();
        Assert.Equal("Unsaved editor draft", draft.Name);
        Assert.Equal(42, draft.SkipNotificationGraceDays);
        Assert.Equal(failedRun.Id, vm.Logs.SelectedRun?.Id);
        ctx.Report.RecordNote("scope of proof", "Uses independent file-backed ConfigStore and LogStore writers, actual sidebar controls and Window.Activate. No OS scheduled task is launched and no idle timer/file watcher is added. Unsaved editor fields and explicit failed-run selection survive refresh.");

        void SelectNavigation(string text)
        {
            var button = window.GetVisualDescendants().OfType<ToggleButton>()
                .Single(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text));
            button.IsChecked = true;
            ctx.Driver.Pump();
        }
        async Task Until(Func<bool> predicate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!predicate() && DateTime.UtcNow < deadline) await ctx.Driver.PumpAsync(30);
            Assert.True(predicate(), "External state did not appear after navigation/activation.");
        }
    }
}
