using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// U105 (as redesigned on 10 November 2026): after approving updated suggestions,
/// the caches group says it changes when you save, and the page and footer say
/// nothing is saved yet; a tick writes at once. U106: a backup that never ran shows
/// each source's size estimated from the disk, and a measured size once a
/// backup ran. U113: the Verbose switch beside Clear view shows the engine's
/// debug lines, hidden by default, for lines already shown too.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S79_RecommendedDoneSizeAndVerbose : ScenarioBase
{
    protected override string ScenarioName => "S79-recommended-done-size-and-verbose";

    [AvaloniaFact] public async Task Run_S79() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        await RecommendedDone(ctx);
        await SizesAndVerbose(ctx);
    }

    private static Button? Button(Window win, string text) => win.GetVisualDescendants().OfType<Button>()
        .FirstOrDefault(b => b.Content as string == text);

    private static async Task RecommendedDone(ScenarioContext ctx)
    {
        // A backup saved by an earlier version: its caches group lacks today's rules.
        var source = ctx.MakeSource("profile", targetBytes: 20_000);
        Directory.CreateDirectory(Path.Combine(source, "node_modules"));
        File.WriteAllBytes(Path.Combine(source, "node_modules", "scratch.bin"), new byte[3_000_000]);
        var backup = new Backup
        {
            Name = "local_c",
            FiltersText = string.Join("\n", RecommendedFilters.AutoBlockHeader,
                "# Essentials: always excluded; safe for any source.", "/pagefile.sys", "",
                "# App caches, temp files, dev junk: universally safe to skip.", "node_modules/", "",
                RecommendedFilters.AutoBlockFooter),
        };
        backup.SourcePaths.Add(source);
        var vm = new BackupEditorViewModel(backup, isNew: false);
        // The ticks show what the backup has: caches yes, Windows system files no.
        Assert.False(vm.RecExcludeWindowsSystem);
        Assert.True(vm.RecExcludeCachesAndJunk);
        Assert.True(vm.HasRecommendationUpdate);
        // Nothing changed yet: the page says so, and the caches group is in place.
        Assert.False(vm.FiltersUnsaved);
        Assert.True(vm.RecommendedSaved);
        Assert.Equal(GroupStatus.InPlace, vm.CachesState.Status);
        Assert.True(vm.CachesState.HasPending);

        var win = new BackupEditorWindow { DataContext = vm, Width = 1000, Height = 980 };
        ctx.Driver.Show(win);
        Assert.True(ctx.Driver.WaitFor(() => win.GetVisualDescendants().OfType<TabControl>().Any()));
        win.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 2;
        ctx.Driver.Pump();
        Assert.True(ctx.Driver.WaitFor(() => Button(win, "Use ticked suggestions") is { IsEffectivelyVisible: true }));

        // The person approves the suggestions, as in the report: the caches
        // group now waits for Save, and the page and the footer say so.
        Button(win, "Use ticked suggestions")!.Command!.Execute(null);
        ctx.Driver.Pump();
        Assert.Equal(GroupStatus.WillChange, vm.CachesState.Status);
        Assert.True(vm.FiltersUnsaved);
        Assert.StartsWith("Not saved yet.", vm.RecommendedStatus);
        Assert.True(win.FindControl<StackPanel>("UnsavedFilters")!.IsEffectivelyVisible);
        Assert.True(vm.CanEstimateSavings);
        Button(win, "Estimate savings")!.Command!.Execute(null);
        Assert.True(ctx.Driver.WaitFor(() => vm.RecEstimatedSavingsBytes >= 0, 20_000), "The savings estimate never finished.");
        ctx.Driver.Pump();
        Assert.True(vm.RecEstimatedSavingsBytes >= 3_000_000, $"The node_modules folder was not counted: {vm.RecEstimatedSavingsBytes}.");
        Assert.DoesNotContain("Nothing in your sources matched", vm.RecEstimatedSavingsLabel);
        win.FindControl<Border>("RecommendedCard")!.BringIntoView();
        ctx.Driver.Pump();
        ctx.Snapshot(win, "suggestions used, waiting for save",
            "After 'Use ticked suggestions': the caches group reads 'Will change when you save' in amber, the line at the top and the footer say the change is not saved yet, and the estimate shows what the ticked groups leave out (3.0 MB).");

        // Ticking a group writes it at once: no Apply button to find.
        vm.RecExcludeWindowsSystem = true;
        ctx.Driver.Pump();
        Assert.Equal(GroupStatus.WillBeAdded, vm.WindowsSystemState.Status);
        Assert.Null(Button(win, "Apply recommended exclusions"));
        // The Windows group's rules that can match in a folder (U148): thumbs.db, not /Windows/.
        Assert.Contains("thumbs.db", vm.FiltersText);
        Assert.DoesNotContain("/Windows/", vm.FiltersText);
        ctx.Snapshot(win, "tick changed",
            "Windows system files ticked: its rules are in the filters at once and the group reads 'Will be added when you save'; there is no Apply button.");
        win.Close();
        vm.Dispose();
        await Task.CompletedTask;
    }

    private static async Task SizesAndVerbose(ScenarioContext ctx)
    {
        var dest = new Destination { Name = "Local backups", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("repo") };
        var photos = ctx.MakeSource("photos", targetBytes: 2_000_000);
        var docs = ctx.MakeSource("docs", targetBytes: 50_000);
        var never = new Backup { Name = "Photos (never run)", SourcePaths = { photos } };
        var ran = new Backup { Name = "Documents", SourcePaths = { docs },
            LastRunStatus = BackupRunStatus.Success, LastRunSummary = "Completed in 4s",
            LastRunEndUtc = DateTime.UtcNow.AddHours(-1), LastSuccessUtc = DateTime.UtcNow.AddHours(-1) };
        ran.SourceLastSizeBytes[docs] = 7_340_032;
        foreach (var b in new[] { never, ran }) b.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(never); cfg.Backups.Add(ran); });

        var logPath = Path.Combine(ctx.RootDir, "engine-run.log");
        File.WriteAllText(logPath, string.Join("\n",
            "[orchestrator] attempt 1 starting at 2026-11-03T07:29:25Z",
            "[backrest] op=1 status=STATUS_PENDING",
            "[backrest] op=1 status=STATUS_INPROGRESS",
            "[repo-manager] 21:31:49.992Z\tdebug\trepo orchestrator starting backup",
            "[restic] 21:32:01.583Z\tdebug\tcommand: restic.exe backup --json",
            "[progress] 42.5% · Backing up · 21 GB of 50 GB · 4,200 of 10,000 files · 31 MB/s",
            "[recap] Backup complete: 10,000 files (50 GB) checked · 120 new, 30 changed, 9,850 unchanged · 1.2 GB added to the storage · took 26m 10s",
            ""));
        var record = new RunRecord { BackupId = ran.Id, BackupName = ran.Name, StartedUtc = DateTime.UtcNow.AddHours(-1).AddSeconds(-4),
            EndedUtc = DateTime.UtcNow.AddHours(-1), Status = BackupRunStatus.Success, Summary = "Completed in 4s", LogPath = logPath };
        ServiceLocator.Logs.AppendRun(record);

        using var main = new MainWindowViewModel();
        var host = ctx.Driver.Show(new MainWindow { Width = 1100, Height = 820, DataContext = main });
        await ctx.Driver.PumpAsync(200);

        var cards = host.GetVisualDescendants().OfType<BackupCard>().ToList();
        var neverVm = (BackupCardViewModel)cards.Single(c => (c.DataContext as BackupCardViewModel)?.Snapshot.Name == never.Name).DataContext!;
        var ranVm = (BackupCardViewModel)cards.Single(c => (c.DataContext as BackupCardViewModel)?.Snapshot.Name == ran.Name).DataContext!;
        Assert.True(ctx.Driver.WaitFor(() => neverVm.Sources.Single().HasSize, 20_000), "The never-run backup showed no estimated size.");
        await ctx.Driver.PumpAsync(100);
        var estimate = neverVm.Sources.Single();
        Assert.StartsWith("~", estimate.SizeLabel);
        Assert.Contains("before the first backup", estimate.SizeTooltip);
        Assert.Equal("7.0 MB", ranVm.Sources.Single().SizeLabel);
        Assert.Equal("Size measured by the last backup.", ranVm.Sources.Single().SizeTooltip);
        ctx.Snapshot(host, "estimated and measured sizes",
            $"The never-run backup's source shows '({estimate.SizeLabel})', estimated from the disk; the backup that ran shows its measured ({HumanFormat.Size(7_340_032)}).");

        // ---- U113: Verbose beside Clear view ----
        ServiceLocator.Config.Update(cfg => cfg.Logging.MinimumLevel = "Information");
        main.SelectedNav = NavItem.Logs;
        await ctx.Driver.PumpAsync(200);
        var view = host.GetVisualDescendants().OfType<LogsView>().Single();
        view.FindControl<ComboBox>("BackupPicker")!.SelectedItem = ran.Name;
        await ctx.Driver.PumpAsync(300);
        Assert.Equal(record.Id, main.Logs.SelectedRun?.Id);
        Assert.True(ctx.Driver.WaitFor(() => main.Logs.DisplayText.Contains("[recap]")));
        var verbose = view.FindControl<CheckBox>("VerboseLog")!;
        Assert.True(verbose.IsEffectivelyVisible);
        Assert.False(verbose.IsChecked);
        Assert.DoesNotContain("\tdebug\t", main.Logs.DisplayText);
        Assert.DoesNotContain("STATUS_INPROGRESS", main.Logs.DisplayText);
        Assert.Contains("[progress] 42.5%", main.Logs.DisplayText);
        ctx.Snapshot(host, "readable log, verbose off",
            "Activity & logs with Verbose unticked (left of Clear view's place): the run shows the attempt, progress and recap lines only.");

        verbose.IsChecked = true;
        await ctx.Driver.PumpAsync(200);
        Assert.True(VerboseLogFilter.IsVerboseOn());
        Assert.True(ctx.Driver.WaitFor(() => main.Logs.DisplayText.Contains("\tdebug\t")), "Verbose did not reveal the saved debug lines.");
        Assert.Contains("STATUS_INPROGRESS", main.Logs.DisplayText);
        ctx.Snapshot(host, "verbose on shows earlier lines",
            "Verbose ticked: the same saved run now also shows Backrest's debug lines and state polls, without rerunning anything.");
        // U113 follow-up: long lines (the recap) wrap; no horizontal bar.
        var scroll = view.FindControl<ScrollViewer>("LogScroll")!;
        Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1, $"The log is wider ({scroll.Extent.Width}) than its pane ({scroll.Viewport.Width}).");
        // The recap is a console row (U176), drawn within the pane's width.
        var recapRow = Assert.Single(view.GetVisualDescendants().OfType<ConsoleLineRow>(), r => r.Line?.Text.Contains("took 26m 10s") == true);
        Assert.True(recapRow.Bounds.Width <= scroll.Viewport.Width + 1, "The recap row is wider than the pane.");
        verbose.IsChecked = false;
        await ctx.Driver.PumpAsync(100);
        Assert.False(VerboseLogFilter.IsVerboseOn());

        // The live view has Copy, Verbose and Clear view side by side, inside the header.
        main.Logs.SelectedRun = null;
        await ctx.Driver.PumpAsync(200);
        Assert.True(main.Logs.IsShowingLive);
        var header = view.FindControl<Grid>("ConsoleHeader")!;
        foreach (var name in new[] { "CopyLog", "VerboseLog", "ClearLog" })
        {
            var control = view.FindControl<Control>(name)!;
            Assert.True(control.IsEffectivelyVisible, name + " is hidden in the live view.");
            var right = control.TranslatePoint(new Avalonia.Point(control.Bounds.Width, 0), header)!.Value.X;
            Assert.True(right <= header.Bounds.Width + 0.5, $"{name} runs past the header's edge.");
        }
        ctx.Snapshot(host, "live view header",
            "The live view: Copy, Verbose and Clear view all visible in the console header; Clear view appears only here, since a saved run can't be cleared.");
    }
}
