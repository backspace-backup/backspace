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

    /// <summary>U180 (10 November 2026): after Quit, "Backspace.exe --check
    /// local_d" printed "The backup command could not complete: The operation
    /// requested for service … has not been completed within the specified
    /// time interval." The owner's command now starts the service for that
    /// command only; a start by Windows still stays stopped.</summary>
    [Fact]
    public void After_Quit_the_service_starts_for_the_owners_command_only()
    {
        Assert.Equal(SystemServiceLifetime.StartMode.CommandOnly, SystemServiceLifetime.ModeFor(staysQuit: true, [SystemServiceLifetime.ForCommand]));
        // Windows starting it after Quit (a trigger, waking from sleep): stays stopped.
        Assert.Equal(SystemServiceLifetime.StartMode.StayStopped, SystemServiceLifetime.ModeFor(staysQuit: true, []));
        Assert.Equal(SystemServiceLifetime.StartMode.StayStopped, SystemServiceLifetime.ModeFor(staysQuit: true, null));
        Assert.Equal(SystemServiceLifetime.StartMode.StayStopped, SystemServiceLifetime.ModeFor(staysQuit: true, ["--something-else"]));
        // No Quit: a normal start, with or without the command's argument.
        Assert.Equal(SystemServiceLifetime.StartMode.Normal, SystemServiceLifetime.ModeFor(staysQuit: false, [SystemServiceLifetime.ForCommand]));
        Assert.Equal(SystemServiceLifetime.StartMode.Normal, SystemServiceLifetime.ModeFor(staysQuit: false, null));
    }

    [Fact]
    public void A_service_started_for_a_command_stops_again_once_the_command_is_done()
    {
        var start = new DateTime(2026, 11, 10, 21, 0, 0, DateTimeKind.Utc);
        // Waiting for the command's first run.
        Assert.False(SystemServiceLifetime.CommandDone(start, null, anyActive: false, start.AddSeconds(30)));
        // No run came at all.
        Assert.True(SystemServiceLifetime.CommandDone(start, null, anyActive: false, start + SystemServiceLifetime.CommandWait));
        // A run in progress, however long.
        Assert.False(SystemServiceLifetime.CommandDone(start, start.AddHours(2), anyActive: true, start.AddHours(3)));
        // Between two backups of --run-all: the next one comes within seconds.
        var ended = start.AddMinutes(10);
        Assert.False(SystemServiceLifetime.CommandDone(start, ended, anyActive: false, ended.AddSeconds(5)));
        Assert.True(SystemServiceLifetime.CommandDone(start, ended, anyActive: false, ended + SystemServiceLifetime.CommandIdle));
    }

    [Fact]
    public void A_command_that_cannot_run_says_which_command_why_and_what_to_do()
    {
        var timeout = new System.ServiceProcess.TimeoutException(
            "The operation requested for service 'Backspace-ab12' has not been completed within the specified time interval.");
        var check = ServiceCommandLineRun.Failure("check", timeout);
        Assert.StartsWith("The check command could not run: Backspace's background service did not start within 20 seconds", check);
        Assert.Contains("Open Backspace", check);
        Assert.DoesNotContain("backup command", check);
        Assert.StartsWith("The cleanup command could not run: ", ServiceCommandLineRun.Failure("prune", timeout));
        Assert.StartsWith("The backup command could not run: ", ServiceCommandLineRun.Failure("backup", timeout));
        // Any other reason is given as it is.
        Assert.Equal("The check command could not run: This backup no longer exists.",
            ServiceCommandLineRun.Failure("check", new InvalidDataException("This backup no longer exists.")));
        Assert.Equal("cleanup", ServiceCommandLineRun.Noun("prune"));
    }
}
