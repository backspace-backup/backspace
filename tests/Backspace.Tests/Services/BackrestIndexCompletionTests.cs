using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class BackrestIndexCompletionTests
{
    private const string Snapshot = "snapshot-for-this-backup";

    [Fact]
    public void CountersAreScopedToTheRepositoryAndTask_AndAcceptScientificNotation()
    {
        var text = Metric("repo", "success", "2e0") + Metric("other", "success", "50") +
            Metric("repo", "failed", "1") + Metric("repo", "success", "80").Replace("index_snapshots", "backup");
        Assert.Equal(new BackrestIndexCounters(2, 1), BackrestIndexCounters.Parse(text, "repo"));
        Assert.Equal(default, BackrestIndexCounters.Parse("# No index tasks yet\n", "repo"));
        Assert.Throws<InvalidDataException>(() => BackrestIndexCounters.Parse(Metric("repo", "success", "NaN"), "repo"));
        Assert.Throws<InvalidDataException>(() => BackrestIndexCounters.Parse(Metric("repo", "success", "-1"), "repo"));
    }

    [Fact]
    public async Task NeitherAnUnrelatedIndexNorAnEarlyRecordReleasesTheWait()
    {
        using var handler = new Handler(); using var http = Client(handler);
        var maintenance = new MaintenanceCoordinator();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiting = BackrestIndexCompletion.WaitAsync(new(http), "repo", "guid", Snapshot,
            new(4, 0), maintenance, deadline.Token);
        handler.Success = 5; handler.Record = "some-other-snapshot";
        await Task.Delay(150, deadline.Token);
        Assert.False(waiting.IsCompleted);
        handler.Success = 4; handler.Record = Snapshot;
        await Task.Delay(150, deadline.Token);
        Assert.False(waiting.IsCompleted);
        handler.Success = 5;
        await waiting;
        Assert.False(maintenance.IsBlocked);
    }

    [Fact]
    public async Task FailedIndexBlocksFurtherWorkInsteadOfAssumingTheRepositoryIsIdle()
    {
        using var scratch = new TempWorkspace("index-failure");
        using var handler = new Handler { Failed = 1 }; using var http = Client(handler);
        var maintenance = new MaintenanceCoordinator();
        try
        {
            await Assert.ThrowsAsync<IOException>(() => BackrestIndexCompletion.WaitAsync(new(http), "repo", "guid", Snapshot,
                new(4, 0), maintenance, TestContext.Current.CancellationToken));
            Assert.True(maintenance.IsBlocked);
        }
        finally { maintenance.ReleaseEngineOwnershipAfterStop(); } // No real engine exists in this HTTP fixture.
    }

    [Fact]
    public async Task CancellationWaitsForConfirmation_ThenCancelsWithoutBlocking()
    {
        using var handler = new Handler(); using var http = Client(handler);
        var maintenance = new MaintenanceCoordinator(); using var stop = new CancellationTokenSource();
        var waiting = BackrestIndexCompletion.WaitAsync(new(http), "repo", "guid", Snapshot,
            new(4, 0), maintenance, stop.Token, TimeSpan.FromSeconds(2));
        stop.Cancel();
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        handler.Success = 5; handler.Record = Snapshot;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(maintenance.IsBlocked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrUncertainSchedulingWithoutConfirmationKeepsFurtherWorkBlocked(bool schedulingUncertain)
    {
        using var scratch = new TempWorkspace("index-uncertain");
        using var handler = new Handler(); using var http = Client(handler);
        var maintenance = new MaintenanceCoordinator(); using var stop = new CancellationTokenSource();
        if (!schedulingUncertain) stop.Cancel();
        try
        {
            await Assert.ThrowsAsync<IOException>(() => BackrestIndexCompletion.WaitAsync(new(http), "repo", "guid", Snapshot,
                new(4, 0), maintenance, stop.Token, TimeSpan.FromMilliseconds(150), schedulingUncertain));
            Assert.True(maintenance.IsBlocked);
        }
        finally { maintenance.ReleaseEngineOwnershipAfterStop(); } // Synthetic handler has finished; release only its fixture gate.
    }

    private static string Metric(string repo, string status, string count) =>
        $"backrest_tasks_run_total{{plan_id=\"_unassociated_\",repo_id=\"{repo}\",status=\"{status}\",task_type=\"index_snapshots\"}} {count}\n";
    private static HttpClient Client(Handler handler) => new(handler) { BaseAddress = new Uri("http://index-fixture.invalid") };
    private sealed class Handler : HttpMessageHandler
    {
        internal long Success = 4, Failed;
        internal string? Record;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/metrics")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    Metric("repo", "success", Interlocked.Read(ref Success).ToString()) + Metric("repo", "failed", Interlocked.Read(ref Failed).ToString()), Encoding.UTF8, "text/plain") });
            var record = Volatile.Read(ref Record);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { operations = record is null ? Array.Empty<object>() : new object[] { new { snapshotId = record, status = "STATUS_SUCCESS", operationIndexSnapshot = new { } } } }), Encoding.UTF8, "application/json") });
        }
    }
}
