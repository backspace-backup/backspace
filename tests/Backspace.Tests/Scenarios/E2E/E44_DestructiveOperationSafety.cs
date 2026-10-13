using System;
using System.Diagnostics;
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
public sealed class E44_DestructiveOperationSafety : BackupE2EBase
{
    public E44_DestructiveOperationSafety(ITestOutputHelper output) : base(output) { }
    protected override string ScenarioName => "E44-destructive-operation-safety";
    [AvaloniaFact] public async Task Run_E44() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = deadline.Token;
        var source = Path.Combine(ctx.SourcesDir, "source");
        Directory.CreateDirectory(Path.Combine(source, "left"));
        Directory.CreateDirectory(Path.Combine(source, "right"));
        var dest = new Destination { Name = "Shared disposable repository", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("shared"), Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e44", Guid.NewGuid().ToString("N")) };
        Backup MakeBackup(string name) => new() { Name = name, SourcePaths = { source }, UseVss = false, VssDisabledAcknowledged = true,
            PruneSchedule = new BackupSchedule(), CheckSchedule = new BackupSchedule(),
            Targets = { new BackupTarget { DestinationId = dest.Id, StorageName = "default" } } };
        var selected = MakeBackup("Selected backup");
        var sibling = MakeBackup("Other backup");
        ServiceLocator.Config.Update(c => { c.Destinations.Add(dest); c.Backups.Add(selected); c.Backups.Add(sibling); });
        for (var version = 1; version <= 3; version++)
        {
            File.WriteAllText(Path.Combine(source, "left", "same.txt"), "left-version-" + version);
            File.WriteAllText(Path.Combine(source, "right", "same.txt"), "right-version-" + version);
            File.WriteAllText(Path.Combine(source, "unique.txt"), "unique-version-" + version);
            Assert.Equal(BackupRunStatus.Success, (await fixture.Engine.BackupPruneCheckAsync(selected, source, dest, "default", ct)).Status);
            Assert.Equal(BackupRunStatus.Success, (await fixture.Engine.BackupPruneCheckAsync(sibling, source, dest, "default", ct)).Status);
        }
        var snapshots = await fixture.Engine.ListSnapshotsAsync(selected, source, dest, "default", ct);
        var selectedMiddle = snapshots.Single(s => s.Number == 2);
        var otherIds = (await fixture.Engine.ListSnapshotsAsync(sibling, source, dest, "default", ct)).Select(s => s.SnapshotId).ToArray();

        // Pruning an older revision changes the display number, never the selected identity.
        await fixture.Engine.PruneSnapshotIdAsync(selected, source, dest, "default", snapshots.Single(s => s.Number == 1).SnapshotId!, ct);
        var pinnedTarget = ctx.MakeRestoreTarget("pinned");
        var pinned = await ServiceLocator.Restore.RunAsync(new RestoreRequest { Backup = selected, SourcePath = source,
            Destination = dest, StorageName = "default", Revision = selectedMiddle.Number, SnapshotId = selectedMiddle.SnapshotId,
            Files = new[] { "unique.txt" }, TargetPath = pinnedTarget }, ct);
        Assert.Equal(RestoreTerminalStatus.Success, pinned.TerminalStatus);
        Assert.Equal("unique-version-2", File.ReadAllText(Path.Combine(pinnedTarget, "unique.txt")));
        ctx.Report.RecordNote("Selected snapshot stays selected", "After revision 1 was deleted, the original revision 2 still restored version-2 bytes instead of silently restoring version 3.");

        var latest = snapshots[0];
        var keepTarget = ctx.MakeRestoreTarget("do-not-replace");
        File.WriteAllText(Path.Combine(keepTarget, "unique.txt"), "USER ORIGINAL");
        var kept = await ServiceLocator.Restore.RunAsync(new RestoreRequest { Backup = selected, SourcePath = source,
            Destination = dest, StorageName = "default", Revision = latest.Number, SnapshotId = latest.SnapshotId,
            Files = new[] { "unique.txt", "left/same.txt" }, TargetPath = keepTarget, Overwrite = false }, ct);
        Assert.Equal(RestoreTerminalStatus.Warning, kept.TerminalStatus);
        Assert.Equal("USER ORIGINAL", File.ReadAllText(Path.Combine(keepTarget, "unique.txt")));
        Assert.Equal("left-version-3", File.ReadAllText(Path.Combine(keepTarget, "left", "same.txt")));
        ctx.Report.RecordNote("Existing files stay untouched", "With replacement turned off, an existing original retained its exact contents while a missing selected file was restored.");

        // A linked target or an ordinary missing child beneath it must be
        // refused before the production restore writes any temporary output.
        var linkParent = ctx.MakeRestoreTarget("linked-target-test");
        var linkedTarget = Path.Combine(linkParent, "redirect");
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe") {
            Arguments = $"/c mklink /J \"{linkedTarget}\" \"{keepTarget}\"", UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!)
        { await process.WaitForExitAsync(ct); Assert.Equal(0, process.ExitCode); }
        try
        {
            foreach (var target in new[] { linkedTarget, Path.Combine(linkedTarget, "new-child") })
                await Assert.ThrowsAsync<IOException>(() => ServiceLocator.Restore.RunAsync(new RestoreRequest {
                    Backup=selected, SourcePath=source, Destination=dest, StorageName="default", Revision=latest.Number,
                    SnapshotId=latest.SnapshotId, Files=new[] { "unique.txt" }, TargetPath=target }, ct));
            Assert.Equal("USER ORIGINAL", File.ReadAllText(Path.Combine(keepTarget, "unique.txt")));
            Assert.False(Directory.Exists(Path.Combine(keepTarget, "new-child")));
            Assert.Empty(Directory.GetDirectories(keepTarget, ".backspace-restore-*"));
        }
        finally { Directory.Delete(linkedTarget); }
        ctx.Report.RecordNote("Linked destinations fail before writing", "An actual Windows junction, and a missing child under that junction, were both refused by the production restore service. The original file and destination directory contents stayed unchanged; no temporary restore folder appeared there.");

        var flatTarget = ctx.MakeRestoreTarget("flat");
        var flat = await ServiceLocator.Restore.RunAsync(new RestoreRequest { Backup=selected, SourcePath=source,
            Destination=dest, StorageName="default", Revision=latest.Number, SnapshotId=latest.SnapshotId,
            Files=new[] { "left/same.txt" }, TargetPath=flatTarget, PreserveStructure=false }, ct);
        Assert.Equal(RestoreTerminalStatus.Success, flat.TerminalStatus);
        Assert.Equal("left-version-3", File.ReadAllText(Path.Combine(flatTarget, "same.txt")));
        Assert.False(Directory.Exists(Path.Combine(flatTarget, "left")));
        var duplicate = await ServiceLocator.Restore.RunAsync(new RestoreRequest { Backup=selected, SourcePath=source,
            Destination=dest, StorageName="default", Revision=latest.Number, SnapshotId=latest.SnapshotId,
            Files=new[] { "left/same.txt", "right/same.txt" }, TargetPath=flatTarget, PreserveStructure=false }, ct);
        Assert.Equal(RestoreTerminalStatus.Error, duplicate.TerminalStatus);
        Assert.Equal("left-version-3", File.ReadAllText(Path.Combine(flatTarget, "same.txt")));
        ctx.Report.RecordNote("Flattening refuses name collisions", "A single nested file restores at the chosen root. Two different files with the same basename produce a clear error without replacing the first file.");

        var prune = await fixture.Engine.PruneToLatestAsync(selected, source, dest, "default", ct);
        Assert.Equal(BackupRunStatus.Success, prune.Status);
        Assert.Single(await fixture.Engine.ListSnapshotsAsync(selected, source, dest, "default", ct));
        Assert.Equal(otherIds, (await fixture.Engine.ListSnapshotsAsync(sibling, source, dest, "default", ct)).Select(s => s.SnapshotId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Engine.RestoreFilesAsync(selected, source, dest, "default",
            selectedMiddle.Number, new[] { "unique.txt" }, pinnedTarget, null, ct, expectedSnapshotId:selectedMiddle.SnapshotId));
        Assert.Equal("unique-version-2", File.ReadAllText(Path.Combine(pinnedTarget, "unique.txt")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Engine.PruneSnapshotIdAsync(selected, source, dest, "default", otherIds[0]!, ct));
        ctx.Report.RecordNote("Retention is scoped and missing snapshots fail safely", "Keeping the latest selected backup preserved all three sibling snapshots. A removed selected snapshot was refused, and attempting to delete a sibling snapshot through the selected backup was refused.");

        var erased = await ServiceLocator.Cleaner.EraseBackupFromDestinationAsync(selected, dest, "default", ct);
        Assert.Empty(erased.Errors); Assert.Single(erased.DeletedSnapshotIds);
        Assert.Empty(await fixture.Engine.ListSnapshotsAsync(selected, source, dest, "default", ct));
        Assert.Equal(otherIds, (await fixture.Engine.ListSnapshotsAsync(sibling, source, dest, "default", ct)).Select(s => s.SnapshotId));
        var again = await ServiceLocator.Cleaner.EraseBackupFromDestinationAsync(selected, dest, "default", ct);
        Assert.Empty(again.Errors); Assert.Empty(again.DeletedSnapshotIds);
        ctx.Report.RecordNote("Real erase and repeat erase", "The actual encrypted restic snapshot disappeared, the other backup kept all three revisions, and a second erase correctly removed zero snapshots.");

        // Exclude the sibling from Backspace's configuration: its snapshots must
        // survive destination removal, just like snapshots made by another tool.
        ServiceLocator.Config.Update(c => c.Backups.RemoveAll(b => b.Id == sibling.Id));
        var unrelated = Path.Combine(dest.PathOrSubpath, "chunks");
        Directory.CreateDirectory(unrelated); File.WriteAllText(Path.Combine(unrelated,"user.txt"), "unrelated");
        var removed = await ServiceLocator.Cleaner.WipeEntireDestinationAsync(dest, ct);
        Assert.Empty(removed.Errors);
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(unrelated,"user.txt")));
        var siblingTarget = ctx.MakeRestoreTarget("sibling-survives");
        var siblingRestore = await fixture.Engine.RestoreFilesAsync(sibling, source, dest, "default", 1,
            new[] { "unique.txt" }, siblingTarget, null, ct, expectedSnapshotId:otherIds.Last());
        Assert.Equal(RestoreTerminalStatus.Success, siblingRestore.Status);
        Assert.Equal("unique-version-1", File.ReadAllText(Path.Combine(siblingTarget,"unique.txt")));
        ctx.Report.RecordNote("Unmanaged history remains recoverable", "Destination cleanup preserved an unconfigured backup's oldest revision, which was restored and byte-checked afterward. The unrelated chunks/user.txt also survived.");
    }
}
