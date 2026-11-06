using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>U138: in a backup's activity, every run made by the background
/// service said "Detailed log unavailable". The app now reads the service's
/// history and logs from the portable folder. U139: hovering a row lit a
/// square around the rounded card.</summary>
[Collection(ScenarioCollection.Name)]
public sealed class S84_BackgroundRunLogsInHistory : ScenarioBase
{
    protected override string ScenarioName => "S84-background-run-logs-in-history";
    [AvaloniaFact] public async Task Run_S84() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var now = DateTime.UtcNow;
        var backup = new Backup { Name = "local_c" };
        var installation = Path.Combine(ctx.RootDir, "portable");
        var engineLogs = Path.Combine(SystemService.DataDirectoryFor(installation), "logs", "engine");
        var serviceLogs = Path.Combine(engineLogs, "local_c");
        Directory.CreateDirectory(serviceLogs);
        RunRecord Run(int hoursAgo, BackupRunStatus status, string summary, string? log)
        {
            string path = "";
            if (log is not null) { path = Path.Combine(serviceLogs, log); File.WriteAllText(path, "[recap] Backup complete: 601,922 files (190 GB) checked · 14,145 new, 239 changed, 587,538 unchanged · 12 GB added to the storage · took 59m 40s · snapshot a4042f53"); }
            return new RunRecord { BackupId = backup.Id, BackupName = backup.Name, StartedUtc = now.AddHours(-hoursAgo), EndedUtc = now.AddHours(-hoursAgo).AddMinutes(20), Status = status, Summary = summary, LogPath = path };
        }
        var records = new[]
        {
            Run(1, BackupRunStatus.Success, "Completed in 24m 51s", "20261105-060000.log"),
            Run(5, BackupRunStatus.Success, "Completed in 1h 31m", "20261104-140400.log"),
            Run(7, BackupRunStatus.Skipped, "Interrupted after 21s", null),
        };
        // The service records them in its history in the portable folder; the app reads it as it is.
        var service = new LogStore(engineLogs);
        foreach (var record in records) service.AppendRun(record);
        var runs = LogStore.WithServiceOf(installation).LoadTimelineByBackupId(backup.Id);
        Assert.Equal(3, runs.Count);
        var vm = new BackupHistoryViewModel(backup, runs, checks: false) { SelectedFilter = "All activity" };
        Assert.Equal(new[] { true, true, false }, vm.Rows.Select(r => r.HasLog));

        var window = ctx.Driver.Show(new BackupHistoryWindow { DataContext = vm, Width = 800, Height = 680 });
        await ctx.Driver.PumpAsync(200);
        Assert.Empty(window.GetVisualDescendants().OfType<ListBox>()); // rows are not selectable
        var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Content as string == "Open log").ToList();
        Assert.Equal(2, buttons.Count);
        var notes = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Text?.StartsWith("Detailed log unavailable") == true).ToList();
        Assert.Single(notes);
        // U142: what each backup added, read from the run's own log for runs saved before.
        Assert.Equal(2, window.GetVisualDescendants().OfType<TextBlock>().Count(t => t.IsEffectivelyVisible && t.Text == "14,145 new · 239 changed · 12 GB added"));

        var row = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("history-row"));
        var before = row.BorderBrush;
        var centre = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height - 6), window)!.Value;
        window.MouseMove(centre);
        await ctx.Driver.PumpAsync(100);
        Assert.NotEqual(before?.ToString(), row.BorderBrush?.ToString());
        ctx.Snapshot(window, "background-runs-offer-their-log",
            "All activity for local_c with three background-service runs, read from the service's history in the portable folder. The two with a log show Open log and what they added (14,145 new · 239 changed · 12 GB added, read from the run's own log); only the interrupted run, which has no log, says Detailed log unavailable. The pointer is over the first card: its own rounded outline brightens, with no square highlight around it.");
        ctx.Report.RecordNote("Limits", "The service's real history and log files in a portable folder, read by LogStore, in headless Avalonia. Opening a log reads the file like the app's own runs.");
    }
}
