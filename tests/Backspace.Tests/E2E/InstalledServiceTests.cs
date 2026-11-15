using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Xunit;

namespace Backspace.Tests.E2E;

/// <summary>
/// U246 A (owner, 14 November 2026: Stop on a running backup did nothing and
/// no progress showed, because the window and the installed background
/// service did not understand each other; approved: "a GitHub test that
/// installs the real background service"). On a disposable Windows machine
/// with administrator rights, this installs the real service for a portable
/// copy of the release candidate, then, with the window's own code:
/// starts a backup in the service (Run now), follows its progress through the
/// service's activity, stops it (Stop on the card), checks that the run is
/// recorded as stopped by you, runs it again to completion, and removes the
/// service. Runs only when BACKSPACE_INSTALLED_SERVICE_ROOT names the copy's
/// folder (Backspace.exe beside an empty Backspace.config, which must also be
/// BACKSPACE_CONFIG_ROOT): never on a developer's computer by accident.
/// </summary>
public sealed class InstalledServiceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task The_window_runs_follows_and_stops_a_backup_in_the_installed_service()
    {
        var root = Environment.GetEnvironmentVariable("BACKSPACE_INSTALLED_SERVICE_ROOT");
        if (string.IsNullOrEmpty(root))
            Assert.Skip("Set BACKSPACE_INSTALLED_SERVICE_ROOT (on a disposable Windows machine) to install the real background service.");
        Assert.True(new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator), "Run as administrator.");
        root = Path.GetFullPath(root).TrimEnd('\\');
        var config = Path.Combine(root, "Backspace.config");
        Assert.True(File.Exists(Path.Combine(root, "Backspace.exe")), "No Backspace.exe in " + root);
        Assert.Equal(config, Path.GetFullPath(AppPaths.ConfigRoot).TrimEnd('\\'), ignoreCase: true);
        Assert.Null(SystemService.ForCopy(root));

        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var work = Path.Combine(Path.GetDirectoryName(root)!, "installed-service-work");
        var source = Path.Combine(work, "source");
        var repo = Path.Combine(work, "repo");
        Directory.CreateDirectory(source);
        // Enough data to stop the run while it reads: 2 GB that does not compress.
        var chunk = new byte[16 * 1024 * 1024];
        for (var file = 0; file < 16; file++)
        {
            await using var stream = File.Create(Path.Combine(source, $"data-{file:00}.bin"));
            for (var i = 0; i < 8; i++) { RandomNumberGenerator.Fill(chunk); await stream.WriteAsync(chunk, deadline.Token); }
        }

        var destination = new Destination { Name = "Test drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = repo };
        var backup = new Backup
        {
            Name = "service test", UseVss = false, VssDisabledAcknowledged = true, AbortOnMeteredNetwork = false,
            SourcePaths = { source }, Targets = { new() { DestinationId = destination.Id } },
        };
        var store = new ConfigStore();
        store.Load();
        store.Update(c => { c.Backups.Add(backup); c.Destinations.Add(destination); });

        // Settings > Administrator access, with Windows' approval replaced by
        // running the copy's own setup command as administrator.
        var setup = new WindowsBackgroundBackupSetup(root, config, elevatedForTest: args => RunCopy(root, args));
        // The window finds the service's pipe names beside its own Backspace.exe;
        // this test process runs from elsewhere.
        BackupServicePipe.NamesDirectoryOverride = SystemService.DataDirectoryFor(root);
        try
        {
            await setup.ApplyAsync();
            var system = SystemService.ForCopy(root);
            Assert.NotNull(system);
            Assert.True(system!.Activated, "The service did not confirm its setup.");
            Assert.True(ServiceCommandLineRun.IsActive(root) || ServiceCommandLineRun.FindBinding(root, AppPaths.ConfigRoot) is not null,
                "The window does not see the service as set up.");
            output.WriteLine($"Installed {system.ServiceName} for {root}");

            var monitor = BackgroundServiceActivity.ForDesktop(root);
            Assert.NotNull(monitor);

            // Run now: the window hands the backup to the service.
            var run = Task.Run(() => ServiceCommandLineRun.TryRunFromDesktopAsync(root, backup, deadline.Token));
            var progress = await WaitForAsync(monitor!, backup.Id, s => s.State == "running" && s.Progress is { Percent: > 0 }, TimeSpan.FromMinutes(5), deadline.Token);
            output.WriteLine($"Progress seen: {progress.Progress!.Percent:F1}% ({progress.Progress.Activity})");

            // Stop on the card.
            await monitor!.CancelAsync(progress.RunId!, deadline.Token);
            Assert.True(await run.WaitAsync(TimeSpan.FromMinutes(3), deadline.Token), "Run now did not go through the service.");
            var stopped = await WaitForAsync(monitor, backup.Id, s => s.RunId == progress.RunId && s.State is not ("starting" or "running" or "stopping")
                && s.Progress?.Result is not null, TimeSpan.FromMinutes(3), deadline.Token);
            var stoppedRecord = stopped.Progress!.Result!;
            output.WriteLine($"Stopped: {stopped.State} · {stoppedRecord.Status} · {stoppedRecord.Summary}");
            Assert.True(stoppedRecord.StoppedByYou, "The stopped run was not recorded as stopped by you: " + stoppedRecord.Summary);
            Assert.Equal(BackupRunStatus.Skipped, stoppedRecord.Status);

            // Run now again: it completes in the service.
            Assert.True(await ServiceCommandLineRun.TryRunFromDesktopAsync(root, backup, deadline.Token));
            var completed = await WaitForAsync(monitor, backup.Id, s => s.RunId != progress.RunId && s.Progress?.Result is not null
                && s.State is not ("starting" or "running" or "stopping"), TimeSpan.FromMinutes(5), deadline.Token);
            var record = completed.Progress!.Result!;
            output.WriteLine($"Completed: {completed.State} · {record.Status} · {record.Summary}");
            Assert.Equal(BackupRunStatus.Success, record.Status);
            Assert.False(record.StoppedByYou);
        }
        finally
        {
            // Settings > Administrator access off: the service is removed.
            if (SystemService.ForCopy(root) is not null) await setup.RemoveAsync();
            Assert.Null(SystemService.ForCopy(root));
            BackupServicePipe.NamesDirectoryOverride = null;
            output.WriteLine("Service removed");
        }
    }

    /// <summary>The activity of this backup's run once it matches, as the card reads it.</summary>
    private async Task<ServiceActivity.Snapshot> WaitForAsync(BackgroundServiceActivity monitor, string backupId,
        Func<ServiceActivity.Snapshot, bool> done, TimeSpan limit, CancellationToken token)
    {
        var until = DateTime.UtcNow + limit;
        ServiceActivity.Snapshot? last = null;
        while (DateTime.UtcNow < until)
        {
            await monitor.RefreshAsync(token);
            last = ServiceActivity.ForBackup(monitor.Current, backupId);
            if (last is not null && last.BackupId == backupId && done(last)) return last;
            await Task.Delay(250, token);
        }
        throw new TimeoutException($"The service's activity did not reach the expected state; last: {last?.State} {last?.Progress?.Percent} {monitor.Error}");
    }

    private void RunCopy(string root, string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(root, "Backspace.exe")) { UseShellExecute = false, WorkingDirectory = root };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        var failure = Path.Combine(root, "Backspace.config", Program.SetupFailureFile);
        Assert.True(process.ExitCode == 0, $"Backspace.exe {args[0]} exited with {process.ExitCode}: " + (File.Exists(failure) ? File.ReadAllText(failure) : ""));
        output.WriteLine($"Backspace.exe {args[0]}: done");
    }
}
