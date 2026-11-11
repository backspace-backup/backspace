using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class MaintenanceOutcomeTests
{
    [Theory]
    [InlineData(RunKind.Check, BackupRunStatus.Success, BackupRunStatus.Success)]
    [InlineData(RunKind.Prune, BackupRunStatus.Success, BackupRunStatus.Success)]
    [InlineData(RunKind.Check, BackupRunStatus.Skipped, BackupRunStatus.Skipped)]
    [InlineData(RunKind.Prune, BackupRunStatus.Skipped, BackupRunStatus.Skipped)]
    [InlineData(RunKind.Check, BackupRunStatus.Warning, BackupRunStatus.Warning)]
    [InlineData(RunKind.Prune, BackupRunStatus.Warning, BackupRunStatus.Warning)]
    [InlineData(RunKind.Check, BackupRunStatus.Failed, BackupRunStatus.Failed)]
    [InlineData(RunKind.Prune, BackupRunStatus.Failed, BackupRunStatus.Failed)]
    [InlineData(RunKind.Check, BackupRunStatus.Running, BackupRunStatus.Failed)]
    [InlineData(RunKind.Prune, BackupRunStatus.NeverRun, BackupRunStatus.Failed)]
    public async Task Returned_outcome_is_preserved_in_actual_orchestration_and_history(
        RunKind kind, BackupRunStatus returned, BackupRunStatus expected)
    {
        using var fixture = new Fixture(1);
        fixture.Engine.Result = (_, _) => new RunRecord
        {
            Status = returned, Summary = "Engine result for this source", LogPath = "isolated-engine.log",
            StartedUtc = DateTime.UtcNow.AddSeconds(-2), EndedUtc = DateTime.UtcNow,
        };
        var result = await fixture.Run(kind);
        Assert.Equal(expected, result.Status);
        Assert.Equal(kind, result.Kind);
        var cell = Assert.Single(result.Cells);
        Assert.Equal(expected, cell.Status);
        Assert.Equal(fixture.Backup.SourcePaths[0], cell.SourcePath);
        Assert.Equal("Review repository", cell.DestinationName);
        Assert.Equal("isolated-engine.log", cell.LogPath);
        Assert.Contains("Engine result for this source", cell.Summary);
        if (expected == BackupRunStatus.Failed) Assert.NotEmpty(result.Errors);
        if (expected is BackupRunStatus.Warning or BackupRunStatus.Skipped) Assert.NotEmpty(result.Warnings);
        if (expected == BackupRunStatus.Success) { Assert.Empty(result.Errors); Assert.Empty(result.Warnings); }
        var saved = Assert.Single(fixture.Logs.LoadHistoryByBackupId(fixture.Backup.Id));
        Assert.Equal(result.Status, saved.Status);
        Assert.Equal(cell.Summary, Assert.Single(saved.Cells).Summary);
    }

    [Theory]
    [InlineData(RunKind.Check, BackupRunStatus.Skipped)]
    [InlineData(RunKind.Prune, BackupRunStatus.Skipped)]
    [InlineData(RunKind.Check, BackupRunStatus.Warning)]
    [InlineData(RunKind.Prune, BackupRunStatus.Warning)]
    [InlineData(RunKind.Check, BackupRunStatus.Failed)]
    [InlineData(RunKind.Prune, BackupRunStatus.Failed)]
    public async Task Successful_first_part_does_not_hide_the_second_parts_problem(RunKind kind, BackupRunStatus problem)
    {
        using var fixture = new Fixture(2, separateRepositories: kind == RunKind.Check);
        fixture.Engine.Result = (call, _) => new RunRecord
        {
            Status = call == 1 ? BackupRunStatus.Success : problem,
            Summary = call == 1 ? "First source completed" : "Second source could not complete normally",
        };
        var result = await fixture.Run(kind);
        Assert.Equal(problem == BackupRunStatus.Failed ? BackupRunStatus.Failed : BackupRunStatus.Warning, result.Status);
        Assert.Equal(2, fixture.Engine.Calls);
        Assert.Equal(problem, result.Cells[1].Status);
        Assert.NotEqual(BackupRunStatus.Success, result.Status);
    }

    [Theory]
    [InlineData(RunKind.Check, false)]
    [InlineData(RunKind.Prune, false)]
    [InlineData(RunKind.Check, true)]
    [InlineData(RunKind.Prune, true)]
    public async Task Cancellation_between_or_during_calls_retains_unfinished_parts(RunKind kind, bool duringSecondCall)
    {
        using var fixture = new Fixture(3, separateRepositories: kind == RunKind.Check);
        using var cancelled = new CancellationTokenSource();
        fixture.Engine.Result = (call, token) =>
        {
            if (!duringSecondCall || call == 2) cancelled.Cancel();
            if (duringSecondCall && call == 2) token.ThrowIfCancellationRequested();
            return new RunRecord { Status = BackupRunStatus.Success, Summary = "Completed before cancellation" };
        };
        var result = await fixture.Run(kind, cancelled.Token);
        Assert.Equal(BackupRunStatus.Warning, result.Status);
        Assert.Equal(duringSecondCall ? 2 : 1, fixture.Engine.Calls);
        Assert.Equal(3, result.Cells.Count);
        Assert.Equal(BackupRunStatus.Success, result.Cells[0].Status);
        Assert.All(result.Cells.Skip(1), c => Assert.Equal(BackupRunStatus.Skipped, c.Status));
        Assert.Contains(kind == RunKind.Prune ? "2 skipped" : "but was not done at", result.Summary);
        Assert.Contains(result.Warnings, w => w.Contains("cancel", StringComparison.OrdinalIgnoreCase));
        var persisted = Assert.Single(fixture.Logs.LoadHistoryByBackupId(fixture.Backup.Id));
        Assert.Equal(BackupRunStatus.Warning, persisted.Status);
        Assert.Equal(3, persisted.Cells.Count);
    }

    [Theory]
    [InlineData(RunKind.Check)]
    [InlineData(RunKind.Prune)]
    public async Task Cancellation_after_final_success_does_not_invent_unfinished_work(RunKind kind)
    {
        using var fixture = new Fixture(1);
        using var cancelled = new CancellationTokenSource();
        fixture.Engine.Result = (_, _) => { cancelled.Cancel(); return new RunRecord { Status = BackupRunStatus.Success }; };
        var result = await fixture.Run(kind, cancelled.Token);
        Assert.Equal(BackupRunStatus.Success, result.Status);
        Assert.Equal(BackupRunStatus.Success, Assert.Single(result.Cells).Status);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Detailed_engine_warnings_and_errors_are_never_discarded(bool error)
    {
        using var fixture = new Fixture(1);
        fixture.Engine.Result = (_, _) =>
        {
            var result = new RunRecord { Status = BackupRunStatus.Success, Summary = "Engine supplied details" };
            (error ? result.Errors : result.Warnings).Add("A repository detail requiring attention");
            return result;
        };
        var result = await fixture.Run(RunKind.Check);
        Assert.Equal(error ? BackupRunStatus.Failed : BackupRunStatus.Warning, result.Status);
        Assert.Contains("A repository detail requiring attention", error ? result.Errors : result.Warnings);
    }

    [Fact]
    public async Task Missing_destination_is_a_failed_part_and_duplicate_targets_are_not_double_counted()
    {
        using var fixture = new Fixture(1);
        fixture.Backup.Targets.Add(new() { DestinationId = fixture.Backup.Targets[0].DestinationId });
        fixture.Backup.Targets.Add(new() { DestinationId = "missing-destination" });
        var result = await fixture.Run(RunKind.Check);
        Assert.Equal(1, fixture.Engine.Calls);
        Assert.Equal(2, result.Cells.Count);
        Assert.Equal(BackupRunStatus.Failed, result.Status);
        Assert.Contains("no longer exists", result.Summary);
        Assert.Equal("(missing)", result.Cells[1].DestinationName);
    }

    /// <summary>U185: a check or cleanup whose connection went away mid-run is
    /// skipped (it runs again once the connection is back), not failed; with
    /// the connection still there, the same failure stays a failure.</summary>
    [Theory]
    [InlineData(RunKind.Check)]
    [InlineData(RunKind.Prune)]
    public async Task A_connection_lost_mid_run_skips_instead_of_failing(RunKind kind)
    {
        using var fixture = new Fixture(1);
        fixture.Config.Update(c => { var d = c.Destinations.Single(); d.Kind = DestinationKind.GoogleDrive; });
        fixture.Engine.Result = (_, _) => new RunRecord { Status = BackupRunStatus.Failed, Summary = "dial tcp: no such host", StartedUtc = DateTime.UtcNow, EndedUtc = DateTime.UtcNow };
        fixture.Orchestrator.NetworkAvailableOverride = () => false;
        var lost = await fixture.Run(kind);
        Assert.Equal(BackupRunStatus.Skipped, lost.Status);
        Assert.StartsWith("The connection was lost; it runs again once the connection is back.", Assert.Single(lost.Cells).Summary);
        fixture.Orchestrator.NetworkAvailableOverride = () => true;
        var real = await fixture.Run(kind);
        Assert.Equal(BackupRunStatus.Failed, real.Status);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempWorkspace _workspace = new();
        public BackupOrchestrator Orchestrator { get; }
        public ConfigStore Config { get; }
        public Backup Backup { get; }
        public LogStore Logs { get; } = new();
        public OutcomeEngine Engine { get; }

        public Fixture(int sources, bool separateRepositories = false)
        {
            var config = Config = new ConfigStore(); config.Load();
            var secrets = new SecretsStore();
            var destination = new Destination { Name = "Review repository", Kind = DestinationKind.LocalFolder, PathOrSubpath = _workspace.Sub("repository") };
            Backup = new Backup { Name = "Maintenance outcome review", AbortOnMeteredNetwork = false };
            for (int i = 0; i < (separateRepositories ? 1 : sources); i++) Backup.SourcePaths.Add(_workspace.Sub("source-" + i));
            Backup.Targets.Add(new() { DestinationId = destination.Id });
            if (separateRepositories)
                for (int i = 1; i < sources; i++) Backup.Targets.Add(new() { DestinationId = destination.Id, StorageName = "repository-" + i });
            config.Update(c => { c.Backups.Add(Backup); c.Destinations.Add(destination); });
            var engine = DispatchProxy.Create<IBackupEngine, OutcomeEngine>();
            Engine = (OutcomeEngine)(object)engine;
            Orchestrator = new BackupOrchestrator(config, secrets, engine, Logs,
                new HealthcheckService(config, secrets), new MailService(config, secrets), new NotificationService(config), new BackupProgressService());
        }

        public Task<RunRecord> Run(RunKind kind, CancellationToken token = default) => kind == RunKind.Prune
            ? Orchestrator.RunPruneAsync(Backup, token) : Orchestrator.RunCheckAsync(Backup, token);
        public void Dispose() => _workspace.Dispose();
    }

    /// <summary>U179: checks of one backup due together are queued, never run
    /// at once. Two destinations, so the destination's own exclusive access
    /// alone would let the second check start at the first destination while
    /// the first check works at the second.</summary>
    [Fact]
    public async Task Checks_started_together_run_one_after_another()
    {
        using var fixture = new Fixture(2, separateRepositories: true);
        var structure = new BackupCheck { Kind = CheckKind.Structure, Schedule = new BackupSchedule() };
        var sample = new BackupCheck { Kind = CheckKind.DataSample, Schedule = new BackupSchedule() };
        fixture.Backup.Checks = [structure, sample];
        fixture.Config.Update(c => c.Backups.Single(b => b.Id == fixture.Backup.Id).Checks = [structure, sample]);
        var running = 0; var most = 0; var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
        fixture.Engine.Result = (_, _) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            Thread.Sleep(150);
            Interlocked.Decrement(ref running);
            return new RunRecord { Status = BackupRunStatus.Success, Summary = "ok" };
        };
        fixture.Engine.Seen = b => order.Enqueue(b.CheckReadDataSample ? "sample" : "structure");
        var first = Task.Run(() => fixture.Orchestrator.RunCheckAsync(fixture.Backup, structure, CancellationToken.None));
        var second = Task.Run(() => fixture.Orchestrator.RunCheckAsync(fixture.Backup, sample, CancellationToken.None));
        var results = await Task.WhenAll(first, second);
        Assert.Equal(4, fixture.Engine.Calls);
        Assert.Equal(1, most);
        // Each check finished at both destinations before the other began.
        var seen = order.ToArray();
        Assert.Equal(seen[0], seen[1]); Assert.Equal(seen[2], seen[3]); Assert.NotEqual(seen[0], seen[2]);
        Assert.All(results, r => Assert.Equal(BackupRunStatus.Success, r.Status));
        Assert.Equal(structure.Id, results[0].CheckId);
        Assert.Equal(CheckKind.DataSample, results[1].CheckKind);
        Assert.StartsWith("Structure check passed in ", results[0].Summary);
        Assert.StartsWith("Structure check + data sample (1 GiB) passed in ", results[1].Summary);
    }

    /// <summary>The engine gets one check at a time: a structure check, or a
    /// data read limited to the entry's size (U179).</summary>
    [Theory]
    [InlineData(CheckKind.Structure, false, null)]
    [InlineData(CheckKind.DataSample, true, 2L * 1024 * 1024 * 1024)]
    [InlineData(CheckKind.FullDataRead, true, long.MaxValue)]
    public void Each_kind_reaches_the_engine_as_its_own_check(CheckKind kind, bool readsData, long? limit)
    {
        var backup = new Backup { Name = "b", Checks = [new BackupCheck { Kind = CheckKind.Structure }] };
        var view = BackupOrchestrator.EngineViewFor(backup, new BackupCheck { Kind = kind, MaximumBytes = 2L * 1024 * 1024 * 1024 });
        Assert.Equal(readsData, view.CheckReadDataSample);
        Assert.Equal(limit, view.DataCheckLimitBytes);
        Assert.NotNull(view.CheckSchedule); // Never a check of its own inside a backup.
        Assert.False(backup.CheckReadDataSample); // The saved backup is untouched.
    }

    [Fact]
    public async Task A_full_data_read_is_not_started_by_an_engine_that_only_samples()
    {
        using var fixture = new Fixture(1);
        var full = new BackupCheck { Kind = CheckKind.FullDataRead, Schedule = new BackupSchedule() };
        fixture.Config.Update(c => c.Backups.Single(b => b.Id == fixture.Backup.Id).Checks = [full]);
        var result = await fixture.Orchestrator.RunCheckAsync(fixture.Backup, full, CancellationToken.None);
        Assert.Equal(BackupRunStatus.Skipped, result.Status);
        Assert.Equal(0, fixture.Engine.Calls);
        Assert.Contains("can only read a sample", result.Summary);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }

    [Fact]
    public async Task Several_sources_share_one_repository_check_but_keep_separate_retention()
    {
        using var fixture = new Fixture(3);
        Assert.Single((await fixture.Run(RunKind.Check)).Cells);
        Assert.Equal(1, fixture.Engine.Calls);
        Assert.Equal(3, (await fixture.Run(RunKind.Prune)).Cells.Count);
        Assert.Equal(4, fixture.Engine.Calls);
    }

    [Theory]
    [InlineData(0L, 1L)]
    [InlineData(100_000L, 1_000L)]
    [InlineData(107_374_182_400L, 1_073_741_824L)]
    [InlineData(long.MaxValue, 1_073_741_824L)]
    public void Sample_target_uses_the_smaller_percentage_and_size_budget(long stored, long expected)
        => Assert.Equal(expected, ResticEngine.DataCheckSampleBytes(stored));

    [Fact]
    public void Invalid_size_cannot_turn_into_an_unlimited_data_check()
        => Assert.Throws<System.IO.InvalidDataException>(() => ResticEngine.DataCheckSampleBytes(-1));

    [Fact]
    public void Stored_data_size_accepts_the_real_command_heading_but_requires_one_result()
    {
        Assert.Equal(12000, ResticEngine.ParseStoredDataSize("command: stats --mode raw-data --json\n{\"total_size\":12000,\"total_blob_count\":2}\n"));
        Assert.Equal(0, ResticEngine.ParseStoredDataSize("{\"total_size\":0}"));
        foreach (var output in new[] { "no result", "{\"total_size\":-1}", "{\"total_size\":\"12000\"}", "{\"total_size\":1}\n{\"total_size\":2}" })
            Assert.ThrowsAny<Exception>(() => ResticEngine.ParseStoredDataSize(output));
    }

    public class OutcomeEngine : DispatchProxy
    {
        private readonly InMemoryBackupEngine _other = new();
        public int Calls { get; private set; }
        public Action<Backup>? Seen { get; set; }
        public Func<int, CancellationToken, RunRecord> Result { get; set; } = (_, _) => new() { Status = BackupRunStatus.Success };
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name is nameof(IBackupEngine.RunPruneOnlyAsync) or nameof(IBackupEngine.RunCheckOnlyAsync))
            {
                Seen?.Invoke((Backup)args![0]!);
                return Task.FromResult(Result(++Calls, (CancellationToken)args![4]!));
            }
            return method.Invoke(_other, args);
        }
    }
}
