using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Logs &amp; runs view with a backup selected and a populated
/// history of past runs. The terminal pane renders the loaded
/// run's log file — we materialise a fake one on disk under the
/// LogStore root so the file-load path actually fires (covers
/// the recent flicker fix + the new log-loading wiring).
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S11_LogsViewPopulated : ScenarioBase
{
    protected override string ScenarioName => "S11-logs-view-populated";

    [AvaloniaFact] public async Task Run_S11() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var dest = new Destination
        {
            Name = "Local — backup-dir",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("seed"),
        };
        var backup = new Backup
        {
            Name = "Documents",
            SourcePaths = { ctx.MakeSource("docs", seed: 1, targetBytes: 200_000) },
            LastRunStatus = BackupRunStatus.Success,
            LastRunSummary = "rev 12 · in 4s",
            LastRunEndUtc = DateTime.UtcNow.AddHours(-1),
        };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        var otherBackup = new Backup { Name = "Photos" };
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(dest);
            cfg.Backups.Add(backup);
            cfg.Backups.Add(otherBackup);
        });

        // Build a synthetic per-run log file + persist a RunRecord
        // pointing at it. The LogsView's "Selected run" pane reads
        // this off disk via the LogPath.
        var logPath = Path.Combine(ctx.RootDir, "fake-run.log");
        File.WriteAllText(logPath,
            "2026-04-30 14:00:00 INFO RUN_START Documents → Local - backup-dir\n" +
            "2026-04-30 14:00:01 INFO INDEX_FILES Walking 1,234 files\n" +
            "2026-04-30 14:00:03 INFO UPLOAD_CHUNK 512 KiB → chunk a1b2c3\n" +
            "2026-04-30 14:00:04 INFO SNAPSHOT_INFO revision 12 created\n" +
            "2026-04-30 14:00:04 INFO RUN_END Success · in 4s\n");
        var record = new RunRecord
        {
            BackupId = backup.Id,
            BackupName = backup.Name,
            StartedUtc = DateTime.UtcNow.AddHours(-1).AddSeconds(-4),
            EndedUtc = DateTime.UtcNow.AddHours(-1),
            Status = BackupRunStatus.Success,
            Summary = "rev 12 · in 4s",
            LogPath = logPath,
        };
        ServiceLocator.Logs.AppendRun(record);

        using var main = new MainWindowViewModel();
        var host = ctx.Driver.Show(new MainWindow { Width = 1024, Height = 640, DataContext = main });
        var activityNav = host.GetVisualDescendants().OfType<RadioButton>()
            .Single(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Activity & logs"));
        Click(host, activityNav);
        await ctx.Driver.PumpAsync(180);
        Assert.Equal(NavItem.Logs, main.SelectedNav);
        var vm = main.Logs;
        var view = host.GetVisualDescendants().OfType<LogsView>().Single();
        // The full suite may retain unrelated destination-maintenance history.
        // Choose this backup through its real control rather than relying on
        // whichever bucket happens to appear first in the selector.
        var backupPicker = view.FindControl<ComboBox>("BackupPicker")!;
        backupPicker.SelectedItem = backup.Name;
        await ctx.Driver.PumpAsync(180);
        Assert.All(vm.History, r => Assert.Equal(backup.Id, r.BackupId));
        var picker = view.FindControl<ComboBox>("RunPicker")!;
        var badge = view.FindControl<Border>("LiveBadge")!;
        var context = view.FindControl<TextBlock>("ConsoleContext")!;
        var clear = view.FindControl<Button>("ClearLog")!;
        var combinedHint = view.FindControl<TextBlock>("CombinedOutputHint")!;
        Assert.Equal(record.Id, vm.SelectedRun?.Id);
        Assert.Contains("RUN_END Success", vm.DisplayText);
        Assert.False(badge.IsEffectivelyVisible);
        Assert.False(clear.IsEffectivelyVisible);
        Assert.False(combinedHint.IsEffectivelyVisible);
        Assert.Contains("Success", context.Text);
        ctx.Snapshot(host, "logs populated past-run",
            "The actual Activity & logs navigation opens the saved successful run. Its output is visible; no LIVE indicator or Clear view action is shown for history.");

        // Register a realistic in-flight restore through the same activity
        // registry the restore UI uses. Selecting the actual bound ComboBox
        // checks binding and presentation, not only the label getter.
        var running = new RunRecord { BackupId = backup.Id, BackupName = backup.Name,
            Kind = RunKind.Restore, StartedUtc = DateTime.UtcNow, Status = BackupRunStatus.Running,
            Summary = "Running…" };
        ServiceLocator.Activity.Register(running);
        var otherRunning = new RunRecord { BackupId = otherBackup.Id, BackupName = otherBackup.Name,
            Kind = RunKind.Restore, StartedUtc = DateTime.UtcNow.AddSeconds(1), Status = BackupRunStatus.Running,
            Summary = "Running…" };
        ServiceLocator.Activity.Register(otherRunning);
        try
        {
            ctx.Driver.Pump();
            picker.SelectedItem = vm.History.Single(r => r.Id == running.Id);
            ServiceLocator.Engine.RaiseLineWritten("2026-05-01 12:00:00 INFO RESTORE_START Documents");
            ServiceLocator.Engine.RaiseLineWritten("2026-05-01 12:00:01 INFO RESTORE_FILE annual-report.pdf");
            ServiceLocator.Engine.RaiseLineWritten("2026-05-01 12:00:02 INFO RESTORE_PROGRESS 42 of 123 files");
            ServiceLocator.Engine.RaiseLineWritten("2026-05-01 12:00:03 INFO RESTORE_FILE Photos — holiday.jpg");
            await ctx.Driver.PumpAsync(240);
            Assert.Equal(running.Id, vm.SelectedRun?.Id);
            Assert.Contains("42 of 123 files", vm.DisplayText);
            Assert.True(badge.IsEffectivelyVisible);
            Assert.True(clear.IsEffectivelyVisible);
            Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(),
                t => t.IsEffectivelyVisible && string.Equals(t.Text, "live", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("All current activity", context.Text);
            Assert.True(combinedHint.IsEffectivelyVisible);
            Assert.Contains("holiday.jpg", vm.DisplayText);
            Assert.DoesNotContain("live", context.Text!, StringComparison.OrdinalIgnoreCase);
            // Select the other running backup through the actual keyboard
            // control. Both activities still share one engine stream, so the
            // console must not relabel those same lines as Photos-only.
            backupPicker.Focus();
            host.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None); host.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            ctx.Driver.Pump();
            Assert.Equal(otherBackup.Name, vm.SelectedBackup);
            Assert.Equal(otherRunning.Id, vm.SelectedRun?.Id);
            Assert.Equal("All current activity", context.Text);
            Assert.Contains("annual-report.pdf", vm.DisplayText);
            Assert.Contains("holiday.jpg", vm.DisplayText);
            host.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None); host.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            ctx.Driver.Pump();
            Assert.Equal(backup.Name, vm.SelectedBackup);
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                host.RequestedThemeVariant = theme;
                Screenshots.ScreenshotDiffTests.SettleInitialTransitions(host);
                var header = view.FindControl<Grid>("ConsoleHeader")!;
                foreach (var control in new Control[] { badge, context, clear, view.FindControl<Button>("CopyLog")! })
                {
                    var at = control.TranslatePoint(new Point(0, 0), header)!.Value;
                    Assert.True(at.X >= -1 && at.Y >= -1 && at.X + control.Bounds.Width <= header.Bounds.Width + 1
                        && at.Y + control.Bounds.Height <= header.Bounds.Height + 1,
                        $"{control.Name} escapes the narrow console header.");
                }
                var copy = view.FindControl<Button>("CopyLog")!;
                var copyPoint = copy.TranslatePoint(new Point(copy.Bounds.Width / 2, copy.Bounds.Height / 2), host)!.Value;
                host.MouseMove(copyPoint);
                var copyBorder = copy.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "RootBorder");
                // Theme and pointer transitions use the real animation clock.
                // Await frames until the hover settles instead of sampling one
                // nearly-complete frame after a synchronous layout pump.
                var hoverDeadline = DateTime.UtcNow.AddSeconds(2);
                while ((copyBorder.Background as ISolidColorBrush)?.Color != Color.Parse("#303C4B")
                       && DateTime.UtcNow < hoverDeadline)
                {
                    await Task.Delay(16);
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    ctx.Driver.Pump();
                }
                Assert.Equal(Color.Parse("#303C4B"), Assert.IsAssignableFrom<ISolidColorBrush>(copyBorder.Background).Color);
                ctx.Snapshot(host, $"logs single live indicator {theme}",
                    "Exactly one LIVE badge, All current activity and an explanation that concurrent output is combined. Lines from Documents and Photos remain visible when the selected running backup changes. Copy and Clear view fit the header in both themes.");
                host.MouseMove(new Point(2, 2));
            }

            picker.SelectedIndex = -1; ctx.Driver.Pump();
            Assert.Null(vm.SelectedRun);
            Assert.Equal("All current activity", context.Text);
            Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(),
                t => t.IsEffectivelyVisible && string.Equals(t.Text, "live", StringComparison.OrdinalIgnoreCase));
            ctx.Snapshot(host, "unselected output has one live indicator",
                "With no run selected, the console reads LIVE · All current activity once. The explanation identifies its combined scope instead of implying that all lines came from the selected backup.");

            var savedBytes = File.ReadAllBytes(logPath);
            Click(host, clear); ctx.Driver.Pump();
            Assert.Empty(vm.DisplayText);
            Assert.Equal(savedBytes, File.ReadAllBytes(logPath));
            Assert.Contains(ServiceLocator.Logs.LoadHistory(backup.Name), r => r.Id == record.Id);
            picker.SelectedItem = vm.History.Single(r => r.Id == record.Id);
            await ctx.Driver.PumpAsync(180);
            Assert.False(badge.IsEffectivelyVisible);
            Assert.False(clear.IsEffectivelyVisible);
            Assert.False(combinedHint.IsEffectivelyVisible);
            Assert.Contains("RUN_END Success", vm.DisplayText);
            Assert.DoesNotContain("holiday.jpg", vm.DisplayText);
            ctx.Snapshot(host, "saved history after clearing live display",
                "Returning to a past run hides LIVE and restores its saved output. Clearing the live view did not delete the log file or history record.");
        }
        finally { ServiceLocator.Activity.Unregister(running.Id); ServiceLocator.Activity.Unregister(otherRunning.Id); }
        host.RequestedThemeVariant = ThemeVariant.Light;
        host.MouseMove(new Point(2, 2));

        var fresh = new Backup { Name = "New family archive" };
        ServiceLocator.Config.Update(cfg => cfg.Backups.Add(fresh));
        vm.Refresh();
        view.FindControl<ComboBox>("BackupPicker")!.SelectedItem = fresh.Name;
        await ctx.Driver.PumpAsync(180);
        Assert.True(vm.HasNoHistoryForSelected);
        Assert.False(badge.IsEffectivelyVisible);
        var emptyNotice = view.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == "No runs yet for this backup");
        Assert.True(emptyNotice.IsEffectivelyVisible);
        ctx.Snapshot(host, "no runs has no live indicator",
            "A backup with no history shows clear next-step guidance and does not claim it has live activity.");

        // Stay on this never-run backup. No navigation, refresh, or manual
        // run selection may be needed to uncover its very first live output.
        var firstActivity = new RunRecord { BackupId = fresh.Id, BackupName = fresh.Name,
            Kind = RunKind.TestRestore, StartedUtc = DateTime.UtcNow.AddSeconds(-2),
            Status = BackupRunStatus.Running, Summary = "Running…" };
        ServiceLocator.Activity.Register(firstActivity);
        try
        {
            await ctx.Driver.PumpAsync(60);
            ServiceLocator.Engine.RaiseLineWritten("FIRST_ACTIVITY_LINE Checking the new archive");
            await ctx.Driver.PumpAsync(240);
            Assert.Equal(fresh.Name, vm.SelectedBackup);
            Assert.Equal(firstActivity.Id, vm.SelectedRun?.Id);
            Assert.False(vm.HasNoHistoryForSelected);
            Assert.False(emptyNotice.IsEffectivelyVisible);
            Assert.True(badge.IsEffectivelyVisible);
            Assert.Contains("FIRST_ACTIVITY_LINE", vm.DisplayText);
            Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(),
                t => t.IsEffectivelyVisible && string.Equals(t.Text, "live", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
                t => t.IsEffectivelyVisible && t.Text?.Contains("FIRST_ACTIVITY_LINE") == true);
            ctx.Snapshot(host, "first activity replaces the empty hint",
                "Without changing backup selection or reopening Logs, the first activity removes No runs yet, shows one LIVE badge, and displays the first output line.");
        }
        finally { ServiceLocator.Activity.Unregister(firstActivity.Id); }

        await ctx.Driver.PumpAsync(180);
        Assert.Empty(vm.History);
        Assert.True(vm.HasNoHistoryForSelected);
        Assert.True(emptyNotice.IsEffectivelyVisible);
        Assert.False(badge.IsEffectivelyVisible);
        ctx.Snapshot(host, "unregistered activity restores the empty hint",
            "If the activity leaves before a saved result arrives, the empty hint returns and the LIVE badge is hidden; stale live status is not left behind.");

        var firstLog = Path.Combine(ctx.RootDir, "first-activity.log");
        File.WriteAllText(firstLog, "FIRST_RUN_COMPLETED Archive check finished successfully.\n");
        ServiceLocator.Logs.AppendRun(new RunRecord { Id = firstActivity.Id, BackupId = fresh.Id,
            BackupName = fresh.Name, Kind = firstActivity.Kind, StartedUtc = firstActivity.StartedUtc,
            EndedUtc = DateTime.UtcNow, Status = BackupRunStatus.Success,
            Summary = "First archive check completed", LogPath = firstLog });
        await ctx.Driver.PumpAsync(240);
        Assert.Single(vm.History);
        Assert.Equal(firstActivity.Id, vm.SelectedRun?.Id);
        Assert.False(vm.HasNoHistoryForSelected);
        Assert.False(emptyNotice.IsEffectivelyVisible);
        Assert.False(badge.IsEffectivelyVisible);
        Assert.False(clear.IsEffectivelyVisible);
        Assert.Contains("FIRST_RUN_COMPLETED", vm.DisplayText);
        ctx.Snapshot(host, "first saved result replaces the empty hint",
            "The first completed record appears automatically with its saved output and Success status. The empty hint and LIVE badge are both hidden.");
        ctx.Report.RecordNote("scope and negative cases", "Actual navigation and Clear view receive pointer clicks; live/history selection uses the bound ComboBox. First-activity transitions require no manual selection or refresh. Activity and engine lines are fixture signals, not a real restore. Saved log bytes and history remain after clearing. The empty hint updates both when the first activity appears and when it disappears; historical and never-run backups cannot display the LIVE badge.");
        Backspace.App.QuitRequested = true; host.Close(); Backspace.App.QuitRequested = false;
    }

    private static void Click(Window host, Control target)
    {
        var at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), host)!.Value;
        host.MouseDown(at, MouseButton.Left); host.MouseUp(at, MouseButton.Left);
    }
}
