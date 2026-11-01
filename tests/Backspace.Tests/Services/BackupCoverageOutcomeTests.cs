using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class BackupCoverageOutcomeTests
{
    [Theory]
    [InlineData(BackupRunStatus.Success, BackupRunStatus.Skipped, false, BackupRunStatus.Warning, false)]
    [InlineData(BackupRunStatus.Success, BackupRunStatus.Success, true, BackupRunStatus.Warning, false)]
    [InlineData(BackupRunStatus.Failed, BackupRunStatus.Skipped, false, BackupRunStatus.Failed, false)]
    [InlineData(BackupRunStatus.Skipped, BackupRunStatus.Skipped, false, BackupRunStatus.Skipped, false)]
    [InlineData(BackupRunStatus.Warning, BackupRunStatus.Success, false, BackupRunStatus.Warning, true)]
    [InlineData(BackupRunStatus.Success, BackupRunStatus.Success, false, BackupRunStatus.Success, true)]
    public async Task Actual_orchestration_preserves_coverage_failure_priority_and_last_complete_backup(
        BackupRunStatus first, BackupRunStatus second, bool cancelAfterFirst, BackupRunStatus expected, bool complete)
    {
        using var workspace = new TempWorkspace();
        var config = new ConfigStore(); config.Load();
        var secrets = new SecretsStore(); var logs = new LogStore();
        var destination = new Destination { Name = "Coverage repository", Kind = DestinationKind.LocalFolder, PathOrSubpath = workspace.Sub("repository") };
        var priorSuccess = DateTime.UtcNow.AddDays(-3);
        const string ping = "https://coverage.invalid/ping/00000000-0000-0000-0000-000000000001";
        var backup = new Backup { Name = "Partial coverage review", AbortOnMeteredNetwork = false, UseVss = false, VssDisabledAcknowledged = true,
            LastSuccessUtc = priorSuccess, HealthcheckPingUrl = ping,
            SourcePaths = { workspace.Sub("source-a"), workspace.Sub("source-b") }, Targets = { new() { DestinationId = destination.Id } } };
        config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(destination);
            c.Notifications.OnSuccess = new(); c.Notifications.OnSkipped = new(); c.Notifications.OnFailure = new(); });
        using var cancelled = new CancellationTokenSource();
        using var handler = new RecordingHandler(); using var http = new HttpClient(handler);
        var engine = DispatchProxy.Create<IBackupEngine, OutcomeEngine>();
        ((OutcomeEngine)(object)engine).Result = call =>
        {
            if (cancelAfterFirst || first == BackupRunStatus.Failed && call == 2) cancelled.Cancel();
            var status = call == 1 ? first : second;
            if (status == BackupRunStatus.Failed) throw new PermanentFailure();
            return new RunRecord { Status = status, Summary = "Source result: " + status };
        };
        var orchestrator = new BackupOrchestrator(config, secrets, engine, logs, new HealthcheckService(config, secrets, http),
            new MailService(config, secrets), new NotificationService(config), new BackupProgressService());
        var result = await orchestrator.RunBackupAsync(backup, cancelled.Token);

        Assert.Equal(expected, result.Status);
        Assert.Equal(expected, Assert.Single(logs.LoadHistoryByBackupId(backup.Id)).Status);
        var saved = config.Current.Backups.Single();
        Assert.Equal(expected, saved.LastRunStatus);
        if (complete) Assert.True(saved.LastSuccessUtc > priorSuccess);
        else Assert.Equal(priorSuccess, saved.LastSuccessUtc);
        Assert.Equal(complete, handler.Addresses.Contains(ping));
        if (first == BackupRunStatus.Failed) Assert.Contains(handler.Addresses, u => u.EndsWith("/fail", StringComparison.Ordinal));
        if (cancelAfterFirst) Assert.Contains(result.Warnings, w => w.Contains("before all sources", StringComparison.Ordinal));
    }

    private sealed class PermanentFailure : Exception, INonRetriable
    { public PermanentFailure() : base("The first source cannot be backed up.") { } }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Addresses { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Addresses.Add(request.RequestUri!.AbsoluteUri); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }

    public class OutcomeEngine : DispatchProxy
    {
        private readonly InMemoryBackupEngine _other = new();
        private int _calls;
        public Func<int, RunRecord> Result { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method!.Name == nameof(IBackupEngine.BackupPruneCheckAsync)
                ? Task.FromResult(Result(++_calls)) : method.Invoke(_other, args);
    }
}
