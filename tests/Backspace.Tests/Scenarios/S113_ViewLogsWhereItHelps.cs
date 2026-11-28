using System;
using System.Linq;
using System.Threading.Tasks;
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
/// U265 (owner, 28 November 2026): "Why does it show a View logs button under
/// 9 days ago? Seems useless here. Conversely, right now my instance says
/// Backup health Restore check failed - 4h ago and there is no View logs
/// button below (which would make sense). Make sure the view logs button
/// shows up nicely where really appropriate only." A missed schedule (the
/// last backup was fine, 9 days ago) has no View logs; a run with warnings and
/// a failed run have one under Last backup; a failed restore check has one
/// under Backup health.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S113_ViewLogsWhereItHelps : ScenarioBase
{
    protected override string ScenarioName => "S113-view-logs-where-it-helps";

    [AvaloniaFact] public async Task Run_S113() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var now = DateTime.UtcNow;
        var drive = new Destination { Name = "Disk", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("disk") };
        Backup Make(string name, double daysAgo, BackupRunStatus last, string summary)
        {
            var b = new Backup { Name = name, SourcePaths = { ctx.MakeSource(name, targetBytes: 4096) }, Schedule = TestSchedules.DueInAnHour(),
                CreatedUtc = now.AddDays(-60), LastSuccessUtc = last == BackupRunStatus.Failed ? now.AddDays(-3) : now.AddDays(-daysAgo),
                LastRunStatus = last, LastRunStartUtc = now.AddDays(-daysAgo).AddMinutes(-5), LastRunEndUtc = now.AddDays(-daysAgo), LastRunSummary = summary };
            b.Targets.Add(new BackupTarget { DestinationId = drive.Id, StorageName = "default" });
            return b;
        }
        var missed = Make("local_d", 9, BackupRunStatus.Success, "27.42 MB uploaded · 150 new / 195 mod / 0 rem · in 1m 29s");
        var checkFailed = Make("local_c", 16 / 24.0, BackupRunStatus.Success, "387.14 MB uploaded · 37 new / 201 mod / 0 rem · in 9m 13s");
        checkFailed.LastVerifyUtc = now.AddHours(-4); checkFailed.LastVerifyPass = false;
        checkFailed.LastVerifySummary = "Restore check (1% of files)\n«Disk»: rev 25 · 4 files could not be recovered correctly.";
        var warned = Make("photos", 0.2, BackupRunStatus.Warning, "Completed with warnings: 2 files could not be read");
        var failed = Make("music", 0.1, BackupRunStatus.Failed, "The backup could not write to the destination");
        // Warnings, then a missed schedule: the log still explains the warnings.
        var warnedLate = Make("documents", 9, BackupRunStatus.Warning, "Completed with warnings: 1 file could not be read");
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(drive);
            cfg.Backups.Add(missed); cfg.Backups.Add(checkFailed); cfg.Backups.Add(warned); cfg.Backups.Add(failed); cfg.Backups.Add(warnedLate);
        });

        using var main = new MainWindowViewModel();
        var host = ctx.Driver.Show(new MainWindow { Width = 1300, Height = 1650, DataContext = main });
        var backups = host.GetVisualDescendants().OfType<BackupsView>().Single();
        var vm = (BackupsViewModel)backups.DataContext!;
        await Task.WhenAll(vm.Cards.Select(c => c.CheckHistoryRead)).WaitAsync(TimeSpan.FromSeconds(10));
        await ctx.Driver.PumpAsync(300);
        bool Shown(Backup b, string button)
        {
            var card = backups.GetVisualDescendants().OfType<BackupCard>().Single(c => (c.DataContext as BackupCardViewModel)?.Id == b.Id);
            return card.GetVisualDescendants().OfType<Button>().Single(x => x.Name == button).IsEffectivelyVisible;
        }
        // A missed schedule: orange, but no log explains it.
        Assert.Equal("warn", vm.Cards.Single(c => c.Id == missed.Id).StatusClass);
        Assert.False(Shown(missed, "RunWarnLogs"), "A missed schedule has no log to view.");
        Assert.False(Shown(missed, "CheckFailLogs"));
        // A failed restore check: View logs under Backup health, none under Last backup.
        Assert.True(Shown(checkFailed, "CheckFailLogs"), "A failed check offers its log.");
        Assert.False(Shown(checkFailed, "RunWarnLogs"));
        Assert.False(Shown(checkFailed, "RunFailLogs"));
        // Warnings and failures of the run itself: View logs under Last backup.
        Assert.True(Shown(warned, "RunWarnLogs"));
        Assert.True(Shown(failed, "RunFailLogs"));
        Assert.True(Shown(warnedLate, "RunWarnLogs"), "A run with warnings keeps its View logs when its schedule is also missed.");
        Assert.False(Shown(warned, "CheckFailLogs"));
        ctx.Snapshot(host, "view-logs-where-it-helps",
            "local_c: Backup health 'Restore check failed' with View logs under it; local_d: missed schedule (9d ago), orange, no View logs; music: failed run with View logs; photos: run with warnings with View logs.");

        // U265 (owner, 28 November 2026): "The coloring seems poor. In dark
        // mode there is not even any outline." The button is the card's
        // surface with an outline in the tile's tone, in both themes, never
        // the tile's own colour.
        Button Of(Backup b, string name) => backups.GetVisualDescendants().OfType<BackupCard>()
            .Single(c => (c.DataContext as BackupCardViewModel)?.Id == b.Id).GetVisualDescendants().OfType<Button>().Single(x => x.Name == name);
        foreach (var theme in new[] { Avalonia.Styling.ThemeVariant.Light, Avalonia.Styling.ThemeVariant.Dark })
        {
            host.RequestedThemeVariant = theme;
            Screenshots.ScreenshotDiffTests.SettleInitialTransitions(host);
            await ctx.Driver.PumpAsync(150);
            foreach (var (backup, name) in new[] { (checkFailed, "CheckFailLogs"), (warned, "RunWarnLogs"), (failed, "RunFailLogs") })
            {
                var button = Of(backup, name);
                var presenter = button.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
                var outline = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(presenter.BorderBrush).Color;
                var fill = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(presenter.Background).Color;
                var tile = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(button.GetVisualAncestors().OfType<Border>().First(c => c.Classes.Contains("check-summary")).Background).Color;
                Assert.True(outline.A == 255 && presenter.BorderThickness.Left >= 1, $"{name} has a visible outline in the {theme} theme.");
                Assert.True(Contrast(outline, tile) >= 3, $"{name}'s outline stands out from its tile in the {theme} theme ({Contrast(outline, tile):0.0}:1).");
                Assert.NotEqual(tile, fill);
                var text = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(button.Foreground).Color;
                Assert.True(Contrast(text, fill) >= 4.5, $"{name}'s text reads on the button in the {theme} theme ({Contrast(text, fill):0.0}:1).");
            }
        }
        ctx.Snapshot(host, "view-logs-dark",
            "The same cards in the dark theme: each View logs is a dark button with an orange or red outline on its orange or red tile.");
        host.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        Screenshots.ScreenshotDiffTests.SettleInitialTransitions(host);
        await ctx.Driver.PumpAsync(150);

        // The check's View logs opens Activity & logs.
        NavItem? went = null;
        void OnGo(NavItem item) => went = item;
        AppNavigator.Requested += OnGo;
        try { vm.Cards.Single(c => c.Id == checkFailed.Id).ViewCheckLogsCommand.Execute(null); }
        finally { AppNavigator.Requested -= OnGo; }
        Assert.Equal(NavItem.Logs, went);
    }

    private static double Contrast(Avalonia.Media.Color a, Avalonia.Media.Color b)
    {
        static double L(Avalonia.Media.Color c)
        {
            static double C(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * C(c.R) + 0.7152 * C(c.G) + 0.0722 * C(c.B);
        }
        var (x, y) = (L(a), L(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }
}
