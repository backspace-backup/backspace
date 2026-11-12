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
/// U197–U200 (11 November 2026), the owner's three backups as in their
/// screenshots. A running check shows under Backup health with the turning
/// icon, its name in short, a bar of how far it got and its step; the hover
/// has everything; Last backup keeps the last backup's result (U197, U198).
/// With several kinds of problem the banner says only "N backups need
/// attention" and each pill's hover says why: a skipped backup is not a
/// check issue (U199). A backup that has stored nothing yet says why it
/// has no checks, calmly (U200).
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S102_CheckUnderBackupHealth : ScenarioBase
{
    protected override string ScenarioName => "S102-check-under-backup-health";

    [AvaloniaFact] public async Task Run_S102() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var now = DateTime.UtcNow;
        var drive = new Destination { Name = "Google Drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("drive") };
        var dropbox = new Destination { Name = "M:\\", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("m") };
        Backup Make(string name, Destination dest, BackupRunStatus status, string summary)
        {
            var b = new Backup { Name = name, SourcePaths = { ctx.MakeSource(name, targetBytes: 4096) },
                LastRunStatus = status, LastRunStartUtc = now.AddHours(-3), LastRunEndUtc = now.AddHours(-3).AddSeconds(40), LastRunSummary = summary,
                LastSuccessUtc = status == BackupRunStatus.Success ? now.AddHours(-3) : null,
                Checks = { new BackupCheck { Kind = CheckKind.DataSample, MaximumBytes = BackupCheck.OneGiB },
                           new BackupCheck { Kind = CheckKind.TestRestore, SamplePercent = 1, MaximumBytes = BackupCheck.OneGiB } } };
            b.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
            return b;
        }
        var localC = Make("local_c", drive, BackupRunStatus.Success, "Backup completed");
        var localD = Make("local_d", drive, BackupRunStatus.Success, "Backup completed");
        var localDropbox = Make("local_dropbox", dropbox, BackupRunStatus.Skipped, "M:\\ not mounted");
        localD.Checks.RemoveAt(1); // its data sample alone, passed below
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(drive); cfg.Destinations.Add(dropbox);
            cfg.Backups.Add(localC); cfg.Backups.Add(localD); cfg.Backups.Add(localDropbox);
        });
        // local_c: its last data sample did not finish. local_d: it passed.
        ServiceLocator.Logs.AppendRun(new RunRecord { BackupId = localC.Id, BackupName = localC.Name, Kind = RunKind.Check, CheckKind = CheckKind.DataSample,
            StartedUtc = now.AddHours(-2), EndedUtc = now.AddHours(-2).AddMinutes(5), Status = BackupRunStatus.Warning, Summary = "The data sample was stopped before it finished." });
        ServiceLocator.Logs.AppendRun(new RunRecord { BackupId = localD.Id, BackupName = localD.Name, Kind = RunKind.Check, CheckKind = CheckKind.DataSample,
            StartedUtc = now.AddHours(-2), EndedUtc = now.AddHours(-2).AddMinutes(5), Status = BackupRunStatus.Success, Summary = "No errors were found." });

        using var main = new MainWindowViewModel();
        var host = ctx.Driver.Show(new MainWindow { Width = 1300, Height = 900, DataContext = main });
        var backups = host.GetVisualDescendants().OfType<BackupsView>().Single();
        var vm = (BackupsViewModel)backups.DataContext!;
        BackupCardViewModel Card(Backup b) => vm.Cards.Single(c => c.Id == b.Id);
        BackupCard View(Backup b) => host.GetVisualDescendants().OfType<BackupCard>().Single(v => v.DataContext == Card(b));
        for (var i = 0; i < 100 && (Card(localC).VerifyClass != "stale" || Card(localDropbox).VerifyClass == "loading"); i++) await ctx.Driver.PumpAsync(30);
        await ctx.Driver.PumpAsync(200);

        // U199: each reason with its own count.
        Assert.Equal("stale", Card(localC).VerifyClass);
        Assert.True(vm.HealthHeadline == "2 backups need attention", string.Join(" | ", vm.Cards.Select(c => $"{c.Name}: {c.VerifyClass} {c.VerifyLabel} / {c.StatusClass} {c.DisplayedRunStatus}")) + " :: " + vm.HealthDetail);
        // Only the headline and the pills: each pill says why on hover; the
        // reasons beside the headline wrapped onto a second line.
        Assert.Equal("", vm.HealthDetail);
        Assert.Equal("A check is incomplete, overdue, or unavailable. Click to jump to this backup's card.", vm.AffectedBackups.Single(a => a.Name == "local_c").Tooltip);
        Assert.Equal("Its last run was skipped. Click to jump to this backup's card.", vm.AffectedBackups.Single(a => a.Name == "local_dropbox").Tooltip);
        // U200: nothing stored yet, so nothing to check: calm, with the reason.
        Assert.Equal("No checks yet: nothing backed up so far", Card(localDropbox).VerifyLabel);
        Assert.Equal("never", Card(localDropbox).VerifyClass);
        Assert.StartsWith("This backup has not stored anything yet", Card(localDropbox).VerifyDetails);
        // A backup that has stored data keeps its plain wording.
        Assert.Equal("ok", Card(localD).VerifyClass);
        ctx.Snapshot(host, "banner and no checks yet",
            "The banner: \"2 backups need attention\" on one line with the local_c and local_dropbox pills; each pill's hover says why (a check incomplete, overdue, or unavailable; its last run was skipped). local_dropbox, never backed up because M:\\ is not mounted, says \"No checks yet: nothing backed up so far\" in grey.");

        // U197/U198: local_d's data sample runs, as the orchestrator reports it.
        var progress = ServiceLocator.Progress;
        var cardD = Card(localD);
        var lastBackup = cardD.LastRunLabel;
        cardD.UtcNow = () => DateTime.UtcNow.AddMinutes(7).AddSeconds(5);
        progress.ReportActivity(localD.Id, "Structure check + data sample (1 GiB) (1 of 2)");
        progress.ReportActivityStep(localD.Id, "Reading the data sample · 20 of 64 packs", "Google Drive", 52);
        for (var i = 0; i < 50 && !cardD.IsChecking; i++) await ctx.Driver.PumpAsync(20);
        await ctx.Driver.PumpAsync(100);
        Assert.True(cardD.IsChecking);
        var view = View(localD);
        // Last backup keeps the last backup's result: no "Checking" there.
        Assert.Equal(lastBackup, cardD.LastRunLabel);
        Assert.False(view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "RunningText").IsEffectivelyVisible);
        Assert.Equal("ok", cardD.StatusClass);
        var label = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "CheckRunText");
        Assert.True(label.IsEffectivelyVisible);
        Assert.Equal("Checking · Structure + data sample (1 of 2) · 7 min", label.Text);
        var bar = view.GetVisualDescendants().OfType<ProgressBar>().Single(b => b.Name == "CheckProgressBar");
        Assert.True(bar.IsEffectivelyVisible);
        Assert.False(bar.IsIndeterminate);
        Assert.Equal(52, bar.Value);
        Assert.Equal("Reading the data sample · 20 of 64 packs", view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "CheckStepText").Text);
        var summary = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "CheckSummary");
        Assert.True(summary.Classes.Contains("running"));
        var tip = Assert.IsType<string>(ToolTip.GetTip(view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "CheckRunning")));
        Assert.StartsWith("Structure check + data sample (1 GiB) (1 of 2)\nOn Google Drive\nReading the data sample · 20 of 64 packs\nAbout 52% of this check done\nRunning for 7 min\n\n", tip);
        Assert.Contains("downloads a sample of the stored data", tip);
        Assert.DoesNotContain("0.0%", tip);
        ctx.Snapshot(host, "a check running under backup health",
            $"local_d's data sample runs: Backup health shows the turning icon and \"{label.Text}\"; below the sources and destinations, where a backup's progress shows, a bar at 52% and \"Reading the data sample · 20 of 64 packs\" (U204). Last backup keeps \"{lastBackup}\". Hover: \"{tip.Replace("\n", " / ")}\".");

        host.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Screenshots.ScreenshotDiffTests.SettleInitialTransitions(host);
        await ctx.Driver.PumpAsync(100);
        ctx.Snapshot(host, "a check running, dark theme", "The same in the dark theme: Backup health in blue with the check, the bar and step below the sources; Last backup keeps its green result.");
        host.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;

        // Before restic gives a count: the bar moves without a percentage.
        progress.ReportActivity(localD.Id, "Sample test restore (1%, at most 1 GiB) (2 of 2)");
        await ctx.Driver.PumpAsync(150);
        Assert.Equal("Checking · Sample test restore (2 of 2) · 7 min", label.Text);
        Assert.True(bar.IsIndeterminate);
        Assert.False(view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "CheckStepText").IsVisible);

        // Two destinations: the step says which one.
        progress.ReportActivityStep(localD.Id, "Restoring and comparing files · 120 of 400 files", "Google Drive (2 of 2)", 80);
        await ctx.Driver.PumpAsync(150);
        Assert.Equal("Google Drive (2 of 2) · Restoring and comparing files · 120 of 400 files", cardD.CheckStepLine);

        // The check ends: Backup health shows its result again.
        progress.ReportActivity(localD.Id, "");
        for (var i = 0; i < 50 && cardD.IsChecking; i++) await ctx.Driver.PumpAsync(20);
        Assert.False(cardD.IsChecking);
        Assert.False(label.IsEffectivelyVisible);
        Assert.Equal("ok", cardD.HealthTileClass);
        // Through the background service: local_c's backup ends and its
        // check starts in the same job. Last backup shows the backup that just
        // ended, read from the history the service wrote; the check's step
        // and bar come through the service's activity report.
        var cardC = Card(localC);
        var runId = Guid.NewGuid().ToString("N");
        var jobStart = DateTime.UtcNow.AddMinutes(-20);
        var running = new Backspace.Services.Platform.Windows.ServiceActivity.Snapshot(1, runId, "running", localC.Id, "backup", jobStart,
            new(localC.Id, 60, "", [], []));
        cardC.ApplyBackgroundActivity(running, null);
        await ctx.Driver.PumpAsync(100);
        Assert.True(cardC.ShowsRunInLastBackup);
        Assert.False(cardC.IsChecking);
        ServiceLocator.Logs.AppendRun(new RunRecord { BackupId = localC.Id, BackupName = localC.Name, StartedUtc = jobStart, EndedUtc = DateTime.UtcNow.AddMinutes(-1),
            Status = BackupRunStatus.Success, Summary = "12.5 MB uploaded" });
        var checking = running with { Progress = running.Progress! with { Activity = "Structure check + data sample (1 GiB) (1 of 2)",
            ActivityStep = "Checking the snapshots · 3 of 40 snapshots", ActivityWhere = "Google Drive", ActivityPercent = 14.5 } };
        cardC.ApplyBackgroundActivity(Backspace.Services.Platform.Windows.ServiceActivity.Parse(Backspace.Services.Platform.Windows.ServiceActivity.Encode(checking)), null);
        for (var i = 0; i < 100 && !cardC.LastRunLabel.Contains("12.5 MB", StringComparison.Ordinal); i++) await ctx.Driver.PumpAsync(20);
        Assert.True(cardC.IsChecking);
        Assert.False(cardC.ShowsRunInLastBackup);
        Assert.Equal("ok", cardC.StatusClass);
        Assert.Contains("12.5 MB uploaded", cardC.LastRunLabel);
        Assert.Equal("Checking the snapshots · 3 of 40 snapshots", cardC.CheckStepLine);
        Assert.Equal(14.5, cardC.CheckPercent);
        cardC.ApplyBackgroundActivity(checking with { State = "stopping" }, null);
        Assert.Equal("Stopping the check…", cardC.CheckStepLine);
        Assert.Contains("12.5 MB uploaded", cardC.LastRunLabel);
        // An out-of-range share is refused by the activity report's checks.
        Assert.Throws<System.IO.InvalidDataException>(() => Backspace.Services.Platform.Windows.ServiceActivity.Parse(
            Backspace.Services.Platform.Windows.ServiceActivity.Encode(checking with { Progress = checking.Progress! with { ActivityPercent = 140 } })));
        // U205: a test restore you start (Test restore on the card) shows
        // the same way: under Backup health, with its bar and step below.
        var cardDrop = Card(localD);
        var test = ServiceLocator.ActiveRestores.BeginTest(localD, "Sample test restore (1%)", DateTime.UtcNow.AddMinutes(-2), null);
        cardDrop.UtcNow = () => DateTime.UtcNow;
        cardDrop.IsVerifying = true;
        await ctx.Driver.PumpAsync(100);
        Assert.True(cardDrop.ShowsTestRestore);
        Assert.Equal("Test restore · 2 min", cardDrop.CheckRunLabel);
        Assert.False(cardDrop.CheckProgressKnown); // nothing counted yet
        Assert.Equal("Preparing the files to restore", cardDrop.CheckStepLine.Replace("Google Drive · ", ""));
        ServiceLocator.ActiveRestores.UpdateProgress(test, 400, 120, 1000, 300);
        for (var i = 0; i < 20; i++) { Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); await ctx.Driver.PumpAsync(50); }
        Assert.Equal(30, cardDrop.CheckPercent, 1);
        Assert.Equal("Restoring and comparing files · 120 of 400 files", cardDrop.CheckStepLine);
        Assert.Contains("Restores a sample of files", cardDrop.CheckTooltip);
        Assert.True(view.GetVisualDescendants().OfType<ProgressBar>().Single(b => b.Name == "CheckProgressBar").IsEffectivelyVisible);
        ctx.Snapshot(host, "a test restore running",
            $"A test restore started from local_d's card: Backup health says \"{cardDrop.CheckRunLabel}\", the strip below the sources shows 30% and \"{cardDrop.CheckStepLine}\".");
        ServiceLocator.ActiveRestores.End(test, RestoreTerminalStatus.Success, null);
        cardDrop.IsVerifying = false;
        await ctx.Driver.PumpAsync(100);
        Assert.False(cardDrop.ShowsTestRestore);
        Assert.False(cardDrop.ShowsCheckProgress);
        ctx.Report.RecordNote("Limits", "Headless Avalonia with the orchestrator's progress reports given directly; the real engine's counts are proven by CheckProgressTests (restic run on a local repository). The service relays the same fields through its activity report.");
        host.Close();
    }
}
