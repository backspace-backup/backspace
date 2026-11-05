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
/// service said "Detailed log unavailable", because the service sends no
/// paths. U139: hovering a row lit a square around the rounded card.</summary>
[Collection(ScenarioCollection.Name)]
public sealed class S84_BackgroundRunLogsInHistory : ScenarioBase
{
    protected override string ScenarioName => "S84-background-run-logs-in-history";
    [AvaloniaFact] public async Task Run_S84() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var now = DateTime.UtcNow;
        var backup = new Backup { Name = "local_c" };
        var serviceLogs = Path.Combine(ctx.RootDir, "service", "logs", "engine", "local_c");
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
        // The real service side: its history protocol, then the desktop's cache.
        var protocol = new ServiceHistoryProtocol(_ => records.ToList());
        var reply = JsonSerializer.Deserialize<ServiceHistoryProtocol.Response>(
            protocol.Dispatch(JsonSerializer.SerializeToUtf8Bytes(new ServiceHistoryProtocol.Request(1, backup.Id), ServiceRunAuthorization.Json)),
            ServiceRunAuthorization.Json)!;
        Assert.All(reply.Runs, r => Assert.Equal("", r.LogPath));
        var cache = new ServiceHistoryCache(Path.Combine(ctx.RootDir, "service-history"), (_, _) => Task.FromResult(reply));
        await cache.RefreshAsync(backup.Id, CancellationToken.None);
        var vm = new BackupHistoryViewModel(backup, cache.ReadCached(backup.Id), checks: false) { SelectedFilter = "All activity" };
        Assert.Equal(new[] { true, true, false }, vm.Rows.Select(r => r.HasLog));

        var window = ctx.Driver.Show(new BackupHistoryWindow { DataContext = vm, Width = 800, Height = 680 });
        await ctx.Driver.PumpAsync(200);
        Assert.Empty(window.GetVisualDescendants().OfType<ListBox>()); // rows are not selectable
        var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Content as string == "Open log").ToList();
        Assert.Equal(2, buttons.Count);
        var notes = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Text?.StartsWith("Detailed log unavailable") == true).ToList();
        Assert.Single(notes);
        // U142: what each backup added, read by the service from its own log for runs saved before.
        Assert.Equal(2, window.GetVisualDescendants().OfType<TextBlock>().Count(t => t.IsEffectivelyVisible && t.Text == "14,145 new · 239 changed · 12 GB added"));

        var row = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("history-row"));
        var before = row.BorderBrush;
        var centre = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height - 6), window)!.Value;
        window.MouseMove(centre);
        await ctx.Driver.PumpAsync(100);
        Assert.NotEqual(before?.ToString(), row.BorderBrush?.ToString());
        ctx.Snapshot(window, "background-runs-offer-their-log",
            "All activity for local_c with three background-service runs. The two whose log the service still keeps show Open log and what they added (14,145 new · 239 changed · 12 GB added, read from the run's own log); only the interrupted run, which has no log, says Detailed log unavailable. The pointer is over the first card: its own rounded outline brightens, with no square highlight around it.");
        ctx.Report.RecordNote("Limits", "Real ServiceHistoryProtocol and desktop ServiceHistoryCache in headless Avalonia; the named pipe between them is replaced by a direct call. Opening the log goes through the existing transcript pipe, which this scenario does not exercise.");
    }
}
