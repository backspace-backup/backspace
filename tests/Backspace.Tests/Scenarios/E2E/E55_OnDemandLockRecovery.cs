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
/// process's lock is cleared. Checked on the cleanup after a backup and on
/// the stand-alone cleanup, both run by restic directly (U175).
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
        var held = Hold(AppPaths.ResticBinary, destination.PathOrSubpath, password);
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

            // The stand-alone cleanup (restic directly too, U175 step 2) obeys
            // the same rule: one unlock, one retry, then a failure.
            commands.Clear();
            var standalone = await fixture.Engine.RunPruneOnlyAsync(backup, source, destination, "default", ct, Output.WriteLine);
            Assert.Equal(BackupRunStatus.Failed, standalone.Status);
            Assert.Equal(new[] { 11, 11 }, Ran("forget").Select(f => f.Result.ExitCode));
            Assert.Single(Ran("unlock"));
            Assert.Empty(Ran("prune"));
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

        // U188, the owner's case (11 November 2026): a check stopped while
        // quitting left restic's exclusive lock, and the next backup on a
        // computer that had not used the repository yet failed at its first
        // command (cat config) without trying to remove the stale lock.
        // `key passwd` takes the exclusive lock and then waits for a new
        // password on its input, like a long check.
        var exclusive = Hold(AppPaths.ResticBinary, destination.PathOrSubpath, password, "key", "passwd");
        var exclusiveOut = exclusive.StandardOutput.ReadToEndAsync(ct);
        var exclusiveErr = exclusive.StandardError.ReadToEndAsync(ct);
        try
        {
            var wait = Stopwatch.StartNew();
            while (!Directory.EnumerateFiles(locks).Any())
            {
                if (exclusive.HasExited) throw new IOException("The exclusive lock process exited: " + await exclusiveErr);
                if (wait.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("The exclusive lock process did not acquire its lock.");
                await Task.Delay(50, ct);
            }
            await Task.Delay(500, ct);

            // While the exclusive lock is live, the backup is refused: a real failure.
            File.Delete(KnownRepositories.FilePath);
            commands.Clear();
            var blocked = await BackUp("exclusive held");
            Assert.Equal(BackupRunStatus.Failed, blocked.Status);
            Assert.Equal(0, Assert.Single(Ran("cat")).Result.ExitCode);
            Assert.Equal(new[] { 11, 11 }, Ran("backup").Select(b => b.Result.ExitCode));
            Assert.Single(Ran("unlock"));
            Assert.False(exclusive.HasExited);
            Assert.NotEmpty(Directory.EnumerateFiles(locks));
            ctx.Report.RecordNote("A live exclusive lock still refuses the backup",
                "While a restic process held the repository alone, the backup asked restic once to remove stale locks, was refused again and failed; the live lock remained.");

            exclusive.Kill(entireProcessTree: true);
            await exclusive.WaitForExitAsync(ct);
            await Task.WhenAll(exclusiveOut, exclusiveErr);
        }
        finally
        {
            if (!exclusive.HasExited) { exclusive.Kill(entireProcessTree: true); await exclusive.WaitForExitAsync(CancellationToken.None); }
            exclusive.Dispose();
        }
        Assert.NotEmpty(Directory.EnumerateFiles(locks));

        // Owner's starting state: the repository is new to this computer and a
        // killed process's exclusive lock is in it.
        File.Delete(KnownRepositories.FilePath);
        commands.Clear();
        var afterStop = await BackUp("after the stopped check");
        Assert.True(afterStop.Status == BackupRunStatus.Success, afterStop.Summary);
        var cat = Assert.Single(Ran("cat"));
        Assert.Equal(new[] { "cat", "--no-lock", "config" }, cat.Command.Arguments);
        Assert.Equal(0, cat.Result.ExitCode);
        // The first command that locks (the snapshot list, or the backup) is
        // refused once; the ordinary unlock clears the dead process's lock.
        Assert.Single(commands, c => c.Result.ExitCode == 11);
        Assert.Equal(0, Assert.Single(Ran("unlock")).Result.ExitCode);
        Assert.Equal(0, Assert.Single(Ran("backup")).Result.ExitCode);
        Assert.DoesNotContain(commands, c => ResticRunner.Arguments(c.Command).Contains("--remove-all"));
        Assert.Empty(Directory.EnumerateFiles(locks));
        ctx.Report.RecordNote("A stopped run's exclusive lock no longer fails the next backup (U188)",
            "The repository was new to this computer and held a killed restic's exclusive lock. Reading its settings took no lock and succeeded; " +
            "the backup was refused once, removed the stale lock with an ordinary unlock and was saved on its retry. No force removal.");
    }

    private static Process Hold(string restic, string repository, string password, params string[] command)
    {
        var start = new ProcessStartInfo(restic) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["RESTIC_PASSWORD"] = password;
        if (command.Length == 0) command = ["backup", "--stdin", "--stdin-filename", "held-input.txt", "--quiet"];
        foreach (var arg in new[] { "--repo", repository }.Concat(command))
            start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }
}
