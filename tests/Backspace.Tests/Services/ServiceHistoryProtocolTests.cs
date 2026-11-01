using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Backspace.Models;
using Backspace.Services.Platform.Windows;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceHistoryProtocolTests
{
    private static ServiceHistoryProtocol.Response Send(ServiceHistoryProtocol protocol, ServiceHistoryProtocol.Request request)
        => JsonSerializer.Deserialize<ServiceHistoryProtocol.Response>(protocol.Dispatch(
            JsonSerializer.SerializeToUtf8Bytes(request, ServiceRunAuthorization.Json)), ServiceRunAuthorization.Json)!;

    [Fact]
    public void Pages_remain_stable_when_a_new_run_arrives_and_never_include_another_backup()
    {
        var records = Enumerable.Range(0, 8).Select(n => new RunRecord
        { BackupId = "chosen", StartedUtc = new DateTime(2026, 10, 20).AddMinutes(n), Summary = "Run " + n }).ToList();
        records.Add(new() { BackupId = "other", Summary = "Unrelated" });
        var protocol = new ServiceHistoryProtocol(_ => records);
        var first = Send(protocol, new(1, "chosen", Limit: 3));
        Assert.Null(first.Error);
        Assert.Equal(new[] { "Run 7", "Run 6", "Run 5" }, first.Runs.Select(r => r.Summary));
        records.Add(new() { BackupId = "chosen", StartedUtc = new DateTime(2026, 10, 21), Summary = "New" });
        var second = Send(protocol, new(1, "chosen", first.NextAfterRunId, 3));
        Assert.Equal(new[] { "Run 4", "Run 3", "Run 2" }, second.Runs.Select(r => r.Summary));
        var last = Send(protocol, new(1, "chosen", second.NextAfterRunId, 3));
        Assert.Equal(new[] { "Run 1", "Run 0" }, last.Runs.Select(r => r.Summary));
        Assert.Null(last.NextAfterRunId);
        Assert.All(first.Runs.Concat(second.Runs).Concat(last.Runs), r => Assert.Equal("chosen", r.BackupId));
    }

    [Fact]
    public void Read_does_not_modify_service_history_or_offer_private_transcript_paths()
    {
        var original = new RunRecord { BackupId = "chosen", LogPath = "private-root.log",
            Cells = new() { new() { LogPath = "private-cell.log", Summary = "Kept" } } };
        var response = Send(new(_ => new() { original }), new(1, "chosen"));
        Assert.Equal("private-root.log", original.LogPath);
        Assert.Equal("private-cell.log", original.Cells[0].LogPath);
        Assert.Empty(response.Runs[0].LogPath);
        Assert.Empty(response.Runs[0].Cells[0].LogPath);
        Assert.Equal("Kept", response.Runs[0].Cells[0].Summary);
    }

    [Fact]
    public void Size_limit_returns_a_continuation_without_silently_losing_large_records()
    {
        var records = Enumerable.Range(0, 4).Select(n => new RunRecord
        { BackupId = "chosen", StartedUtc = new DateTime(2026, 10, 20).AddMinutes(n), Summary = new string('x', 300_000) }).ToList();
        var protocol = new ServiceHistoryProtocol(_ => records);
        var first = Send(protocol, new(1, "chosen", Limit: 100));
        Assert.Equal(2, first.Runs.Length);
        var next = Send(protocol, new(1, "chosen", first.NextAfterRunId, 100));
        Assert.Equal(2, next.Runs.Length);
        Assert.Equal(4, first.Runs.Concat(next.Runs).Select(r => r.Id).Distinct().Count());
        Assert.Null(next.NextAfterRunId);
        records[3].Summary = new string('x', ServiceHistoryProtocol.MaximumResponseBytes);
        Assert.Equal("run-too-large", Send(protocol, new(1, "chosen")).Error);
    }

    [Fact]
    public void Removed_cursor_is_reported_instead_of_returning_an_incomplete_page()
    {
        Assert.Equal("history-changed", Send(new(_ => new()), new(1, "chosen", Guid.NewGuid().ToString("N"))).Error);
    }

    [Theory]
    [InlineData("{\"version\":1,\"backupId\":\"x\",\"path\":\"C:/private\"}")]
    [InlineData("{\"version\":1,\"backupId\":\"x\",\"backupId\":\"y\"}")]
    [InlineData("{\"version\":1,\"backupId\":\"x\",\"limit\":101}")]
    [InlineData("{\"version\":1,\"backupId\":\"x\",\"limit\":0}")]
    [InlineData("{\"version\":1,\"backupId\":\"x\",\"afterRunId\":\"../../secrets.bin\"}")]
    [InlineData("{\"version\":2,\"backupId\":\"x\"}")]
    public void Invalid_request_does_not_access_history(string json)
    {
        var calls = 0;
        var protocol = new ServiceHistoryProtocol(_ => { calls++; return new(); });
        var response = JsonSerializer.Deserialize<ServiceHistoryProtocol.Response>(protocol.Dispatch(Encoding.UTF8.GetBytes(json)), ServiceRunAuthorization.Json)!;
        Assert.Equal("invalid-history-request", response.Error);
        Assert.Equal(0, calls);
    }
}
