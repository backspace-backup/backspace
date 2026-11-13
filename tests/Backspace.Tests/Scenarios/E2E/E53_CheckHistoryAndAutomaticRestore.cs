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
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

[Collection(ScenarioCollection.Name)]
public sealed class E53_CheckHistoryAndAutomaticRestore(ITestOutputHelper output) : BackupE2EBase(output)
{
    protected override string ScenarioName => "E53-check-history-and-automatic-restore";
    [AvaloniaFact] public Task Run_E53() => Run();
    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var source = ctx.MakeSource("documents", 5316, 2_000_000);
        var changed = Path.Combine(source, "compare-me.txt"); File.WriteAllText(changed, "original bytes");
        var stamp = File.GetLastWriteTimeUtc(changed);
        var dest = new Destination { Name = "Local encrypted archive", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("repo"), Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e53", Guid.NewGuid().ToString("N")) };
        var backup = new Backup { Name = "Documents", UseVss = false, VssDisabledAcknowledged = true, AbortOnMeteredNetwork = false,
            SourcePaths = { source }, Targets = { new() { DestinationId = dest.Id } }, PruneSchedule = new(),
            Checks = { new BackupCheck { Kind = CheckKind.Structure, Schedule = new() { Frequency = ScheduleFrequency.Monthly } } } };
        ServiceLocator.Config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(dest); });
        using var mainVm = new MainWindowViewModel();
        var main = ctx.Driver.Show(new MainWindow { DataContext = mainVm, Width = 1024, Height = 768 });
        var view = main.GetVisualDescendants().OfType<BackupsView>().Single();
        Assert.Equal(BackupRunStatus.Success, (await RunBackupViaUiAsync(ctx, backup, view, deadline.Token)).Status);
        var checkedRun = await ServiceLocator.Orchestrator.RunCheckAsync(backup, backup.Checks[0], deadline.Token);
        Assert.Equal(BackupRunStatus.Success, checkedRun.Status);
        Assert.Equal(CheckDepth.RepositoryStructure, Assert.Single(checkedRun.Checks).Depth);
        await ctx.Driver.PumpAsync(300);
        var cards = (BackupsViewModel)view.DataContext!;
        var card = cards.Cards.Single();
        Assert.Equal("ok", card.VerifyClass); Assert.Null(ServiceLocator.Config.Current.Backups.Single().LastVerifyUtc);
        Assert.Contains("stored data not read", card.CheckScopeLabel);
        ctx.Snapshot(main, "scheduled-check-on-card", "A real repository check turns the check line green without a manual Test restore. The result says Backup records intact; its details explain that file contents were not read.");
        Click(main, FindButton(main, "Activity")); await ctx.Driver.PumpAsync(200);
        var history = main.OwnedWindows.OfType<BackupHistoryWindow>().Single();
        Assert.Equal("All activity", ((BackupHistoryViewModel)history.DataContext!).SelectedFilter);
        Assert.Equal(2, ((BackupHistoryViewModel)history.DataContext!).Rows.Count);
        ((BackupHistoryViewModel)history.DataContext!).SelectedFilter = "Backup runs";
        Assert.Single(((BackupHistoryViewModel)history.DataContext!).Rows);
        ctx.Snapshot(history, "backup-run-history", "The actual History button opens this backup's dated run list, newest first.");
        var backupRunId = ((BackupHistoryViewModel)history.DataContext!).Rows.Single().RunId;
        Click(history, FindButton(history, "Open log")); await ctx.Driver.PumpAsync(200);
        Assert.Equal(backupRunId, mainVm.Logs.SelectedRun?.Id);
        AppNavigator.Go(NavItem.Backups); await ctx.Driver.PumpAsync(200);
        Click(main, FindButton(main, "Activity")); await ctx.Driver.PumpAsync(200);
        history = main.OwnedWindows.OfType<BackupHistoryWindow>().Single();
        Assert.Equal("All activity", ((BackupHistoryViewModel)history.DataContext!).SelectedFilter);
        ((BackupHistoryViewModel)history.DataContext!).SelectedFilter = "Checks";
        Assert.Equal(checkedRun.Id, Assert.Single(((BackupHistoryViewModel)history.DataContext!).Rows).RunId);
        ctx.Snapshot(history, "repository-check-history", "Check history opens the repository check with its time, result and actual depth; backup runs are excluded."); history.Close();

        using var editor = new BackupEditorViewModel(ServiceLocator.Config.Current.Backups.Single(), false);
        Assert.DoesNotContain(editor.Checks, c => c.Kind == CheckKind.TestRestore);
        var settings = ctx.Driver.Show(new BackupEditorWindow { DataContext = editor, Width = 1024, Height = 768 });
        settings.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 1;
        settings.FindControl<TabControl>("ScheduleSubTabs")!.SelectedIndex = 2; // Checks (U228)
        await ctx.Driver.PumpAsync(100);
        // U179: each check is its own entry, added with "Add a check".
        var addRestore = settings.FindControl<Button>("AddTestRestore")!;
        addRestore.BringIntoView(); await ctx.Driver.PumpAsync(100);
        Click(settings, addRestore); await ctx.Driver.PumpAsync(100);
        var restoreEntry = editor.Checks.Single(c => c.Kind == CheckKind.TestRestore);
        restoreEntry.SamplePercent = 100; restoreEntry.MaximumGiB = 0;
        ctx.Snapshot(settings, "automatic-restore-options", "The sample test restore is its own check: every file (100%, no limit); comparison with originals is optional and off.");
        var saved = editor.CommitToWorking()!;
        var restoreCheck = saved.Checks.Single(c => c.Kind == CheckKind.TestRestore);
        Assert.Equal(100, restoreCheck.SamplePercent); Assert.Equal(0, restoreCheck.MaximumBytes);
        ServiceLocator.Config.Update(c => c.Backups[0] = saved); settings.Close();
        var complete = await ServiceLocator.Orchestrator.RunCheckAsync(saved, restoreCheck, deadline.Token);
        Assert.Equal(BackupRunStatus.Success, complete.Status);
        var automatic = ServiceLocator.Logs.LoadHistoryByBackupId(saved.Id).First(r => r.Kind == RunKind.TestRestore);
        Assert.True(automatic.TriggeredAutomatically); Assert.Equal(BackupRunStatus.Success, automatic.Status);
        Assert.Equal(CheckDepth.RestoreSample, Assert.Single(automatic.Checks).Depth);
        Assert.True(ServiceLocator.Config.Current.Backups.Single().LastVerifyPass);
        await ctx.Driver.PumpAsync(250);

        File.WriteAllText(changed, "modified bytes"); File.SetLastWriteTimeUtc(changed, stamp);
        Assert.False(restoreCheck.CompareOriginals);
        var withoutComparison = await ServiceLocator.Orchestrator.RunCheckAsync(saved, restoreCheck, deadline.Token);
        Assert.Equal(BackupRunStatus.Success, withoutComparison.Status);
        Assert.Contains("not requested", ServiceLocator.Config.Current.Backups.Single().LastVerifySummary);
        {
            var comparisonWindow = ctx.Driver.Show(new TestRestoreOptionsWindow(saved.Name, restoreCheck.RestoreOptions()));
            var compare = comparisonWindow.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "CompareOriginals");
            Assert.False(compare.IsChecked);
            Click(comparisonWindow, compare); await ctx.Driver.PumpAsync(100); Assert.True(compare.IsChecked);
            ctx.Snapshot(comparisonWindow, "optional-original-comparison", "Comparison is an explicit optional checkbox, separate from sample/all recovery and from restic's integrity checks.");
            restoreCheck.CompareOriginals = ((RestoreCheckOptionsViewModel)comparisonWindow.FindControl<RestoreCheckOptions>("OptionsUi")!.DataContext!).ToOptions().CompareOriginals;
            comparisonWindow.Close();
        }
        Assert.True(restoreCheck.CompareOriginals);
        ServiceLocator.Config.Update(c => c.Backups[0].Checks.Single(x => x.Id == restoreCheck.Id).CompareOriginals = true);
        var mismatch = await ServiceLocator.Orchestrator.RunCheckAsync(saved, restoreCheck, deadline.Token);
        Assert.Equal(BackupRunStatus.Failed, mismatch.Status);
        Assert.Contains("Files recovered successfully", mismatch.Summary);
        Assert.Contains("does not establish which copy changed", mismatch.Summary);
        ServiceLocator.Config.Update(c => c.Backups[0].Checks.RemoveAll(x => x.Kind == CheckKind.TestRestore));
        var shallow = await ServiceLocator.Orchestrator.RunCheckAsync(saved, saved.Checks.First(c => c.Kind == CheckKind.Structure), deadline.Token);
        Assert.Equal(BackupRunStatus.Success, shallow.Status);
        await ctx.Driver.PumpAsync(300); card = cards.Cards.Single(); Assert.Equal("fail", card.VerifyClass);
        ctx.Snapshot(main, "shallow-success-keeps-comparison-failure", "After a same-size, same-date content mismatch, a later structure-only check succeeds. The card still says Check needs attention.");
        Click(main, FindButton(main, "Activity")); await ctx.Driver.PumpAsync(200);
        history = main.OwnedWindows.OfType<BackupHistoryWindow>().Single();
        Assert.Contains(((BackupHistoryViewModel)history.DataContext!).Rows, r => r.Status == BackupRunStatus.Failed);
        ctx.Snapshot(history, "failed-comparison-in-history", "The failed restore comparison and later successful repository check remain separate dated results."); history.Close();
        File.WriteAllText(changed, "original bytes"); File.SetLastWriteTimeUtc(changed, stamp);
        // A test restore after each backup (U179): its own check, run right after the backup ends.
        ServiceLocator.Config.Update(c => c.Backups[0].Checks.Add(new BackupCheck { Kind = CheckKind.TestRestore, SamplePercent = 100, MaximumBytes = 0, Schedule = null }));
        // Hold the real automatic comparison at a pause checkpoint so the
        // native-card entry point can be exercised while its parent run owns it.
        ActiveRestoreSession? inlineSession = null;
        void PauseAutomaticComparison()
        {
            if (inlineSession is not null) return;
            inlineSession = ServiceLocator.ActiveRestores.RunningTestFor(saved.Id);
            inlineSession?.TogglePause();
        }
        ServiceLocator.ActiveRestores.Changed += PauseAutomaticComparison;
        var inlineTask = ServiceLocator.Orchestrator.RunBackupAsync(ServiceLocator.Config.Current.Backups.Single(), deadline.Token);
        try
        {
            for (var n = 0; n < 3000 && inlineSession?.IsPaused != true && !inlineTask.IsCompleted; n++) await ctx.Driver.PumpAsync(10);
            Assert.NotNull(inlineSession); Assert.True(inlineSession.IsPaused);
            card = cards.Cards.Single();
            Assert.True(card.IsVerifying);
            // U205: followed and stopped on the card; Verify waits for it.
            var entry = main.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Verify"));
            Assert.False(entry.IsEnabled);
            Assert.Contains("A restore check is running", ToolTip.GetTip(entry)?.ToString());
            Assert.True(main.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StopButton").IsEffectivelyVisible);
            Assert.DoesNotContain(ServiceLocator.ActiveRestores.Sessions, s => s.IsTestRestore);
            // Since U179 the test restore after each backup is its own check
            // run, started when the backup ends: Backup health says it is
            // checking (U197), and Last backup keeps the backup's result.
            Assert.DoesNotContain(main.GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "RunningText" && t.IsEffectivelyVisible);
            var running = main.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "CheckRunText" && t.IsEffectivelyVisible);
            Assert.StartsWith("Restore check", running.Text);
            ctx.Snapshot(main, "automatic-comparison-phase", "After the backup ends, its test restore runs as a check: Backup health says Checking, Stop is shown, and Verify waits for it (its hover says a test restore is running).");
            await ctx.Driver.PumpAsync(1200); // Make omitted comparison time detectable in the whole-second headline.
            await inlineSession.TogglePauseAsync();
        }
        finally
        {
            ServiceLocator.ActiveRestores.Changed -= PauseAutomaticComparison;
            if (inlineSession?.IsPaused == true) await inlineSession.TogglePauseAsync();
        }
        var inline = await inlineTask;
        Assert.Equal(BackupRunStatus.Success, inline.Status);
        Assert.Contains(ServiceLocator.Logs.LoadHistoryByBackupId(saved.Id), r => r.Kind == RunKind.Check && r.CheckKind == CheckKind.TestRestore && r.StartedUtc >= inline.StartedUtc && r.Status == BackupRunStatus.Success);
        Assert.Equal(BackupOrchestrator.BuildSummary(inline, null, backup, ServiceLocator.Config.Current), inline.Summary);
        await ctx.Driver.PumpAsync(250);
        AppNavigator.Go(NavItem.Backups); await ctx.Driver.PumpAsync(150);
        Assert.Equal("ok", cards.Cards.Single().VerifyClass);
        Assert.False(cards.Cards.Single().IsRunning); Assert.False(cards.Cards.Single().IsVerifying);
        Assert.DoesNotContain("Testing restored files", cards.Cards.Single().OperationPhaseLabel);
        Assert.Contains("Run this backup's checks now", cards.Cards.Single().TestRestoreTooltip);
        var finalLabel = main.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == cards.Cards.Single().LastRunLabel);

        Assert.True(finalLabel.Bounds.Height < 50, "The detailed comparison result must not turn the card header into a paragraph.");
        ctx.Snapshot(main, "passing-recheck-clears-problem", "A new successful full restore comparison clears the problem. The after-backup check path also completed with real restic.");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var cancelled = Task.Run(() => ServiceLocator.TestRestores.RunScheduledAsync(saved, restoreCheck.RestoreOptions(), cancel.Token));
        for (var n = 0; n < 500 && !ServiceLocator.TestRestores.IsRunning(saved.Id) && !cancelled.IsCompleted; n++) await ctx.Driver.PumpAsync(5);
        Assert.True(ServiceLocator.TestRestores.IsRunning(saved.Id)); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(ServiceLocator.TestRestores.IsRunning(saved.Id));
        var interrupted = ServiceLocator.Logs.LoadTimelineByBackupId(saved.Id).First(r => r.Kind == RunKind.TestRestore);
        Assert.Equal(BackupRunStatus.Skipped, interrupted.Status);
        Assert.Contains("stopped before it finished", interrupted.Summary);
        Assert.True(interrupted.TriggeredAutomatically);
        Assert.True(ServiceLocator.Config.Current.Backups.Single().LastVerifyPass);
        for (var n = 0; n < 100 && cards.Cards.Single().VerifyClass != "stale"; n++) await ctx.Driver.PumpAsync(10);
        Assert.Equal("stale", cards.Cards.Single().VerifyClass);
        Assert.Equal("warn", cards.HealthClass);
        Assert.Equal(saved.Id, Assert.Single(cards.AffectedBackups).Id);
        Assert.DoesNotContain("Everything looks good", cards.HealthHeadline);
        Assert.DoesNotContain(main.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && (t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Any(r => r.Text == "Everything looks good") ?? false));
        ctx.Snapshot(main, "cancelled-check-keeps-banner-honest", "After cancelling a real comparison, both the backup card and the overall banner require attention. The earlier successful backup and full comparison are still saved, but do not turn this incomplete check green.");
        ctx.Report.RecordNote("Scope and limits", "Real encrypted local repository, real scheduled-check coordinator and after-backup path, actual history and options controls in headless Avalonia. Failed comparison, shallower success, full passing retry, and external cancellation checked. No Windows task registration, reboot, Credential Manager access or live cloud account used.");
    }
    private static Button FindButton(Window window, string text) => window.GetVisualDescendants().OfType<Button>()
        .Single(b => b.IsEffectivelyVisible && (b.Content is string label ? label
            : string.Join(" ", b.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text))) == text);
    private static void Click(Window window, Control control)
    {
        control.BringIntoView(); window.UpdateLayout(); Screenshots.ScreenshotDiffTests.SettleInitialTransitions(window);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
    }
}
