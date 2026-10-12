using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.WebDav;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

[Collection(ScenarioCollection.Name)]
public sealed class E45_MountedSnapshotSafety : BackupE2EBase
{
    public E45_MountedSnapshotSafety(ITestOutputHelper output) : base(output) { }
    protected override string ScenarioName => "E45-mounted-snapshot-safety";
    [AvaloniaFact] public async Task Run_E45() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = deadline.Token;
        var source = Path.Combine(ctx.SourcesDir, "source");
        Directory.CreateDirectory(Path.Combine(source, "nested", "deeper"));
        Directory.CreateDirectory(Path.Combine(source, "empty", "deeper"));
        var destination = new Destination { Name = "Disposable mounted backup", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("repository"), Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e45", Guid.NewGuid().ToString("N")) };
        var backup = new Backup { Name = "Mounted snapshot audit", SourcePaths = { source }, UseVss = false,
            PruneSchedule = new BackupSchedule(), CheckSchedule = new BackupSchedule(),
            Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } } };
        ServiceLocator.Config.Update(c => { c.Destinations.Add(destination); c.Backups.Add(backup); });
        for (var version = 1; version <= 3; version++)
        {
            File.WriteAllText(Path.Combine(source, "readme.txt"), "root-version-" + version);
            File.WriteAllText(Path.Combine(source, "nested", "one.txt"), "nested-version-" + version);
            File.WriteAllText(Path.Combine(source, "nested", "deeper", "two.txt"), "deep-version-" + version);
            Assert.Equal(BackupRunStatus.Success,
                (await fixture.Engine.BackupPruneCheckAsync(backup, source, destination, "default", ct)).Status);
        }
        var revisions = await fixture.Engine.ListSnapshotsAsync(backup, source, destination, "default", ct);
        var latest = revisions[0];
        var bulkTarget = ctx.MakeRestoreTarget("bulk");
        Assert.True(await fixture.Engine.RestoreSnapshotSubtreeAsync(backup, source, destination, "default",
            latest.Number, bulkTarget, ct, latest.SnapshotId));
        Assert.Equal("root-version-3", File.ReadAllText(Path.Combine(bulkTarget, "readme.txt")));
        Assert.Equal("deep-version-3", File.ReadAllText(Path.Combine(bulkTarget, "nested", "deeper", "two.txt")));
        Assert.True(Directory.Exists(Path.Combine(bulkTarget, "empty", "deeper")));
        ctx.Report.RecordNote("Complete nested bulk restore", "The actual bundled Backrest and restic restored all three files and the empty/deeper folder. Directory counts did not turn a successful restore into a warning.");

        var cache = ctx.MakeRestoreTarget("mount-cache");
        using (var provider = new BackupMountTreeProvider(fixture.Engine, backup, destination, "default", cache))
        {
            await using (var first = await provider.OpenAsync("All revisions/source (3 revisions)/r1/readme.txt", ct))
                Assert.Equal("root-version-1", await new StreamReader(first).ReadToEndAsync(ct));
            await using (var second = await provider.OpenAsync("All revisions/source (3 revisions)/r1/nested/one.txt", ct))
                Assert.Equal("nested-version-1", await new StreamReader(second).ReadToEndAsync(ct));
            await using (var third = await provider.OpenAsync("All revisions/source (3 revisions)/r1/nested/deeper/two.txt", ct))
                Assert.Equal("deep-version-1", await new StreamReader(third).ReadToEndAsync(ct));
        }
        ctx.Report.RecordNote("Mounted reads use the production engine", "The first file came through lazy restore; the second and third came through the published complete bulk cache. All three contents matched the chosen oldest snapshot.");

        await fixture.Engine.PruneSnapshotIdAsync(backup, source, destination, "default",
            revisions.Single(r => r.Number == 1).SnapshotId!, ct);
        using (var remounted = new BackupMountTreeProvider(fixture.Engine, backup, destination, "default", cache))
        await using (var stream = await remounted.OpenAsync("All revisions/source (2 revisions)/r1/readme.txt", ct))
            Assert.Equal("root-version-2", await new StreamReader(stream).ReadToEndAsync(ct));
        ctx.Report.RecordNote("Retention cannot reuse another snapshot's cached bytes", "After the oldest snapshot was deleted, a new mount using the same cache root returned version 2 for the new revision 1. It did not return the version-1 file left by the earlier mount.");

        var emptySource = Path.Combine(ctx.SourcesDir, "onlyempty");
        Directory.CreateDirectory(Path.Combine(emptySource, "child"));
        var emptyBackup = new Backup { Name = "Only empty folders", SourcePaths = { emptySource }, UseVss = false,
            PruneSchedule = new BackupSchedule(), CheckSchedule = new BackupSchedule(),
            Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } } };
        ServiceLocator.Config.Update(c => c.Backups.Add(emptyBackup));
        Assert.Equal(BackupRunStatus.Success,
            (await fixture.Engine.BackupPruneCheckAsync(emptyBackup, emptySource, destination, "default", ct)).Status);
        var emptyRevision = (await fixture.Engine.ListSnapshotsAsync(emptyBackup, emptySource, destination, "default", ct))[0];
        var emptyTarget = ctx.MakeRestoreTarget("empty-only");
        Assert.True(await fixture.Engine.RestoreSnapshotSubtreeAsync(emptyBackup, emptySource, destination, "default",
            emptyRevision.Number, emptyTarget, ct, emptyRevision.SnapshotId));
        Assert.True(Directory.Exists(Path.Combine(emptyTarget, "child")));
        Assert.Empty(Directory.EnumerateFiles(emptyTarget, "*", SearchOption.AllDirectories));
        ctx.Report.RecordNote("Empty-only backups restore successfully", "A second actual backup contained only an empty child directory. Bulk restore returned success, recreated that directory, and produced no invented files.");

        var bareSource = Path.Combine(ctx.SourcesDir, "bare-root");
        Directory.CreateDirectory(bareSource);
        var bareBackup = new Backup { Name = "Completely empty source", SourcePaths = { bareSource }, UseVss = false,
            PruneSchedule = new BackupSchedule(), CheckSchedule = new BackupSchedule(),
            Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } } };
        Assert.Equal(BackupRunStatus.Success,
            (await fixture.Engine.BackupPruneCheckAsync(bareBackup, bareSource, destination, "default", ct)).Status);
        var bareRevision = (await fixture.Engine.ListSnapshotsAsync(bareBackup, bareSource, destination, "default", ct))[0];
        var bareTarget = Path.Combine(ctx.RootDir, "empty-root-restore");
        Assert.True(await fixture.Engine.RestoreSnapshotSubtreeAsync(bareBackup, bareSource, destination, "default",
            bareRevision.Number, bareTarget, ct, bareRevision.SnapshotId));
        Assert.True(Directory.Exists(bareTarget));
        Assert.Empty(Directory.EnumerateFileSystemEntries(bareTarget));
        ctx.Report.RecordNote("A completely empty source is still a valid restore", "A third real backup had no files or child folders. Restoring it succeeded and created an empty destination directory.");
        ctx.Report.RecordNote("Scope of this check", "This exercises the production snapshot provider and bundled engines against an encrypted local repository. Native Explorer/WinFSP behavior and live cloud access are separate acceptance checks.");
    }
}
