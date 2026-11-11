using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

[Collection(ScenarioCollection.Name)]
public sealed class E54_PlanUpdatesAndCleanupSchedule(ITestOutputHelper output) : BackupE2EBase(output)
{
    protected override string ScenarioName => "E54-plan-updates-and-cleanup-schedule";
    [AvaloniaFact] public Task Run_E54() => Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = deadline.Token;
        var source = Path.Combine(ctx.SourcesDir, "small-source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "kept.txt"), "keep this");
        File.WriteAllText(Path.Combine(source, "excluded.txt"), "initially included");
        var destination = new Destination { Name = "Disposable settings repository", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("repo"), Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e54", Guid.NewGuid().ToString("N")) };
        var backup = new Backup { Name = "Edited settings", SourcePaths = { source }, UseVss = false,
            VssDisabledAcknowledged = true, PruneSchedule = new(), CheckSchedule = new(), KeepPolicy = "1",
            Targets = { new() { DestinationId = destination.Id } } };
        ServiceLocator.Config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(destination); });
        var sibling = new Backup { Name = "Other backup", SourcePaths = { source }, UseVss = false,
            VssDisabledAcknowledged = true, PruneSchedule = new(), CheckSchedule = new(), KeepPolicy = "1",
            Targets = { new() { DestinationId = destination.Id } } };
        // Backups and the cleanup after them run restic directly (U175).
        var direct = new System.Collections.Concurrent.ConcurrentQueue<ResticCommand>();
        fixture.Engine.ResticCommandFinished += (command, result) => { if (result.Succeeded) direct.Enqueue(command); };
        for (var i = 0; i < 2; i++)
            Assert.Equal(BackupRunStatus.Success, (await fixture.Engine.BackupPruneCheckAsync(sibling, source, destination, "default", ct)).Status);
        var siblingIds = (await fixture.Engine.ListSnapshotsAsync(sibling, source, destination, "default", ct)).Select(s => s.SnapshotId).ToArray();
        async Task BackUp()
        {
            var run = await fixture.Engine.BackupPruneCheckAsync(backup, source, destination, "default", ct);
            Assert.True(run.Status == BackupRunStatus.Success, run.Summary);
        }
        Task<System.Collections.Generic.IReadOnlyList<RevisionSummary>> Snapshots() =>
            fixture.Engine.ListSnapshotsAsync(backup, source, destination, "default", ct);
        await BackUp();
        await BackUp();
        Assert.Equal(2, (await Snapshots()).Count);
        ctx.Report.RecordNote("Independent cleanup does not run during backup", "With retention set to keep one and cleanup scheduled separately, two real backups kept both snapshots. Previously Backrest removed the older one during the second backup.");

        backup.FiltersText = "excluded.txt";
        backup.KeepPolicy = "2";
        backup.BandwidthLimitKBps = 512;
        var planId = ResticBackrestEngine.MakePlanId(backup, source, destination, "default");
        var excludeFile = ResticBackrestEngine.ExcludeFilePath(planId);
        if (OperatingSystem.IsWindows())
        {
            // Another program holding the exclude file (a scanner, Dropbox):
            // the backup waits for it (U135), then reads the edited rules.
            using var held = new FileStream(excludeFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            var editingBackup = Task.Run(BackUp);
            await Task.Delay(250, ct);
            Assert.False(editingBackup.IsCompleted);
            held.Dispose();
            await editingBackup;
        }
        else await BackUp();
        // restic gets the exclude file, holding the edited text as written (U151).
        var edited = direct.Last(c => c.Arguments[0] == "backup");
        var arguments = ResticRunner.Arguments(edited).ToList();
        Assert.Equal(excludeFile, arguments[arguments.IndexOf(FilterTranslator.ExcludeFileFlag) + 1]);
        Assert.Equal("512", arguments[arguments.LastIndexOf("--limit-upload") + 1]);
        Assert.Equal("excluded.txt", File.ReadAllText(excludeFile));
        var snapshots = await Snapshots();
        Assert.Equal(3, snapshots.Count);
        var files = await fixture.Engine.ListSnapshotFilesAsync(backup, source, destination, "default", snapshots[0].Number, ct, snapshots[0].SnapshotId);
        Assert.Equal("kept.txt", Assert.Single(files).Path);
        var oldFiles = await fixture.Engine.ListSnapshotFilesAsync(backup, source, destination, "default", snapshots[^1].Number, ct, snapshots[^1].SnapshotId);
        Assert.Equal(2, oldFiles.Count);
        ctx.Report.RecordNote("Edited exclusion reaches the next backup", "The third backup omitted excluded.txt while the earlier snapshot still lists both files. On Windows the exclude file was held open against replacement for 250 ms during the edit; the backup waited, then restic got the edited file and the new bandwidth limit on its command line.");

        var cleanup = await fixture.Engine.RunPruneOnlyAsync(backup, source, destination, "default", ct);
        Assert.True(cleanup.Status == BackupRunStatus.Success, cleanup.Summary);
        Assert.Equal(2, (await Snapshots()).Count);
        ctx.Report.RecordNote("Explicit cleanup uses the edited retention", "Running cleanup kept two snapshots, using the new setting rather than the original keep-one policy.");

        await BackUp();
        Assert.Equal(3, (await Snapshots()).Count);
        backup.PruneSchedule = null;
        await BackUp();
        Assert.Equal(2, (await Snapshots()).Count);
        // The explicit cleanup still goes through Backrest (step 2 moves it);
        // the inline one ran restic directly. One each, nothing automatic.
        var config = await fixture.Sidecar.Client!.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>("/v1.Backrest/GetConfig", new(), ct);
        Assert.Equal(2, config.Plans.Count); // The edited plan and the untouched sibling, with no duplicates.
        var operations = await fixture.Sidecar.Client.CallAsync<GetOperationsRequest, BackrestOperationList>("/v1.Backrest/GetOperations", new()
        { Selector = new() { RepoGuid = config.Repos.Single().Guid }, LastN = 256 }, ct);
        Assert.Equal(1, operations.Operations.Count(op => op.Status == "STATUS_SUCCESS" && op.OperationRunCommand?.Command.StartsWith("forget --tag ", StringComparison.Ordinal) == true));
        Assert.DoesNotContain(operations.Operations, op => op.OperationForget is not null || op.OperationBackup is not null);
        Assert.Single(direct, c => c.Arguments[0] == "forget");
        Assert.Equal(siblingIds, (await fixture.Engine.ListSnapshotsAsync(sibling, source, destination, "default", ct)).Select(s => s.SnapshotId));
        ctx.Report.RecordNote("Inline cleanup runs once and leaves other backups alone", "Switching cleanup to after each backup kept two snapshots. Exactly two successful cleanup commands ran overall: the explicit cleanup (through Backrest) and this inline cleanup (restic directly). No automatic Forget task ran, and Backrest ran no backup. Both snapshots of another backup in the same repository survived unchanged.");
    }
}
