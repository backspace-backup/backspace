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
public sealed class E55_OnDemandLockRecovery(ITestOutputHelper output) : BackupE2EBase(output)
{
    protected override string ScenarioName => "E55-on-demand-lock-recovery";
    [AvaloniaFact] public Task Run_E55() => Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = deadline.Token;
        var source = Path.Combine(ctx.SourcesDir, "small-source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "file.txt"), "protected backup");
        var password = Guid.NewGuid().ToString("N");
        var destination = new Destination { Name = "Disposable lock recovery repository", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("repo"), Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e55", password) };
        var backup = new Backup { Name = "On-demand lock recovery", SourcePaths = { source }, UseVss = false,
            VssDisabledAcknowledged = true, PruneSchedule = new(), CheckSchedule = new(), KeepPolicy = "1",
            Targets = { new() { DestinationId = destination.Id } } };
        ServiceLocator.Config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(destination); });
        for (var i = 0; i < 2; i++)
            Assert.Equal(BackupRunStatus.Success, (await fixture.Engine.BackupPruneCheckAsync(backup, source, destination, "default", ct)).Status);
        var config = await fixture.Sidecar.Client!.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>("/v1.Backrest/GetConfig", new(), ct);
        var repo = Assert.Single(config.Repos);
        Assert.False(repo.AutoUnlock);
        async Task<BackrestOperationList> Operations() => await fixture.Sidecar.Client.CallAsync<GetOperationsRequest, BackrestOperationList>(
            "/v1.Backrest/GetOperations", new() { Selector = new() { RepoGuid = repo.Guid }, LastN = 256 }, ct);
        Assert.DoesNotContain((await Operations()).Operations, op => op.OperationRunCommand?.Command == "unlock");
        Assert.True((await fixture.Sidecar.Client.ReadIndexCountersAsync(repo.Id, ct)).Success >= 2);
        Assert.Equal(2, (await Operations()).Operations.Count(op => op.OperationIndexSnapshot is not null));
        ctx.Report.RecordNote("Healthy backups do not run an unlock command", "Two real backups succeeded with unconditional unlocking disabled. No recovery command was needed.");
        ctx.Report.RecordNote("Background indexing finishes before backup returns", "Both saved snapshots already had real index records, and the daemon reported at least two completed indexing tasks when the backup calls returned.");

        // A real restic backup waiting on stdin keeps a shared repository lock.
        // Exclusive cleanup must leave that live process and its lock alone.
        var start = new ProcessStartInfo(fixture.Sidecar.ResticBinaryPath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["RESTIC_PASSWORD"] = password;
        foreach (var arg in new[] { "--repo", repo.Uri, "backup", "--stdin", "--stdin-filename", "held-input.txt", "--quiet" })
            start.ArgumentList.Add(arg);
        using var held = Process.Start(start)!;
        var released = false;
        var stdout = held.StandardOutput.ReadToEndAsync(ct);
        var stderr = held.StandardError.ReadToEndAsync(ct);
        try
        {
            var locks = Path.Combine(repo.Uri, "locks");
            var wait = Stopwatch.StartNew();
            while (!Directory.EnumerateFiles(locks).Any())
            {
                if (held.HasExited) throw new IOException("The test lock process exited: " + await stderr);
                if (wait.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("The test lock process did not acquire its lock.");
                await Task.Delay(50, ct);
            }
            // Ensure the held child acquired its own lock rather than observing
            // only the daemon's short-lived post-backup indexing lock.
            await Task.Delay(500, ct);
            Assert.False(held.HasExited);
            var cleanup = await fixture.Engine.RunPruneOnlyAsync(backup, source, destination, "default", ct, Output.WriteLine);
            Assert.Equal(BackupRunStatus.Failed, cleanup.Status);
            Assert.False(held.HasExited);
            Assert.NotEmpty(Directory.EnumerateFiles(locks));
            Assert.Equal(2, (await fixture.Engine.ListSnapshotsAsync(backup, source, destination, "default", ct)).Count);
            ctx.Report.RecordNote("A live lock is preserved", "Cleanup was refused while another real restic process held the repository open. Its process and lock remained present, and both snapshots survived.");

            held.Kill(entireProcessTree: true);
            await held.WaitForExitAsync(ct);
            await Task.WhenAll(stdout, stderr);
            // Windows retains the process object (and its PID) while this test
            // owns an open process handle, even after exit. A real crash leaves
            // no supervising test handle; release ours before testing staleness.
            held.Dispose(); released = true;
            Assert.NotEmpty(Directory.EnumerateFiles(locks));
            cleanup = await fixture.Engine.RunPruneOnlyAsync(backup, source, destination, "default", ct, Output.WriteLine);
            Assert.True(cleanup.Status == BackupRunStatus.Success, cleanup.Summary);
            Assert.Single(await fixture.Engine.ListSnapshotsAsync(backup, source, destination, "default", ct));
            var recovery = (await Operations()).Operations.Where(op => op.OperationRunCommand?.Command == "unlock").ToArray();
            Assert.Equal(2, recovery.Length);
            Assert.All(recovery, op => Assert.Equal("STATUS_SUCCESS", op.Status));
            Assert.DoesNotContain((await Operations()).Operations, op => op.OperationRunCommand?.Command.Contains("remove-all", StringComparison.Ordinal) == true);
            ctx.Report.RecordNote("A crashed process's lock is recovered on demand", "After the test process was killed, cleanup removed its stale lock with normal unlock and then applied the keep-one policy. The two recovery attempts each issued exactly one unlock; neither used force removal.");
        }
        finally
        {
            if (!released && !held.HasExited) { held.Kill(entireProcessTree: true); await held.WaitForExitAsync(CancellationToken.None); }
        }
    }
}
