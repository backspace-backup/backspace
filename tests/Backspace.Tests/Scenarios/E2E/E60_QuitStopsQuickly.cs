using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// U126 (4 November 2026): Quit during a running backup took about 15 seconds,
/// so it looked as if it hadn't worked. The background service gives its
/// backup worker 15 seconds to end before forcing it, and it stopped reading
/// the worker's output as soon as it began stopping, so the worker blocked
/// writing its last messages. This checks the worker's own part stays quick:
/// real engine, local repository, a backup stopped mid-way, then the engine
/// shut down as the worker does, both timed (0.3 s and 0.0 s on 4 November).
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E60_QuitStopsQuickly : BackupE2EBase
{
    public E60_QuitStopsQuickly(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E60-quit-stops-quickly";

    [AvaloniaFact] public async Task Run_E60() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        // Enough data that the backup is still running when it is stopped.
        var source = Path.Combine(ctx.RootDir, "sources", "big");
        Directory.CreateDirectory(source);
        var block = new byte[16 << 20];
        for (var i = 0; i < 64; i++)
        {
            RandomNumberGenerator.Fill(block);
            File.WriteAllBytes(Path.Combine(source, $"part{i:00}.bin"), block);
        }
        var destination = new Destination
        {
            Name = "Local", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("local"), Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e60", $"e60-{Guid.NewGuid():N}"),
        };
        var backup = new Backup
        {
            Name = "Big (E60)", SourcePaths = { source }, UseVss = false, VssDisabledAcknowledged = true, Threads = 2,
            BandwidthLimitKBps = 20_000, // keeps it running for a while
            PruneSchedule = new BackupSchedule(), CheckSchedule = new BackupSchedule(),
            Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } },
        };
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(destination); cfg.Backups.Add(backup); });

        using var stop = new CancellationTokenSource();
        var moving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLine(string line) { if (line.StartsWith(BackupLiveProgress.Prefix, StringComparison.Ordinal) && !line.Contains("· 0 B done")) moving.TrySetResult(); }
        ServiceLocator.Engine.LineWritten += OnLine;
        RunRecord record;
        TimeSpan cancelToEnd, engineStop;
        try
        {
            var run = Task.Run(() => ServiceLocator.Orchestrator.RunBackupAsync(backup, stop.Token));
            Assert.True(await Task.WhenAny(moving.Task, Task.Delay(TimeSpan.FromMinutes(2))) == moving.Task, "The backup never started moving data.");
            var watch = Stopwatch.StartNew();
            stop.Cancel();
            record = await run;
            cancelToEnd = watch.Elapsed;
            // What the worker does next: stop the engine.
            watch.Restart();
            await fixture.Sidecar.StopAsync();
            engineStop = watch.Elapsed;
        }
        finally { ServiceLocator.Engine.LineWritten -= OnLine; }

        ctx.Report.RecordNote("Timings", $"stop to end of run: {cancelToEnd.TotalSeconds:0.0} s; engine stop: {engineStop.TotalSeconds:0.0} s; run status: {record.Status}");
        Output.WriteLine($"stop to end of run: {cancelToEnd.TotalSeconds:0.0} s; engine stop: {engineStop.TotalSeconds:0.0} s; status {record.Status}");
        Assert.NotEqual(BackupRunStatus.Success, record.Status);
        Assert.True(cancelToEnd < TimeSpan.FromSeconds(5), $"The run took {cancelToEnd.TotalSeconds:0.0} s to end after Stop.");
        Assert.True(engineStop < TimeSpan.FromSeconds(3), $"The engine took {engineStop.TotalSeconds:0.0} s to stop.");
    }
}
