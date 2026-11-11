using System;
using System.Collections.Concurrent;
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

/// <summary>
/// The stale-lock rule with real restic: healthy work never unlocks; a
/// command refused by a lock (exit code 11) gets one ordinary unlock, which
/// removes stale locks only, and one retry. A live lock survives; a crashed
/// process's lock is cleared. Checked on the cleanup after a backup, which
/// runs restic directly (U175), and on the stand-alone cleanup, which still
/// goes through Backrest until step 2.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E55_OnDemandLockRecovery(ITestOutputHelper output) : BackupE2EBase(output)
{
    protected override string ScenarioName => "E55-on-demand-lock-recovery";
    [AvaloniaFact] public Task Run_E55() => Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = deadline.Token;
        var source = Path.Combine(ctx.SourcesDir, "small-source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "file.txt"), "protected backup");
        var password = Guid.NewGuid().ToString("N");
        var destination = new Destination { Name = "Disposable lock recovery repository", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("repo"), Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e55", password) };
        // Cleanup after each backup (direct), keep one; no check.
        var backup = new Backup { Name = "On-demand lock recovery", SourcePaths = { source }, UseVss = false,
            VssDisabledAcknowledged = true, PruneSchedule = null, CheckSchedule = new(), KeepPolicy = "1",
            Targets = { new() { DestinationId = destination.Id } } };
        ServiceLocator.Config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(destination); });
        var commands = new ConcurrentQueue<(ResticCommand Command, ResticResult Result)>();
        fixture.Engine.ResticCommandFinished += (command, result) => commands.Enqueue((command, result));
        (ResticCommand Command, ResticResult Result)[] Ran(string name) => commands.Where(c => c.Command.Arguments[0] == name).ToArray();
        async Task<RunRecord> BackUp(string marker)
        {
            File.WriteAllText(Path.Combine(source, "changed.txt"), marker);
            return await fixture.Engine.BackupPruneCheckAsync(backup, source, destination, "default", ct, Output.WriteLine);
        }

        for (var i = 0; i < 2; i++)
            Assert.Equal(BackupRunStatus.Success, (await BackUp("healthy " + i)).Status);
        Assert.Empty(Ran("unlock"));
        Assert.Equal(3, Ran("forget").Length + Ran("prune").Length); // forget then prune, then forget --prune
        ctx.Report.RecordNote("Healthy backups do not run an unlock command",
            "Two real backups with their cleanup succeeded; restic was never asked to unlock.");

        // A real restic backup waiting on its input keeps a shared lock.
        var locks = Path.Combine(destination.PathOrSubpath, "locks");
        var held = Hold(fixture.Sidecar.ResticBinaryPath, destination.PathOrSubpath, password);
        var released = false;
        var stdout = held.StandardOutput.ReadToEndAsync(ct);
        var stderr = held.StandardError.ReadToEndAsync(ct);
        try
        {
            var wait = Stopwatch.StartNew();
            while (!Directory.EnumerateFiles(locks).Any())
            {
                if (held.HasExited) throw new IOException("The test lock process exited: " + await stderr);
                if (wait.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("The test lock process did not acquire its lock.");
                await Task.Delay(50, ct);
            }
            await Task.Delay(500, ct);
            Assert.False(held.HasExited);

            // The backup shares the repository; its cleanup needs it alone.
            commands.Clear();
            var refused = await BackUp("while held");
            Assert.Equal(BackupRunStatus.Warning, refused.Status);
            Assert.Contains("maintenance reported STATUS_ERROR", refused.Summary);
            Assert.Equal(0, Assert.Single(Ran("backup")).Result.ExitCode);
            Assert.Equal(new[] { 11, 11 }, Ran("forget").Select(f => f.Result.ExitCode));
            Assert.Equal(new[] { "unlock" }, Assert.Single(Ran("unlock")).Command.Arguments);
            Assert.DoesNotContain(commands, c => ResticRunner.Arguments(c.Command).Contains("--remove-all"));
            Assert.False(held.HasExited);
            Assert.NotEmpty(Directory.EnumerateFiles(locks));
            ctx.Report.RecordNote("A live lock is preserved (direct cleanup)",
                "The backup itself was saved beside the live restic process. Its cleanup was refused (exit code 11), asked restic once to remove stale locks, " +
                "tried once more and was refused again: the live process and its lock remained, and the run ended with a warning.");

            // The standalone cleanup (Backrest, until step 2) obeys the same rule.
            var standalone = await fixture.Engine.RunPruneOnlyAsync(backup, source, destination, "default", ct, Output.WriteLine);
            Assert.Equal(BackupRunStatus.Failed, standalone.Status);
            Assert.False(held.HasExited);
            Assert.NotEmpty(Directory.EnumerateFiles(locks));

            held.Kill(entireProcessTree: true);
            await held.WaitForExitAsync(ct);
            await Task.WhenAll(stdout, stderr);
            // Windows retains the process object (and its PID) while this test
            // owns an open process handle, even after exit. A real crash leaves
            // no supervising test handle; release ours before testing staleness.
            held.Dispose(); released = true;
            Assert.NotEmpty(Directory.EnumerateFiles(locks));

            commands.Clear();
            var recovered = await BackUp("after the crash");
            Assert.True(recovered.Status == BackupRunStatus.Success, recovered.Summary);
            Assert.Equal(new[] { 11, 0 }, Ran("forget").Select(f => f.Result.ExitCode));
            Assert.Equal(0, Assert.Single(Ran("unlock")).Result.ExitCode);
            Assert.DoesNotContain(commands, c => ResticRunner.Arguments(c.Command).Contains("--remove-all"));
            Assert.Single(await fixture.Engine.ListSnapshotsAsync(backup, source, destination, "default", ct));
            ctx.Report.RecordNote("A crashed process's lock is recovered on demand (direct cleanup)",
                "After the test process was killed, the next backup's cleanup was refused once, removed the stale lock with an ordinary unlock " +
                "and applied the keep-one policy on its retry. No force removal.");
        }
        finally
        {
            if (!released && !held.HasExited) { held.Kill(entireProcessTree: true); await held.WaitForExitAsync(CancellationToken.None); }
            if (!released) held.Dispose();
        }
    }

    private static Process Hold(string restic, string repository, string password)
    {
        var start = new ProcessStartInfo(restic) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["RESTIC_PASSWORD"] = password;
        foreach (var arg in new[] { "--repo", repository, "backup", "--stdin", "--stdin-filename", "held-input.txt", "--quiet" })
            start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }
}
