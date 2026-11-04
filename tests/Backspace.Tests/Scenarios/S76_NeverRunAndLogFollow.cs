using System;
using System.IO;
using System.Linq;
using System.Text;
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
/// U079: a backup that has never run offers nothing to mount, restore, test or
/// browse; those actions are disabled and say why, while a backup that ran
/// keeps them. U083: the log follows its last line like a terminal; once the
/// reader scrolls up, refreshes no longer pull them back, and Jump to latest
/// returns.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S76_NeverRunAndLogFollow : ScenarioBase
{
    protected override string ScenarioName => "S76-never-run-and-log-follow";

    [AvaloniaFact] public async Task Run_S76() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var dest = new Destination { Name = "Local backups", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("repo") };
        var ran = new Backup { Name = "Documents", SourcePaths = { ctx.MakeSource("docs", targetBytes: 50_000) },
            LastRunStatus = BackupRunStatus.Success, LastRunSummary = "Completed in 4s",
            LastRunEndUtc = DateTime.UtcNow.AddHours(-1), LastSuccessUtc = DateTime.UtcNow.AddHours(-1) };
        var never = new Backup { Name = "Photos (never run)", SourcePaths = { ctx.MakeSource("photos", targetBytes: 50_000) } };
        // Like local_d on 4 November 2026: it ran, but only ever failed.
        var failed = new Backup { Name = "Z drive (only failed)", SourcePaths = { ctx.MakeSource("d", targetBytes: 50_000) },
            LastRunStatus = BackupRunStatus.Failed, LastRunSummary = "1 error · in 1s", LastRunEndUtc = DateTime.UtcNow.AddMinutes(-30) };
        foreach (var b in new[] { ran, never, failed }) b.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(dest); cfg.Backups.Add(ran); cfg.Backups.Add(never); cfg.Backups.Add(failed); });

        // A long saved log for the follow checks.
        var log = new StringBuilder();
        for (var i = 1; i <= 400; i++) log.Append($"2026-11-02 10:{i / 60:00}:{i % 60:00} INFO line {i} of the saved run\n");
        log.Append("2026-11-02 10:07:00 INFO RUN_END Success · in 4s\n");
        var logPath = Path.Combine(ctx.RootDir, "long-run.log");
        File.WriteAllText(logPath, log.ToString());
        var record = new RunRecord { BackupId = ran.Id, BackupName = ran.Name, StartedUtc = DateTime.UtcNow.AddHours(-1).AddSeconds(-4),
            EndedUtc = DateTime.UtcNow.AddHours(-1), Status = BackupRunStatus.Success, Summary = "Completed in 4s", LogPath = logPath };
        ServiceLocator.Logs.AppendRun(record);

        using var main = new MainWindowViewModel();
        var host = ctx.Driver.Show(new MainWindow { Width = 1100, Height = 760, DataContext = main });
        await ctx.Driver.PumpAsync(200);

        // ---- U079: the never-run card ----
        var cards = host.GetVisualDescendants().OfType<BackupCard>().ToList();
        var neverCard = cards.Single(c => (c.DataContext as BackupCardViewModel)?.Snapshot.Name == never.Name);
        var ranCard = cards.Single(c => (c.DataContext as BackupCardViewModel)?.Snapshot.Name == ran.Name);
        neverCard.BringIntoView();
        await ctx.Driver.PumpAsync(100);
        Button ButtonOf(BackupCard card, string text) => card.GetVisualDescendants().OfType<Button>()
            .Single(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text));
        foreach (var label in new[] { "Restore files", "Test restore" })
        {
            Assert.False(ButtonOf(neverCard, label).IsEffectivelyEnabled, label + " was enabled for a backup that never ran.");
            Assert.True(ButtonOf(ranCard, label).IsEffectivelyEnabled, label + " was disabled for a backup that ran.");
        }
        var neverVm = (BackupCardViewModel)neverCard.DataContext!;
        var ranVm = (BackupCardViewModel)ranCard.DataContext!;
        Assert.False(neverVm.CanUseSnapshots);  // Mount and Browse snapshots bind to this
        Assert.True(ranVm.CanUseSnapshots);
        var failedVm = main.Backups.Cards.Single(c => c.Id == failed.Id);
        Assert.False(failedVm.CanUseSnapshots, "A backup whose only run failed offered Mount and Restore.");
        Assert.False(failedVm.TestRestoreEnabled);
        Assert.Equal(BackupCardViewModel.NothingToRestoreTooltip, neverVm.RestoreFilesTooltip);
        Assert.Equal(BackupCardViewModel.NothingToRestoreTooltip, neverVm.TestRestoreTooltip);
        if (neverVm.CanMountSnapshot)
            Assert.False(ButtonOf(neverCard, "Mount").IsEffectivelyEnabled);
        ctx.Snapshot(host, "never-run card",
            "The never-run backup's Mount, Restore files and Test restore are greyed out; the backup that ran keeps them enabled.");
        // The More menu's Browse snapshots item follows the same rule.
        var more = neverCard.GetVisualDescendants().OfType<Button>().Single(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "More"));
        more.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await ctx.Driver.PumpAsync(100);
        var browse = ((MenuFlyout)more.Flyout!).Items.OfType<MenuItem>().Single(i => (i.Header as string) == "Browse snapshots in browser");
        Assert.False(browse.IsEnabled);
        ctx.Snapshot(host, "never-run more menu", "More menu of the never-run backup: Browse snapshots in browser is greyed out.");
        ((MenuFlyout)more.Flyout!).Hide();

        // ---- U083: the log follows like a terminal ----
        main.SelectedNav = NavItem.Logs;
        await ctx.Driver.PumpAsync(200);
        var view = host.GetVisualDescendants().OfType<LogsView>().Single();
        view.FindControl<ComboBox>("BackupPicker")!.SelectedItem = ran.Name;
        await ctx.Driver.PumpAsync(300);
        Assert.Equal(record.Id, main.Logs.SelectedRun?.Id);
        var scroll = view.FindControl<ScrollViewer>("LogScroll")!;
        var jump = view.FindControl<Button>("JumpToLatest")!;
        double Bottom() => Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height * 2, "The test log must be longer than the view.");
        Assert.True(Math.Abs(scroll.Offset.Y - Bottom()) < 2, "A newly opened run should show its last line.");
        Assert.False(jump.IsVisible);

        // The reader scrolls up to read or select.
        scroll.Offset = new Vector(0, 0);
        await ctx.Driver.PumpAsync(100);
        Assert.False(view.IsFollowing);
        Assert.True(jump.IsEffectivelyVisible);
        // The periodic history refresh (every 3 s) must not pull them back.
        ServiceLocator.Logs.RefreshBackgroundHistory(ran.Id);
        await ctx.Driver.PumpAsync(3500);
        Assert.Equal(record.Id, main.Logs.SelectedRun?.Id);
        Assert.Equal(0, scroll.Offset.Y);
        ctx.Snapshot(host, "log scrolled up stays", "Scrolled to the top of a finished run: refreshes keep the reader there, and Jump to latest is offered at the bottom right.");

        // Jump to latest returns to the end and follows again.
        jump.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await ctx.Driver.PumpAsync(100);
        Assert.True(view.IsFollowing);
        Assert.False(jump.IsVisible);
        Assert.True(Math.Abs(scroll.Offset.Y - Bottom()) < 2);
        // Scrolling back to the end by hand also resumes following.
        scroll.Offset = new Vector(0, 0); await ctx.Driver.PumpAsync(50);
        Assert.False(view.IsFollowing);
        scroll.Offset = new Vector(0, Bottom()); await ctx.Driver.PumpAsync(50);
        Assert.True(view.IsFollowing);
        ctx.Snapshot(host, "log back at the end", "After Jump to latest the last line is shown and the button is gone.");
    }
}
