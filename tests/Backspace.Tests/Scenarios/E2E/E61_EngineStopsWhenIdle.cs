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
/// for as long as it was open. Since U175 there is no engine to keep running:
/// backups, browsing and restores run restic directly, and nothing of them is
/// left running once they are done. (The name is from when Backrest, the
/// engine then, was stopped once idle.)
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
        var self = Environment.ProcessId;
        {
            // The live view receives each line once (4 November 2026: every
            // progress line appeared twice during the owner's local_c run).
            var live = new System.Collections.Concurrent.ConcurrentQueue<string>();
            void Heard(string line) => live.Enqueue(line);
            ServiceLocator.Engine.LineWritten += Heard;
            RunRecord first;
            try { first = await Task.Run(() => ServiceLocator.Orchestrator.RunBackupAsync(backup, CancellationToken.None)); }
            finally { ServiceLocator.Engine.LineWritten -= Heard; }
            Assert.True(first.Status is BackupRunStatus.Success or BackupRunStatus.Warning, string.Join(" | ", first.Errors));
            var setup = live.Where(l => l.StartsWith("[setup] ") || l.StartsWith("[recap] ")).ToList();
            Assert.NotEmpty(setup);
            Assert.Equal(setup.Count, setup.Distinct().Count());
            Assert.Single(live, l => l.StartsWith("[orchestrator] attempt 1 starting"));
            Output.WriteLine($"Live lines heard once each: {setup.Count} setup/recap lines, {live.Count} lines in all");
            // The backup ran restic itself, and nothing of it is left running.
            Assert.Contains(live, l => l.Contains("\tdebug\tcommand: ", StringComparison.Ordinal) && l.Contains(" backup --json ", StringComparison.Ordinal));
            Assert.Empty(Children(self, "restic"));
            Assert.Empty(Children(self, "rclone"));
            Assert.Empty(Children(self, "backrest"));
            ctx.Report.RecordNote("Nothing left after a backup", "The backup ran restic directly; no restic, rclone or Backrest was left running.");

            // Browsing and restoring run restic directly too: nothing left running.
            var revisions = await ServiceLocator.Revisions.ListRevisionsAsync(backup, source, destination, "default", CancellationToken.None);
            var files = await ServiceLocator.Revisions.ListFilesAsync(backup, source, destination, "default", revisions[0].Number, CancellationToken.None);
            var target = ctx.MakeRestoreTarget("browse-restore");
            var picked = files.First(f => f.Path.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-')).Path;
            var restored = await ServiceLocator.Restore.RunAsync(new RestoreRequest { Backup = backup, SourcePath = source, Destination = destination,
                StorageName = "default", Revision = revisions[0].Number, Files = new[] { picked }, IncludePatterns = new[] { picked },
                TargetPath = target }, CancellationToken.None);
            Assert.True(restored.TerminalStatus == RestoreTerminalStatus.Success, restored.ErrorMessage);
            Assert.Equal(System.IO.File.ReadAllBytes(System.IO.Path.Combine(source, picked)), System.IO.File.ReadAllBytes(System.IO.Path.Combine(target, picked)));
            Assert.Empty(Children(self, "restic"));
            Assert.Empty(Children(self, "rclone"));
            Assert.Empty(Children(self, "backrest"));
            ctx.Report.RecordNote("Nothing left after browsing and a restore", $"Listing the snapshots ({revisions.Count}) and files ({files.Count}) and restoring {picked} ran restic directly; no restic, rclone or Backrest was left running.");

            // The next backup leaves nothing either.
            System.IO.File.WriteAllText(System.IO.Path.Combine(source, "after-idle.txt"), "second run");
            var second = await Task.Run(() => ServiceLocator.Orchestrator.RunBackupAsync(backup, CancellationToken.None));
            Assert.True(second.Status is BackupRunStatus.Success or BackupRunStatus.Warning, string.Join(" | ", second.Errors));
            Assert.Empty(Children(self, "restic"));
            Assert.Empty(Children(self, "rclone"));
            Output.WriteLine($"Second backup: {second.Status}, nothing left running");
        }
    }
}
