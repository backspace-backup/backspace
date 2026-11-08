using System;
using System.Collections.Concurrent;
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

/// <summary>
/// U156 with the real engine. Restic lets backups share a repository (shared
/// locks) but needs it alone for cleanup and checks.
///
/// Part 1, "Backups at once per destination" 2: two backups into one local
/// repository at the same time, each in its own engine (as two background
/// workers run them), with inline cleanup and check. Both back up together,
/// take turns for cleanup and checks, and succeed; a check of the repository
/// afterwards passes.
///
/// Part 2, the defaults (1 per destination, 2 in total): a second backup to
/// the same destination is queued naming the first, then runs; a backup to
/// another destination runs alongside; a third waits for the total.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E64_BackupsAtOnceRealEngine : BackupE2EBase
{
    public E64_BackupsAtOnceRealEngine(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E64-backups-at-once-real-engine";

    [AvaloniaFact] public async Task Run_E64() => await Run();

    private Destination LocalRepository(ScenarioContext ctx, string name) => new()
    {
        Name = name, Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination(name.Replace(' ', '-')),
        Encrypted = true, StoragePasswordRef = ServiceLocator.Secrets.PutNew("e64-" + name, $"e64-{Guid.NewGuid():N}"),
    };

    private static Backup Backup(string name, string source, Destination destination, bool inlineMaintenance) => new()
    {
        Name = name, SourcePaths = { source }, UseVss = false, VssDisabledAcknowledged = true, AbortOnMeteredNetwork = false, Threads = 2,
        PruneSchedule = inlineMaintenance ? null : new BackupSchedule(), CheckSchedule = inlineMaintenance ? null : new BackupSchedule(),
        Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } },
    };

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        // ---- Part 1: two per destination, two engines, one repository ----
        var shared = LocalRepository(ctx, "Shared repo");
        var a = Backup("Docs A (E64)", ctx.MakeSource("docs-a", seed: 641, targetBytes: 24_000_000), shared, inlineMaintenance: true);
        var b = Backup("Docs B (E64)", ctx.MakeSource("docs-b", seed: 642, targetBytes: 24_000_000), shared, inlineMaintenance: true);
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(shared); cfg.Backups.Add(a); cfg.Backups.Add(b);
            cfg.Limits = new BackupLimits { PerDestination = 2, Total = 2 };
        });

        // A second engine with its own Backrest folder: what a second
        // background worker has (EngineSlot).
        var secondDir = Path.Combine(Path.GetTempPath(), "backspace-e64-second-" + Guid.NewGuid().ToString("N"));
        var secondSidecar = new BackrestSidecar(AppPaths.BackrestBinary, AppPaths.ResticBinary, secondDir);
        try
        {
            await secondSidecar.StartAsync(CancellationToken.None);
            var secondEngine = new ResticBackrestEngine(secondSidecar, ServiceLocator.Secrets);
            var second = new BackupOrchestrator(ServiceLocator.Config, ServiceLocator.Secrets, secondEngine, ServiceLocator.Logs,
                ServiceLocator.Healthcheck, ServiceLocator.Mail, ServiceLocator.Notifier, new BackupProgressService());
            var first = ServiceLocator.Orchestrator;

            // When each engine backed up: from its first line to "Finishing
            // snapshot history", which follows restic's backup.
            var phases = new ConcurrentDictionary<string, (DateTime Start, DateTime? End)>();
            void Track(IBackupEngine engine, string who) => engine.LineWritten += line =>
            {
                phases.AddOrUpdate(who, _ => (DateTime.UtcNow, null), (_, p) => p);
                if (line.Contains("Finishing snapshot history", StringComparison.Ordinal))
                    phases.AddOrUpdate(who, _ => (DateTime.UtcNow, DateTime.UtcNow), (_, p) => (p.Start, p.End ?? DateTime.UtcNow));
            };
            Track(fixture.Engine, "A"); Track(secondEngine, "B");
            var reasons = new ConcurrentQueue<string>();
            first.RunQueuedReasonChanged += (_, r) => { if (r.Length > 0) reasons.Enqueue("A: " + r); };
            second.RunQueuedReasonChanged += (_, r) => { if (r.Length > 0) reasons.Enqueue("B: " + r); };

            var both = await Task.WhenAll(
                Task.Run(() => first.RunBackupAsync(a, CancellationToken.None)),
                Task.Run(() => second.RunBackupAsync(b, CancellationToken.None))).WaitAsync(TimeSpan.FromMinutes(8));
            foreach (var r in both)
                Assert.True(r.Status == BackupRunStatus.Success, $"{r.BackupName} ended {r.Status}: {r.Summary} {string.Join(" | ", r.Errors)}");
            var pa = phases["A"]; var pb = phases["B"];
            Assert.True(pa.End is not null && pb.End is not null, "Both engines backed up.");
            var together = pa.Start < pb.End && pb.Start < pa.End;
            Assert.True(together, $"The backups overlapped: A {pa.Start:O}–{pa.End:O}, B {pb.Start:O}–{pb.End:O}.");
            Assert.All(both, r => Assert.NotEmpty(r.Checks)); // each ran its inline check

            // Afterwards, the repository checks out, and holds both backups.
            var check = await first.RunCheckAsync(a, CancellationToken.None);
            Assert.Equal(BackupRunStatus.Success, check.Status);
            var snapshotsA = await fixture.Engine.ListSnapshotsAsync(a, a.SourcePaths[0], shared, "default", CancellationToken.None);
            var snapshotsB = await secondEngine.ListSnapshotsAsync(b, b.SourcePaths[0], shared, "default", CancellationToken.None);
            Assert.Single(snapshotsA); Assert.Single(snapshotsB);
            var overlap = (Min(pa.End!.Value, pb.End!.Value) - Max(pa.Start, pb.Start)).TotalSeconds;
            var report = Path.Combine(ctx.ScreenshotsDir, "part1-two-per-destination.txt");
            File.WriteAllText(report,
                $"Per destination 2, total 2. Two engines (own Backrest folders), one local repository.\n" +
                $"A backup phase: {pa.Start:HH:mm:ss.fff} to {pa.End:HH:mm:ss.fff} UTC\n" +
                $"B backup phase: {pb.Start:HH:mm:ss.fff} to {pb.End:HH:mm:ss.fff} UTC\n" +
                $"Backed up together for {overlap:F1} s.\n" +
                $"Queued reasons seen: {(reasons.IsEmpty ? "(none)" : string.Join(" / ", reasons))}\n" +
                $"A: {both[0].Status}, {both[0].Summary}\nB: {both[1].Status}, {both[1].Summary}\n" +
                $"Check of the repository afterwards: {check.Status}, {check.Summary}\n" +
                $"Snapshots: A {snapshotsA.Count}, B {snapshotsB.Count}\n");
            Output.WriteLine(File.ReadAllText(report));
        }
        finally
        {
            try { await secondSidecar.StopAsync(); } catch { }
            try { Directory.Delete(secondDir, recursive: true); } catch { }
        }

        // ---- Part 2: the defaults, one engine (the window) ----
        var drive = LocalRepository(ctx, "Drive"); var nas = LocalRepository(ctx, "NAS"); var usb = LocalRepository(ctx, "USB");
        var c = Backup("local_c (E64)", ctx.MakeSource("local-c", seed: 643, targetBytes: 40_000_000), drive, inlineMaintenance: false);
        var d = Backup("local_d (E64)", ctx.MakeSource("local-d", seed: 644, targetBytes: 2_000_000), drive, inlineMaintenance: false);
        var photos = Backup("photos (E64)", ctx.MakeSource("photos", seed: 645, targetBytes: 40_000_000), nas, inlineMaintenance: false);
        var music = Backup("music (E64)", ctx.MakeSource("music", seed: 646, targetBytes: 2_000_000), usb, inlineMaintenance: false);
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(drive); cfg.Destinations.Add(nas); cfg.Destinations.Add(usb);
            cfg.Backups.Add(c); cfg.Backups.Add(d); cfg.Backups.Add(photos); cfg.Backups.Add(music);
            cfg.Limits = new BackupLimits();
        });
        var orchestrator = ServiceLocator.Orchestrator;
        var events = new ConcurrentQueue<(string Id, string Reason, DateTime At)>();
        orchestrator.RunQueuedReasonChanged += (id, r) => events.Enqueue((id, r, DateTime.UtcNow));
        var runC = Task.Run(() => orchestrator.RunBackupAsync(c, CancellationToken.None));
        await Task.Delay(1500);
        var runD = Task.Run(() => orchestrator.RunBackupAsync(d, CancellationToken.None));
        var runP = Task.Run(() => orchestrator.RunBackupAsync(photos, CancellationToken.None));
        await Task.Delay(1500);
        var runM = Task.Run(() => orchestrator.RunBackupAsync(music, CancellationToken.None));
        var all = await Task.WhenAll(runC, runD, runP, runM).WaitAsync(TimeSpan.FromMinutes(8));
        foreach (var r in all)
            Assert.True(r.Status == BackupRunStatus.Success, $"{r.BackupName} ended {r.Status}: {r.Summary} {string.Join(" | ", r.Errors)}");
        string[] Of(Backup b) => events.Where(e => e.Id == b.Id).Select(e => e.Reason).ToArray();
        Assert.Contains("waiting for local_c (E64) (same destination)", Of(d));
        Assert.Equal("", Of(d).Last());
        Assert.Empty(Of(photos));
        Assert.Empty(Of(c));
        Assert.Contains("2 backups already running", Of(music));
        Assert.Equal("", Of(music).Last());
        // local_d starts only once local_c has ended.
        var dRan = events.Last(e => e.Id == d.Id && e.Reason == "").At;
        Assert.True(dRan >= all[0].EndedUtc!.Value.AddSeconds(-2), $"local_d ran at {dRan:O}, local_c ended {all[0].EndedUtc:O}.");
        var lines = string.Join("\n", events.Select(e =>
            $"{e.At:HH:mm:ss.fff} {all.First(r => r.BackupId == e.Id).BackupName}: {(e.Reason.Length == 0 ? "runs" : "Queued: " + e.Reason)}"));
        var report2 = Path.Combine(ctx.ScreenshotsDir, "part2-defaults.txt");
        File.WriteAllText(report2, "Defaults: 1 per destination, 2 in total. One engine.\n" + lines + "\n"
            + string.Join("\n", all.Select(r => $"{r.BackupName}: {r.Status}, {r.StartedUtc:HH:mm:ss}–{r.EndedUtc:HH:mm:ss} UTC")) + "\n");
        Output.WriteLine(File.ReadAllText(report2));
    }

    private static DateTime Min(DateTime x, DateTime y) => x < y ? x : y;
    private static DateTime Max(DateTime x, DateTime y) => x > y ? x : y;
}
