using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services.Platform.Windows;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceCommandLineRunTests
{
    [Fact]
    public void NamesResolveCaseInsensitivelyAndManualBackupsRunOnlyWhenNamed()
    {
        var config = new AppConfig { Backups = { new() { Id = "one", Name = "Documents" }, new() { Id = "two", Name = "Archive", Enabled = false } } };
        Assert.Equal("one", Assert.Single(ServiceCommandLineRun.SelectBackups(config, "DOCUMENTS")).Id);
        // "Run all" takes only backups with automatic backups on.
        Assert.Equal("one", Assert.Single(ServiceCommandLineRun.SelectBackups(config, null)).Id);
        Assert.Empty(ServiceCommandLineRun.SelectBackups(config, "missing"));
        // Named explicitly, a backup with automatic backups off runs, as Run now does (U171).
        Assert.Equal("two", Assert.Single(ServiceCommandLineRun.SelectBackups(config, "archive")).Id);
        config.Backups.Add(new() { Id = "three", Name = "documents" });
        Assert.Throws<IOException>(() => ServiceCommandLineRun.SelectBackups(config, "Documents"));
    }

    [Fact]
    public void OwnerRequestStillCannotSupplyPathsOrExecutionArguments()
    {
        var request = new ServiceHostRequest(1, "start-cli", new string('1', 32), "approved-id", "check");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, ServiceRunAuthorization.Json);
        Assert.Equal("start-cli", ServiceHostRequest.Parse(bytes).Command);
        var json = Encoding.UTF8.GetString(bytes).TrimEnd('}') + ",\"path\":\"C:/outside\"}";
        Assert.Throws<JsonException>(() => ServiceHostRequest.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Throws<InvalidDataException>(() => ServiceHostRequest.Parse(JsonSerializer.SerializeToUtf8Bytes(request with { Operation = "restore" }, ServiceRunAuthorization.Json)));
    }

    [Fact]
    public async Task LostOwnerStartResponseNeverStartsAnotherCopyOrCancelsAnUnknownRun()
    {
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => ServiceScheduledRun.RunAsync(
            new(1, "start-cli", new string('1', 32), "approved-id", "backup"), (_, _) =>
            { calls++; throw new IOException("Lost reply"); }, _ => Task.CompletedTask, TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PausedOwnerCommandReportsNoRunInsteadOfClaimingBackupCompletion()
    {
        ServiceScheduledRun.Response? reported = null;
        var result = await ServiceScheduledRun.RunAsync(new(1, "start-cli", new string('1', 32), "backup", "backup"),
            (_, _) => Task.FromResult(new ServiceScheduledRun.Response(1, null, "paused", 0, null)),
            _ => throw new Exception("A paused command must not wait for a worker"), TestContext.Current.CancellationToken,
            response => reported = response);
        Assert.Equal(0, result); Assert.Equal("paused", reported?.State); Assert.Null(reported?.RunId);
    }
}
