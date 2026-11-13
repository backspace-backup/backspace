using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// U229 (owner, 12 November 2026): "I checked Activity and open log for the
/// running one, and it let me to this which doesn't seem like what we're
/// checking. Pick a run is blank". The check ran in the background service
/// while Activity &amp; logs showed another backup: the service's run was
/// still the other backup's, so the running check wasn't listed when "Open
/// live log" looked for it. Now it is picked, titled with the check, and
/// the console shows its output only.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S107_LiveLogOfServiceCheck : ScenarioBase
{
    protected override string ScenarioName => "S107-live-log-of-service-check";
    [AvaloniaFact] public async Task Run_S107() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var localC = new Backup { Name = "local_c" };
        var localD = new Backup { Name = "local_d" };
        ServiceLocator.Config.Update(c => { c.Backups.Add(localC); c.Backups.Add(localD); });
        var now = DateTime.UtcNow;
        ServiceLocator.Logs.AppendRun(new RunRecord { BackupId = localD.Id, BackupName = localD.Name, Kind = RunKind.Check, CheckKind = CheckKind.DataSample,
            StartedUtc = now.AddMinutes(-20), EndedUtc = now.AddMinutes(-12), Status = BackupRunStatus.Success,
            Summary = "Data check (1 GiB sample) passed in 8 min 8 s." });
        ServiceLocator.Logs.AppendRun(new RunRecord { BackupId = localC.Id, BackupName = localC.Name,
            StartedUtc = now.AddMinutes(-13), EndedUtc = now.AddMinutes(-5), Status = BackupRunStatus.Skipped, Summary = "Stopped after 8m 10s" });

        // The service: local_d's check runs; local_c's backup ended.
        string Stamp(int secondsAgo, string line) => LogLineStamp.Stamp(line, now.AddSeconds(-secondsAgo));
        var tail = string.Join("\n",
            Stamp(400, "[section] local_d · Restore check (1% of files, at most 1 GiB)\tGoogle Drive - backspace-952aab70"),
            Stamp(390, "[progress] 0% · Preparing the files to restore · Google Drive - backspace-952aab70"),
            Stamp(60, "[progress] 40% · Restoring and comparing files · 1,215 of 2,922 files · Google Drive - backspace-952aab70"));
        var checkRun = new ServiceActivity.Snapshot(1, Guid.NewGuid().ToString("N"), "running", localD.Id, "check", now.AddMinutes(-21),
            new(localD.Id, 0, "", [], [], TranscriptTail: tail, TranscriptEnd: tail.Length,
                Activity: "Restore check (1% of files, at most 1 GiB) (2 of 2)"));
        var backupRun = new ServiceActivity.Snapshot(1, Guid.NewGuid().ToString("N"), "completed", localC.Id, "backup", now.AddMinutes(-13), null);
        var activity = checkRun with { Runs = [checkRun, backupRun] };
        using var monitor = new BackgroundServiceActivity(_ => Task.FromResult<ServiceActivity.Snapshot?>(activity), (_, _) => Task.CompletedTask);
        using var vm = new LogsViewModel(monitor);
        var view = new LogsView { DataContext = vm };
        var window = ctx.Driver.Show(new Window { Width = 1180, Height = 720, Content = view });
        // Activity & logs was on local_c.
        vm.SelectedBackup = "local_c";
        await monitor.RefreshAsync(CancellationToken.None);
        await ctx.Driver.PumpAsync(100);
        Assert.DoesNotContain(vm.History, r => r.Status == BackupRunStatus.Running);

        // local_d's Activity, "Open live log".
        vm.ShowLive(localD.Id);
        await ctx.Driver.PumpAsync(200);
        Assert.Equal("local_d", vm.SelectedBackup);
        Assert.NotNull(vm.SelectedRun);
        Assert.Equal(BackupRunStatus.Running, vm.SelectedRun!.Status);
        Assert.Equal(localD.Id, vm.SelectedRun.BackupId);
        Assert.Same(vm.SelectedRun, view.FindControl<ComboBox>("RunPicker")!.SelectedItem);
        Assert.True(vm.IsShowingLive);
        Assert.Equal("local_d · Restore check (1% of files, at most 1 GiB) (2 of 2)", vm.DisplayContext);
        Assert.Contains("1,215 of 2,922 files", vm.DisplayText);
        // Its own output, not "combined output".
        Assert.False(view.FindControl<TextBlock>("CombinedOutputHint")!.IsEffectivelyVisible);
        // A later poll keeps it.
        await monitor.RefreshAsync(CancellationToken.None);
        await ctx.Driver.PumpAsync(100);
        Assert.Equal(BackupRunStatus.Running, vm.SelectedRun?.Status);
        ctx.Snapshot(window, "live log of the service's check",
            "After \"Open live log\" from local_d's Activity while Activity & logs showed local_c: local_d selected, \"Restore check … (2 of 2)\" picked in Run, the header names the check, and the console shows its section and progress.");
        window.Close();
    }
}
