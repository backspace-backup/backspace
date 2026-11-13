using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// U229-U233 (owner, 12 November 2026), with the real engine: a data check
/// that meets a lock left by a stopped operation, then a restore check.
/// <list type="bullet">
/// <item>Each check keeps a log, named like the check, saying what it did and
/// how it ended (U233); the restore check's log holds every file checked.</item>
/// <item>The left-over lock is a warning, followed by "trying again", not an
/// error, and the check passes (U231).</item>
/// <item>No line shows restic's UTC time (U230).</item>
/// <item>The run picker lists each check once, readable (U232).</item>
/// </list>
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E66_CheckLogsAndRunPicker(ITestOutputHelper output) : BackupE2EBase(output)
{
    protected override string ScenarioName => "E66-check-logs-and-run-picker";
    [AvaloniaFact] public Task Run_E66() => Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var source = ctx.MakeSource("d", 6601, 3_000_000);
        var password = Guid.NewGuid().ToString("N");
        var dest = new Destination { Name = "Google Drive - backspace-952aab70", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("repo"), Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e66", password) };
        var dataCheck = new BackupCheck { Kind = CheckKind.DataSample };
        var restoreCheck = new BackupCheck { Kind = CheckKind.TestRestore, SamplePercent = 50, MaximumBytes = BackupCheck.OneGiB };
        var backup = new Backup { Name = "local_d", UseVss = false, VssDisabledAcknowledged = true, AbortOnMeteredNetwork = false,
            SourcePaths = { source }, Targets = { new() { DestinationId = dest.Id } }, PruneSchedule = new(),
            Checks = { dataCheck, restoreCheck } };
        ServiceLocator.Config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(dest); });
        Assert.Equal(BackupRunStatus.Success, (await ServiceLocator.Orchestrator.RunBackupAsync(backup, deadline.Token)).Status);

        // An operation stopped mid-way leaves its lock (the service restarted
        // during an update, 12 November): restic holding a lock, then killed.
        var start = new System.Diagnostics.ProcessStartInfo(AppPaths.ResticBinary) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["RESTIC_PASSWORD"] = password;
        foreach (var arg in new[] { "--repo", dest.PathOrSubpath!, "backup", "--stdin", "--stdin-filename", "held.txt", "--quiet" }) start.ArgumentList.Add(arg);
        using (var held = System.Diagnostics.Process.Start(start)!)
        {
            var locks = Path.Combine(dest.PathOrSubpath!, "locks");
            for (var i = 0; i < 200 && !Directory.EnumerateFiles(locks).Any(); i++) await Task.Delay(50, deadline.Token);
            Assert.NotEmpty(Directory.EnumerateFiles(locks));
            held.Kill(true);
            await held.WaitForExitAsync(deadline.Token);
        }
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(dest.PathOrSubpath!, "locks")));

        await ServiceLocator.Orchestrator.RunChecksAsync(backup, BackupCheck.InRunOrder(backup.Checks).ToList(), deadline.Token);
        var history = ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id);
        // Checks without a schedule also ran right after the backup: the latest are these.
        var data = history.Where(r => r.Kind == RunKind.Check && r.CheckKind == CheckKind.DataSample).MaxBy(r => r.StartedUtc)!;
        var restore = history.Where(r => r.Kind == RunKind.Check && r.CheckKind == CheckKind.TestRestore).MaxBy(r => r.StartedUtc)!;
        Assert.True(data.Status == BackupRunStatus.Success, data.Summary);
        Assert.True(restore.Status == BackupRunStatus.Success, restore.Summary);

        // U233: each check has its log.
        Assert.True(File.Exists(data.LogPath), "The data check kept no log.");
        Assert.True(File.Exists(restore.LogPath), "The restore check kept no log.");
        var dataLog = File.ReadAllText(data.LogPath);
        var restoreLog = File.ReadAllText(restore.LogPath);
        output.WriteLine(dataLog);
        Assert.Contains("[section] local_d · Data check (1 GiB sample)\tGoogle Drive - backspace-952aab70", dataLog);
        Assert.Contains("[recap] Data check (1 GiB sample) passed on «Google Drive - backspace-952aab70» in ", dataLog);
        Assert.Contains("[progress] ", dataLog);
        Assert.Contains("[section] local_d · Restore check (50% of files, at most 1 GiB)", restoreLog);
        Assert.Contains("[recap] Restore check (50% of files, at most 1 GiB) passed", restoreLog);
        Assert.Contains("[file] ", restoreLog);
        Assert.Contains("--- cell outcome ---", restoreLog);

        // U231: the left-over lock is a warning, then "trying again"; never an error.
        Assert.Contains("[backspace] The repository is locked. Removing locks left by operations that no longer run", dataLog);
        var lockLines = dataLog.Split('\n').Where(l => l.Contains("exit code 11", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(lockLines);
        Assert.All(lockLines, l => Assert.Contains("\twarn\t", l));
        Assert.DoesNotContain(dataLog.Split('\n'), l => l.Contains("\terror\t", StringComparison.Ordinal));

        // U230: shown without restic's UTC time.
        var shown = ConsoleLogParser.ParseText(dataLog);
        Assert.Contains(shown, l => l.Text.StartsWith("[restic] warn", StringComparison.Ordinal));
        Assert.DoesNotContain(shown, l => System.Text.RegularExpressions.Regex.IsMatch(l.Text, @"^\[restic\] \d{2}:\d{2}:\d{2}"));
        var lockShown = shown.First(l => l.Text.Contains("exit code 11", StringComparison.Ordinal));
        Assert.Equal(ConsoleLineKind.Warning, lockShown.Kind);
        Assert.NotNull(lockShown.Time);

        // U232: the picker lists each check once, the restore's own record folded in.
        using var logs = new LogsViewModel();
        logs.SelectedBackup = "local_d";
        Assert.DoesNotContain(logs.History, r => r.Kind == RunKind.TestRestore);
        Assert.Contains(logs.History, r => ReferenceEquals(r, logs.History.First(x => x.Id == restore.Id)));
        var rows = logs.History.Select(RunPickerRow.From).ToList();
        foreach (var row in rows) output.WriteLine($"{row.Title} | {row.When} | {row.Outcome} | {row.Detail}");
        Assert.Contains(rows, r => r.Title == "Restore check (50% of files, at most 1 GiB)" && r.Outcome.StartsWith("Passed · ", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Title == "Data check (1 GiB sample)" && r.IsOk);
        Assert.Contains(rows, r => r.Title == "Backup" && r.Outcome.StartsWith("Completed", StringComparison.Ordinal));

        var view = new LogsView { DataContext = logs };
        var window = ctx.Driver.Show(new Window { Content = view, Width = 1180, Height = 760 });
        logs.SelectedRun = logs.History.First(r => r.Id == data.Id);
        await ctx.Driver.PumpAsync(800);
        ctx.Snapshot(window, "data check log",
            "The data check's saved log: \"local_d · Data check (1 GiB sample)\" header, the lock left behind as an amber warning, \"The repository is locked. Removing locks … then trying again\", progress lines, then a green \"passed … in N s\". No UTC times.");
        logs.SelectedRun = logs.History.First(r => r.Id == restore.Id);
        await ctx.Driver.PumpAsync(800);
        ctx.Snapshot(window, "restore check log",
            "The restore check's saved log: its header, progress while restoring, a green \"passed\" line and what it recovered.");
        // The owner: "the individual file restore results should be only in
        // verbose"; "Ticking timestamps doesn't do anything. Verbose also
        // doesn't do anything" (logs without times or detail lines).
        Assert.DoesNotContain(logs.DisplayLines, l => l.Kind == ConsoleLineKind.File);
        Assert.Contains(logs.DisplayLines, l => l.Text.StartsWith("[backspace] Picks 50% of the files (at most 1 GiB) of the latest backup", StringComparison.Ordinal));
        Assert.StartsWith("[backspace] This checks a sample", logs.DisplayLines.Last(l => l.Kind != ConsoleLineKind.Meta && l.Kind != ConsoleLineKind.Summary && l.Kind != ConsoleLineKind.Blank).Text);
        logs.ShowVerbose = true;
        logs.ShowTimestamps = true;
        await ctx.Driver.PumpAsync(600);
        Assert.Contains(logs.DisplayLines, l => l.Kind == ConsoleLineKind.File);
        Assert.All(logs.DisplayLines.Where(l => l.Kind is ConsoleLineKind.File or ConsoleLineKind.Progress), l => Assert.NotNull(l.Time));
        ctx.Snapshot(window, "restore check log with Verbose and Timestamps",
            "Verbose and Timestamps ticked: every file checked appears (dim), each line with its local time in the left column.");
        logs.ShowVerbose = false;
        logs.ShowTimestamps = false;
        await ctx.Driver.PumpAsync(300);
        var picker = view.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "RunPicker");
        picker.IsDropDownOpen = true;
        await ctx.Driver.PumpAsync(400);
        ctx.Snapshot(window, "run picker",
            "The revamped run picker: each run on two lines — a coloured dot, its name (Restore check, Data check (1 GiB sample), Backup) and \"Today HH:mm\", then a coloured outcome pill with the duration and one line of detail. The restore's own record is not listed twice.");
        picker.IsDropDownOpen = false;
        window.Close();
    }
}
