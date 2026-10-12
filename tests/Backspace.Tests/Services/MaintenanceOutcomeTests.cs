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
        using var fixture = new Fixture(2);
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
        using var fixture = new Fixture(3);
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
        Assert.Contains("2 skipped", result.Summary);
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
        Assert.Contains("2 parts", result.Summary);
        Assert.Equal("(missing)", result.Cells[1].DestinationName);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempWorkspace _workspace = new();
        private readonly BackupOrchestrator _orchestrator;
        public Backup Backup { get; }
        public LogStore Logs { get; } = new();
        public OutcomeEngine Engine { get; }

        public Fixture(int sources)
        {
            var config = new ConfigStore(); config.Load();
            var secrets = new SecretsStore();
            var destination = new Destination { Name = "Review repository", Kind = DestinationKind.LocalFolder, PathOrSubpath = _workspace.Sub("repository") };
            Backup = new Backup { Name = "Maintenance outcome review", AbortOnMeteredNetwork = false };
            for (int i = 0; i < sources; i++) Backup.SourcePaths.Add(_workspace.Sub("source-" + i));
            Backup.Targets.Add(new() { DestinationId = destination.Id });
            config.Update(c => { c.Backups.Add(Backup); c.Destinations.Add(destination); });
            var engine = DispatchProxy.Create<IBackupEngine, OutcomeEngine>();
            Engine = (OutcomeEngine)(object)engine;
            _orchestrator = new BackupOrchestrator(config, secrets, engine, Logs,
                new HealthcheckService(config, secrets), new MailService(config, secrets), new NotificationService(config), new BackupProgressService());
        }

        public Task<RunRecord> Run(RunKind kind, CancellationToken token = default) => kind == RunKind.Prune
            ? _orchestrator.RunPruneAsync(Backup, token) : _orchestrator.RunCheckAsync(Backup, token);
        public void Dispose() => _workspace.Dispose();
    }

    public class OutcomeEngine : DispatchProxy
    {
        private readonly InMemoryBackupEngine _other = new();
        public int Calls { get; private set; }
        public Func<int, CancellationToken, RunRecord> Result { get; set; } = (_, _) => new() { Status = BackupRunStatus.Success };
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name is nameof(IBackupEngine.RunPruneOnlyAsync) or nameof(IBackupEngine.RunCheckOnlyAsync))
                return Task.FromResult(Result(++Calls, (CancellationToken)args![4]!));
            return method.Invoke(_other, args);
        }
    }
}
