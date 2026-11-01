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
        var configPath = Path.Combine(fixture.Sidecar.DataDir, "config.json");
        if (OperatingSystem.IsWindows())
        {
            using var held = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var editingBackup = BackUp();
            await Task.Delay(250, ct);
            held.Dispose();
            await editingBackup;
        }
        else await BackUp();
        using (var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath)))
            Assert.Contains(saved.RootElement.GetProperty("plans").EnumerateArray(), plan =>
                plan.GetProperty("id").GetString() == ResticBackrestEngine.MakePlanId(backup, source, destination, "default") &&
                plan.GetProperty(OperatingSystem.IsWindows() ? "iexcludes" : "excludes").EnumerateArray().Any(value => value.GetString() == source.Replace('\\', '/') + "/**/excluded.txt"));
        var snapshots = await Snapshots();
        Assert.Equal(3, snapshots.Count);
        var files = await fixture.Engine.ListSnapshotFilesAsync(backup, source, destination, "default", snapshots[0].Number, ct, snapshots[0].SnapshotId);
        Assert.Equal("kept.txt", Assert.Single(files).Path);
        var oldFiles = await fixture.Engine.ListSnapshotFilesAsync(backup, source, destination, "default", snapshots[^1].Number, ct, snapshots[^1].SnapshotId);
        Assert.Equal(2, oldFiles.Count);
        ctx.Report.RecordNote("Edited exclusion reaches the existing plan", "The third backup omitted excluded.txt while the earlier snapshot still lists both files. On Windows the daemon's configuration was held open against replacement for 250 ms during the edit; the operation recovered and the new exclusion was verified in the saved configuration file.");

        var cleanup = await fixture.Engine.RunPruneOnlyAsync(backup, source, destination, "default", ct);
        Assert.True(cleanup.Status == BackupRunStatus.Success, cleanup.Summary);
        Assert.Equal(2, (await Snapshots()).Count);
        ctx.Report.RecordNote("Explicit cleanup uses the edited retention", "Running cleanup kept two snapshots, using the new setting rather than the original keep-one policy.");

        await BackUp();
        Assert.Equal(3, (await Snapshots()).Count);
        backup.PruneSchedule = null;
        await BackUp();
        Assert.Equal(2, (await Snapshots()).Count);
        var config = await fixture.Sidecar.Client!.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>("/v1.Backrest/GetConfig", new(), ct);
        var editedPlan = Assert.Single(config.Plans.Where(p => p.Id == ResticBackrestEngine.MakePlanId(backup, source, destination, "default")));
        Assert.Contains("--limit-upload", editedPlan.BackupFlags);
        Assert.Contains("512", editedPlan.BackupFlags);
        Assert.Equal(2, config.Plans.Count); // The edited plan and the untouched sibling, with no duplicates.
        var operations = await fixture.Sidecar.Client.CallAsync<GetOperationsRequest, BackrestOperationList>("/v1.Backrest/GetOperations", new()
        { Selector = new() { RepoGuid = config.Repos.Single().Guid }, LastN = 256 }, ct);
        Assert.Equal(2, operations.Operations.Count(op => op.Status == "STATUS_SUCCESS" && op.OperationRunCommand?.Command.StartsWith("forget --tag ", StringComparison.Ordinal) == true));
        Assert.DoesNotContain(operations.Operations, op => op.OperationForget is not null);
        Assert.Equal(siblingIds, (await fixture.Engine.ListSnapshotsAsync(sibling, source, destination, "default", ct)).Select(s => s.SnapshotId));
        ctx.Report.RecordNote("Inline cleanup runs once and leaves other backups alone", "Switching cleanup to after each backup kept two snapshots. The daemon recorded exactly two successful cleanup commands overall: the explicit cleanup and this inline cleanup. No automatic Forget task ran. Both snapshots of another backup in the same repository survived unchanged.");
    }
}
