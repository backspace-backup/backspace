using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

[Collection(ScenarioCollection.Name)]
public sealed class S71_BackgroundRunLogs : ScenarioBase
{
    protected override string ScenarioName => "S71-background-run-logs";
    [AvaloniaFact] public async Task Run_S71() => await Run();
    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var backup = new Backup { Name = "Documents" };
        ServiceLocator.Config.Update(c => c.Backups.Add(backup));
        // A background run names its log in the portable folder; the app reads it like its own (U138).
        string Log(string name, string text) { var path = Path.Combine(ctx.RootDir, name); File.WriteAllText(path, text); return path; }
        var first = new RunRecord { BackupId = backup.Id, BackupName = backup.Name, StartedUtc = DateTime.UtcNow.AddHours(-2), EndedUtc = DateTime.UtcNow.AddHours(-2).AddSeconds(5), Status = BackupRunStatus.Success,
            LogPath = Log("first.log", "Backup finished successfully.\nThis is the older background run.") };
        var second = new RunRecord { BackupId = backup.Id, BackupName = backup.Name, StartedUtc = DateTime.UtcNow.AddHours(-1), EndedUtc = DateTime.UtcNow.AddHours(-1).AddSeconds(5), Status = BackupRunStatus.Success,
            LogPath = Log("second.log", "Backup finished successfully.\n100 KB saved to Backup drive.\nThis is the newer background run.") };
        ServiceLocator.Logs.AppendRun(first); ServiceLocator.Logs.AppendRun(second);
        ServiceActivity.Snapshot? activity = null;
        using var monitor = new BackgroundServiceActivity(_ => Task.FromResult(activity), (_, _) => Task.CompletedTask);
        using var vm = new LogsViewModel(monitor);
        var view = new LogsView { DataContext = vm };
        var window = ctx.Driver.Show(new Window { Width = 1024, Height = 768, Content = new Border { Padding = new Thickness(0, 24, 0, 0), Child = view }, Title = "Backspace — Activity & logs" });
        await ctx.Driver.PumpAsync(150);
        // Logs outlive each scenario's config reset; an earlier scenario's
        // "[Destinations]" history sorts first and would be selected instead.
        vm.SelectedBackup = backup.Name;
        await ctx.Driver.PumpAsync(50);
        var picker = view.FindControl<ComboBox>("RunPicker")!;
        picker.SelectedItem = vm.History.Single(r => r.Id == first.Id);
        for (var n = 0; n < 50 && !vm.DisplayText.Contains("older background"); n++) await ctx.Driver.PumpAsync(20);
        Assert.Contains("older background", vm.DisplayText);
        picker.SelectedItem = vm.History.Single(r => r.Id == second.Id);
        for (var n = 0; n < 50 && !vm.DisplayText.Contains("newer background"); n++) await ctx.Driver.PumpAsync(20);
        Assert.Contains("newer background", vm.DisplayText); Assert.DoesNotContain("older background", vm.DisplayText);
        ctx.Snapshot(window, "saved-background-log", "The selected completed background run shows its own log, read from the portable folder; the run selected before it no longer shows.");
        var runId = Guid.NewGuid().ToString("N");
        activity = new(1, Guid.NewGuid().ToString("N"), "running", backup.Id, "backup", DateTime.UtcNow,
            new(backup.Id, 42, "", [], [], TranscriptTail: "Scanning Documents…\n42 of 100 files backed up.\nBackground backup is running.", LogRunId: runId));
        activity = activity with { Progress = activity.Progress! with { TranscriptEnd = activity.Progress!.TranscriptTail.Length } };
        await monitor.RefreshAsync(CancellationToken.None); await ctx.Driver.PumpAsync(100);
        Assert.Equal(second.Id, vm.SelectedRun!.Id); // New activity must not steal a past selection.
        // Opening Activity & logs while it runs shows the running run, not
        // the one before it (4 November 2026, the owner's installation).
        vm.ShowRunningRunIfAny(); await ctx.Driver.PumpAsync(50);
        Assert.Equal(runId, vm.SelectedRun!.Id);
        picker.SelectedItem = vm.History.Single(r => r.Id == runId); await ctx.Driver.PumpAsync(100);
        Assert.True(vm.IsShowingLive); Assert.Contains("42 of 100", vm.DisplayText);
        ctx.Snapshot(window, "live-background-log", "The running background backup appears in the run list and its live output reaches the console without starting a local backup.");
        var clear = view.FindControl<Button>("ClearLog")!;
        var point = clear.TranslatePoint(new Point(clear.Bounds.Width / 2, clear.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
        await monitor.RefreshAsync(CancellationToken.None); await ctx.Driver.PumpAsync(100);
        Assert.Empty(vm.DisplayText); // Polling the same tail cannot undo Clear view.
        var next = activity.Progress.TranscriptTail + "\nNew output after clearing.";
        activity = activity with { Progress = activity.Progress with { TranscriptTail = next, TranscriptEnd = next.Length } };
        await monitor.RefreshAsync(CancellationToken.None); await ctx.Driver.PumpAsync(100);
        Assert.Contains("New output after clearing", vm.DisplayText); Assert.DoesNotContain("42 of 100", vm.DisplayText);
        activity = activity with { State = "interrupted", Progress = null };
        await monitor.RefreshAsync(CancellationToken.None); await ctx.Driver.PumpAsync(100);
        Assert.Equal(runId, vm.SelectedRun!.Id); Assert.Equal(BackupRunStatus.Failed, vm.SelectedRun.Status);
        Assert.Contains("not confirmed", vm.SelectedRun.Summary);
        picker.SelectedItem = vm.History.Single(r => r.Id == second.Id);
        for (var n = 0; n < 50 && !vm.DisplayText.Contains("newer background"); n++) await ctx.Driver.PumpAsync(20);
        Assert.Contains("newer background", vm.DisplayText); Assert.Equal(BackupRunStatus.Success, vm.SelectedRun!.Status);
        ctx.Report.RecordNote("Limits", "Actual headless Logs view with controlled service activity and selections through its bound run picker. Native dropdown interaction requires separate checks.");
    }
}
