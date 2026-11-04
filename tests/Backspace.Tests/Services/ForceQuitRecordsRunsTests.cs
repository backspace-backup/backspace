using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U126 (4 November 2026): "Can you confirm all the logs will be flushed if we
/// force quit through the systray?" Force quit cancelled a backup running in
/// the app and exited at once, so the run could be missing from its history.
/// It now waits (up to 5 s) for each cancelled run to record how it ended.
/// </summary>
[Collection(Backspace.Tests.Scenarios.ScenarioCollection.Name)]
public sealed class ForceQuitRecordsRunsTests
{
    [Fact]
    public async Task A_run_stopped_by_force_quit_is_in_its_history_before_Backspace_exits()
    {
        if (ServiceLocator.Engine is not InMemoryBackupEngine engine) return;
        using var workspace = new TempWorkspace("force-quit-records");
        var source = Directory.CreateDirectory(Path.Combine(workspace.Root, "source")).FullName;
        File.WriteAllText(Path.Combine(source, "a.txt"), "a");
        var destination = new Destination { Name = "Local (force quit)", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = Directory.CreateDirectory(Path.Combine(workspace.Root, "dest")).FullName };
        var backup = new Backup { Name = "Force quit test", SourcePaths = { source }, UseVss = false, VssDisabledAcknowledged = true,
            PruneSchedule = new BackupSchedule(), CheckSchedule = new BackupSchedule(),
            Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } } };
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(destination); cfg.Backups.Add(backup); });
        var lines = engine.ScriptedBackupLines;
        // A backup long enough to still be running when Force quit comes.
        engine.ScriptedBackupLines = Enumerable.Range(0, 200).Select(i => $"[fake] chunk {i}").ToArray();
        try
        {
            var run = Task.Run(() => ServiceLocator.Orchestrator.RunBackupAsync(backup, CancellationToken.None));
            Assert.True(SpinWait.SpinUntil(() => ServiceLocator.Orchestrator.IsRunning(backup.Id), 5000));
            await Task.Delay(300);
            Assert.Empty(ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id));

            var watch = System.Diagnostics.Stopwatch.StartNew();
            await ServiceLocator.Orchestrator.CancelAndRecordRunsAsync(
                () => ServiceLocator.Orchestrator.CancelRun(backup.Id, "User quit Backspace"), TimeSpan.FromSeconds(5), "Backspace was force quit");
            // Already recorded when Force quit continues to exit, well within the limit.
            var saved = Assert.Single(ServiceLocator.Logs.LoadHistoryByBackupId(backup.Id));
            Assert.NotEqual(BackupRunStatus.Success, saved.Status);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"Waited {watch.Elapsed.TotalSeconds:0.0} s.");
            Assert.False(ServiceLocator.Orchestrator.IsRunning(backup.Id));
            Assert.Equal(saved.Status, ServiceLocator.Config.Current.Backups.Single(b => b.Id == backup.Id).LastRunStatus);
            await run;
        }
        finally
        {
            engine.ScriptedBackupLines = lines;
            ServiceLocator.Config.Update(cfg => { cfg.Backups.RemoveAll(b => b.Id == backup.Id); cfg.Destinations.RemoveAll(d => d.Id == destination.Id); });
        }
    }
}
