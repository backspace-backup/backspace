using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// U125 (4 November 2026): "make sure that restic and backrest and rclone
/// processes are no longer running once all backups are done (otherwise they
/// can use a lot of RAM for nothing)". The open app kept its engine running
/// for as long as it was open. Real engine: after a backup, an idle engine
/// is stopped with everything it started, not while a run needs it, and the
/// next backup starts it again.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E61_EngineStopsWhenIdle : BackupE2EBase
{
    public E61_EngineStopsWhenIdle(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E61-engine-stops-when-idle";

    [AvaloniaFact] public async Task Run_E61() => await Run();

    private static int[] Children(int parent, string name)
        => Process.GetProcessesByName(name).Where(p => OwnedBy(p.Id, parent)).Select(p => p.Id).ToArray();

    // Direct or indirect child of the engine process.
    private static bool OwnedBy(int pid, int ancestor)
    {
        for (var i = 0; i < 4 && pid > 0; i++)
        {
            var parent = Backspace.Services.Platform.Windows.EngineTransfer.ParentOf(pid);
            if (parent == ancestor) return true;
            pid = parent ?? 0;
        }
        return false;
    }

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = ctx.MakeSource("documents", seed: 161, targetBytes: 300_000);
        var destination = new Destination
        {
            Name = "Local", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("local"), Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e61", $"e61-{Guid.NewGuid():N}"),
        };
        var backup = new Backup
        {
            Name = "Documents (E61)", SourcePaths = { source }, UseVss = false, VssDisabledAcknowledged = true, Threads = 2,
            PruneSchedule = new BackupSchedule(), CheckSchedule = new BackupSchedule(),
            Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } },
        };
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(destination); cfg.Backups.Add(backup); });
        var idleAfter = EngineIdleStop.IdleAfter;
        try
        {
            var first = await Task.Run(() => ServiceLocator.Orchestrator.RunBackupAsync(backup, CancellationToken.None));
            Assert.True(first.Status is BackupRunStatus.Success or BackupRunStatus.Warning, string.Join(" | ", first.Errors));
            Assert.True(fixture.Sidecar.IsRunning);
            var engineId = fixture.Sidecar.ProcessId!.Value;

            // Not yet idle for long enough: left running.
            EngineIdleStop.IdleAfter = TimeSpan.FromHours(1);
            Assert.False(await EngineIdleStop.CheckAsync());
            Assert.True(fixture.Sidecar.IsRunning);

            // While a restore or test restore holds the engine: left running.
            EngineIdleStop.IdleAfter = TimeSpan.Zero;
            using (await ServiceLocator.Maintenance.AcquireReadAsync(CancellationToken.None))
                Assert.False(await EngineIdleStop.CheckAsync());
            Assert.True(fixture.Sidecar.IsRunning);

            // Idle: stopped, with no restic or rclone left behind.
            Assert.True(await EngineIdleStop.CheckAsync());
            Assert.False(fixture.Sidecar.IsRunning);
            Assert.True(Process.GetProcesses().All(p => p.Id != engineId || p.HasExited), "Backrest is still running.");
            Assert.Empty(Children(engineId, "restic"));
            Assert.Empty(Children(engineId, "rclone"));
            ctx.Report.RecordNote("Idle engine", $"Backrest (process {engineId}) stopped after the backup; no restic or rclone left.");
            Output.WriteLine($"Idle engine: Backrest (process {engineId}) stopped; restic left: {Children(engineId, "restic").Length}, rclone left: {Children(engineId, "rclone").Length}");

            // The next backup starts it again.
            System.IO.File.WriteAllText(System.IO.Path.Combine(source, "after-idle.txt"), "second run");
            var second = await Task.Run(() => ServiceLocator.Orchestrator.RunBackupAsync(backup, CancellationToken.None));
            Assert.True(second.Status is BackupRunStatus.Success or BackupRunStatus.Warning, string.Join(" | ", second.Errors));
            Assert.True(fixture.Sidecar.IsRunning);
            Assert.NotEqual(engineId, fixture.Sidecar.ProcessId);
            Output.WriteLine($"Second backup after the stop: {second.Status}, new engine process {fixture.Sidecar.ProcessId}");
        }
        finally { EngineIdleStop.IdleAfter = idleAfter; }
    }
}
