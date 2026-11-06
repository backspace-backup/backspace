using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// U142 (6 November 2026): the cleanup after a backup removes the old backups
/// the keep rules no longer keep, and the data only they used. The run says
/// how much that freed, beside what it added, so the storage is not read as
/// only ever growing. Real engine: restic's own forget and prune.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E62_CleanupFreedSpace : BackupE2EBase
{
    public E62_CleanupFreedSpace(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E62-cleanup-freed-space";

    [AvaloniaFact] public async Task Run_E62() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var source = ctx.MakeSource("docs", targetBytes: 4_000);
        var large = Path.Combine(source, "old-video.mp4");
        var data = new byte[3_000_000];
        new Random(90).NextBytes(data);
        File.WriteAllBytes(large, data);
        var destination = new Destination
        {
            Name = "Google Drive - backspace", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("drive"),
            Encrypted = true, StoragePasswordRef = ServiceLocator.Secrets.PutNew("e62", $"e62-{Guid.NewGuid():N}"),
        };
        // Keep only the latest backup, so the second removes the first.
        var backup = new Backup { Name = "local_c", SourcePaths = { source }, KeepPolicy = "1", CheckSchedule = new BackupSchedule(), UseVss = false, VssDisabledAcknowledged = true };
        backup.Targets.Add(new BackupTarget { DestinationId = destination.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(destination); cfg.Backups.Add(backup); });
        var engine = fixture.Engine;

        // The first backup: its cleanup (a full prune, as none ran yet) removes nothing.
        var firstLines = new List<string>();
        var first = await engine.BackupPruneCheckAsync(backup, source, destination, "default", CancellationToken.None, firstLines.Add);
        Assert.Equal(BackupRunStatus.Success, first.Status);
        Assert.Equal((0, 0L), (first.SnapshotsRemoved, first.BytesFreed));
        Assert.Contains("[cleanup] Nothing to remove: the keep rules keep every backup.", firstLines);

        // The video is deleted; the next backup's cleanup removes the first
        // backup, the only one that still held it, and its data with it.
        File.Delete(large);
        File.WriteAllText(Path.Combine(source, "notes.txt"), "a new note");
        var lines = new List<string>();
        var second = await engine.BackupPruneCheckAsync(backup, source, destination, "default", CancellationToken.None, lines.Add);
        Assert.Equal(BackupRunStatus.Success, second.Status);
        Assert.Equal(1, second.SnapshotsRemoved);
        // Random data doesn't compress: about the video's 3 MB, never more than the storage held.
        Assert.InRange(second.BytesFreed, 2_800_000, 3_400_000);
        var recap = Assert.Single(lines, l => l.StartsWith(CleanupResult.Prefix, StringComparison.Ordinal));
        ctx.Report.RecordNote("The cleanup's line in the second backup's log", recap);
        Assert.StartsWith("[cleanup] 1 old backup removed · 2.", recap);
        Assert.EndsWith(" MB freed in the storage", recap);
        var snapshots = await engine.ListSnapshotsAsync(backup, source, destination, "default", CancellationToken.None);
        Assert.Single(snapshots);

        // The backup's history shows it beside what the backup added.
        first.Id = Guid.NewGuid().ToString("N"); second.Id = Guid.NewGuid().ToString("N");
        first.StartedUtc = DateTime.UtcNow.AddHours(-1); first.EndedUtc = first.StartedUtc.AddMinutes(1);
        var history = new BackupHistoryWindow { DataContext = new BackupHistoryViewModel(backup, new[] { first, second }, checks: false), Width = 900, Height = 560 };
        ctx.Driver.Show(history);
        await ctx.Driver.PumpAsync(200);
        var vm = (BackupHistoryViewModel)history.DataContext!;
        var row = vm.Rows.First();
        Assert.Matches(@"^1 new · 0 changed · [0-9.]+ (B|KB) added · 2\.[0-9] MB freed$", row.Added);
        Assert.DoesNotContain("freed", vm.Rows.Last().Added); // the first freed nothing: no figure
        ctx.Snapshot(history, "history with the space freed",
            "local_c's history: the latest backup reads '1 new · 0 changed · … added · 2.9 MB freed' (its cleanup removed the backup that held the deleted 3 MB video); the earlier one shows no freed figure.");
        history.Close();
    }
}
