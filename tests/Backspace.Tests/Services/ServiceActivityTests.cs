using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceActivityTests
{
    [Fact]
    public void Captures_real_progress_without_private_transcript_paths_or_mutating_history()
    {
        var progress = new BackupProgressService();
        progress.RegisterRunCells("b", ["C:\\Docs"], ["Drive"]);
        progress.ReportCellStarted("b", "C:\\Docs", "Drive");
        progress.ReportLine("b", "C:\\Docs", "Drive", "[0:01] 42.00%  42 files 42 B, total 100 files 100 B, 0 errors");
        var run = new RunRecord { BackupId = "b", StartedUtc = DateTime.UtcNow.AddSeconds(-1), EndedUtc = DateTime.UtcNow,
            Status = BackupRunStatus.Success, LogPath = "private-run.log", Cells = { new() { LogPath = "private-cell.log" } } };
        var copy = ServiceActivity.ParseProgress(ServiceActivity.Encode(ServiceActivity.Capture("b", progress, "", run)), "b");
        Assert.Equal(42, copy.Percent); Assert.True(Assert.Single(copy.Sources).Running);
        Assert.Equal("", copy.Result!.LogPath); Assert.Equal("", Assert.Single(copy.Result.Cells).LogPath);
        Assert.Equal("private-run.log", run.LogPath); Assert.Equal("private-cell.log", run.Cells[0].LogPath);
    }

    /// <summary>U256 (owner, 16 November 2026: "Cannot reach the background
    /// backup service" after the night's checks and cleanups). A finished check
    /// or cleanup reports no live result; the service read it back from its
    /// history, log paths included, and the window refused the whole report.</summary>
    [Fact]
    public void A_result_read_back_from_history_reaches_the_window_without_its_log_paths()
    {
        var saved = new RunRecord { Id = Guid.NewGuid().ToString("N"), BackupId = "b", Kind = RunKind.Prune,
            StartedUtc = DateTime.UtcNow.AddMinutes(-5), EndedUtc = DateTime.UtcNow, Status = BackupRunStatus.Success,
            LogPath = @"C:\config\service\logs\engine\b\cleanup.log", Cells = { new() { LogPath = @"C:\config\service\logs\engine\b\cleanup.log" } } };
        // As 1.1.54 sent it: refused.
        Assert.Throws<InvalidDataException>(() => ServiceActivity.ParseProgress(
            ServiceActivity.Encode(new ServiceActivity.Progress("b", 0, "", [], [], saved, LogRunId: saved.Id)), "b"));
        // As sent now: accepted, the history itself unchanged.
        var copy = ServiceActivity.ParseProgress(
            ServiceActivity.Encode(new ServiceActivity.Progress("b", 0, "", [], [], ServiceActivity.Metadata(saved), LogRunId: saved.Id)), "b");
        Assert.Equal(BackupRunStatus.Success, copy.Result!.Status);
        Assert.Equal("", copy.Result.LogPath);
        Assert.EndsWith("cleanup.log", saved.LogPath);
    }

    [Theory]
    [InlineData("wrong-backup")]
    [InlineData("negative-percent")]
    [InlineData("duplicate-source")]
    [InlineData("unfinished-result")]
    [InlineData("private-path")]
    [InlineData("tail-too-large")]
    [InlineData("tail-position")]
    public void Invalid_or_unrelated_worker_progress_is_rejected(string kind)
    {
        var p = new ServiceActivity.Progress("b", 10, "", [], []);
        p = kind switch
        {
            "wrong-backup" => p with { BackupId = "other" },
            "negative-percent" => p with { Percent = -1 },
            "duplicate-source" => p with { Sources = [new("C:\\D", 0, false, "", ""), new("c:\\d", 0, false, "", "")] },
            "unfinished-result" => p with { Result = new() { BackupId = "b", Status = BackupRunStatus.Running } },
            "tail-too-large" => p with { TranscriptTail = new string('x', ServiceActivity.TranscriptCharacters + 1), TranscriptEnd = ServiceActivity.TranscriptCharacters + 1 },
            "tail-position" => p with { TranscriptTail = "one", TranscriptEnd = 2 },
            _ => p with { Result = new() { BackupId = "b", StartedUtc = DateTime.UtcNow.AddSeconds(-1), EndedUtc = DateTime.UtcNow,
                Status = BackupRunStatus.Success, LogPath = "private.log" } }
        };
        Assert.Throws<InvalidDataException>(() => ServiceActivity.ParseProgress(ServiceActivity.Encode(p), "b"));
    }

    [Fact]
    public void Activity_request_cannot_carry_a_command_or_path()
    {
        Assert.Equal("activity", ServiceHostRequest.Parse(Encoding.UTF8.GetBytes("{\"version\":1,\"command\":\"activity\"}")).Command);
        Assert.ThrowsAny<Exception>(() => ServiceHostRequest.Parse(Encoding.UTF8.GetBytes("{\"version\":1,\"command\":\"activity\",\"backupId\":\"C:\\\\secret\"}")));
        Assert.ThrowsAny<Exception>(() => ServiceHostRequest.Parse(Encoding.UTF8.GetBytes("{\"version\":1,\"command\":\"activity\",\"command\":\"start\"}")));
        Assert.Throws<InvalidDataException>(() => ServiceActivity.Parse(new byte[ServiceActivity.MaximumBytes + 1]));
    }

    [Fact]
    public async Task A_failed_read_preserves_the_live_run_and_cancellation_targets_its_exact_identity()
    {
        var runId = Guid.NewGuid().ToString("N"); var count = 0; string? cancelled = null;
        using var monitor = new BackgroundServiceActivity(_ => ++count == 1
            ? Task.FromResult<ServiceActivity.Snapshot?>(new(1, runId, "running", "b", "backup", DateTime.UtcNow, null))
            : Task.FromException<ServiceActivity.Snapshot?>(new IOException("offline")),
            (id, _) => { cancelled = id; return Task.CompletedTask; });
        await monitor.RefreshAsync(CancellationToken.None); await monitor.RefreshAsync(CancellationToken.None);
        Assert.Equal(runId, monitor.Current!.RunId); Assert.Equal("running", monitor.Current.State);
        Assert.Contains("completion has not been confirmed", monitor.Error);
        await monitor.CancelAsync(runId, CancellationToken.None); Assert.Equal(runId, cancelled);
    }

    [Fact]
    public async Task Overlapping_poll_does_not_start_a_second_read_or_replace_the_first_response()
    {
        var completion = new TaskCompletionSource<ServiceActivity.Snapshot?>(); var calls = 0;
        using var monitor = new BackgroundServiceActivity(_ => { calls++; return completion.Task; }, (_, _) => Task.CompletedTask);
        var first = monitor.RefreshAsync(CancellationToken.None);
        await monitor.RefreshAsync(CancellationToken.None);
        Assert.Equal(1, calls);
        completion.SetResult(new(1, null, "idle", null, null, null, null)); await first;
        Assert.Equal("idle", monitor.Current!.State);
    }
}
