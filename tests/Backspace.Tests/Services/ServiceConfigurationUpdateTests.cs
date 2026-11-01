using System;
using System.IO;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backspace.Models;
using Backspace.Services.Platform.Windows;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceConfigurationUpdateTests
{
    private const string Owner = "S-1-5-21-100-200-300-1001";
    private const string Name = "Backspace-config-test";
    private static byte[] Record(bool ready = false)
    {
        var config = new AppConfig
        {
            Destinations = { new() { Id = "destination", PathOrSubpath = @"D:\Backups" } },
            Backups = { new() { Id = "backup", SourcePaths = { @"C:\Documents" }, Targets = { new() { DestinationId = "destination" } } } }
        };
        var approval = JsonSerializer.Deserialize<ServiceRunAuthorization.Approval>(
            ServiceRunAuthorization.CreateApproval(Owner, Guid.NewGuid().ToString("N"), config), ServiceRunAuthorization.Json)!;
        return JsonSerializer.SerializeToUtf8Bytes(new ServiceConfigurationUpdate.Pending(1, Name, approval, ready), ServiceRunAuthorization.Json);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovery_retains_the_exact_approved_settings_and_phase(bool ready)
    {
        var bytes = Record(ready);
        var saved = ServiceConfigurationUpdate.Parse(bytes, Name, Owner);
        Assert.Equal(ready, saved.Ready);
        Assert.Equal(@"C:\Documents", Assert.Single(saved.Approval.Configuration.Backups[0].SourcePaths));
        Assert.Equal(bytes, JsonSerializer.SerializeToUtf8Bytes(saved, ServiceRunAuthorization.Json));
    }

    [Theory]
    [InlineData("service")]
    [InlineData("owner")]
    [InlineData("digest")]
    [InlineData("revision")]
    [InlineData("source")]
    [InlineData("missing-target")]
    [InlineData("extra-arguments")]
    [InlineData("version")]
    [InlineData("extra-field")]
    public void Inconsistent_recovery_input_is_rejected(string alteration)
    {
        var saved = JsonNode.Parse(Record())!;
        switch (alteration)
        {
            case "service": saved["serviceName"] = "Backspace-other"; break;
            case "owner": saved["approval"]!["ownerSid"] = "S-1-5-21-100-200-300-1002"; break;
            case "digest": saved["approval"]!["configurationSha256"] = new string('0',64); break;
            case "revision": saved["approval"]!["revision"] = "invalid"; break;
            case "source": saved["approval"]!["configuration"]!["backups"]![0]!["sourcePaths"]![0] = @"C:\Windows"; break;
            case "missing-target": saved["approval"]!["configuration"]!["backups"]![0]!["targets"] = new JsonArray(); break;
            case "extra-arguments": saved["approval"]!["configuration"]!["backups"]![0]!["extraBackupArgs"] = "--password-command anything"; break;
            case "version": saved["version"] = 99; break;
            case "extra-field": saved["command"] = "cmd.exe"; break;
        }
        var error = Xunit.Record.Exception(() => ServiceConfigurationUpdate.Parse(Encoding.UTF8.GetBytes(saved.ToJsonString()), Name, Owner));
        Assert.True(error is SecurityException or InvalidDataException or JsonException, error?.ToString() ?? "Invalid update was accepted");
    }

    [Fact]
    public void Duplicate_ready_fields_cannot_bypass_incomplete_update_block()
    {
        var bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Record()).Replace("\"ready\":false", "\"ready\":false,\"ready\":true"));
        Assert.Throws<InvalidDataException>(() => ServiceConfigurationUpdate.Parse(bytes, Name, Owner));
    }

    [Fact]
    public void Removing_the_last_backup_produces_no_authorized_work_or_schedules()
    {
        var config = new AppConfig();
        var revision = Guid.NewGuid().ToString("N");
        var policy = ServiceRunAuthorization.ValidateForTest(ServiceRunAuthorization.CreateApproval(Owner, revision, config), Owner);
        Assert.Empty(ServiceScheduleRegistration.Plans(policy.Snapshot().Configuration));
        Assert.Throws<SecurityException>(() => policy.Authorize(JsonSerializer.SerializeToUtf8Bytes(
            new ServiceRunAuthorization.Request(1, revision, "removed-backup", "backup"), ServiceRunAuthorization.Json)));
    }

    [Theory]
    [InlineData("negative-time")]
    [InlineData("tomorrow-time")]
    [InlineData("unknown-frequency")]
    [InlineData("empty-week")]
    [InlineData("invalid-week")]
    [InlineData("zero-interval")]
    [InlineData("negative-limit")]
    public void Invalid_schedule_is_rejected_before_a_recovery_record_can_be_created(string problem)
    {
        var config = ServiceConfigurationUpdate.Parse(Record(), Name, Owner).Approval.Configuration;
        var schedule = config.Backups[0].Schedule;
        switch (problem)
        {
            case "negative-time": schedule.TimeOfDay = TimeSpan.FromSeconds(-1); break;
            case "tomorrow-time": schedule.TimeOfDay = TimeSpan.FromDays(1); break;
            case "unknown-frequency": schedule.Frequency = (ScheduleFrequency)999; break;
            case "empty-week": schedule.Frequency = ScheduleFrequency.Weekly; schedule.WeeklyDaysBitmask = 0; break;
            case "invalid-week": schedule.Frequency = ScheduleFrequency.Weekly; schedule.WeeklyDaysBitmask = 128; break;
            case "zero-interval": schedule.Frequency = ScheduleFrequency.Hourly; schedule.HourlyInterval = 0; break;
            case "negative-limit": schedule.ExecutionTimeLimit = TimeSpan.FromSeconds(-1); break;
        }
        var error = Xunit.Record.Exception(() => ServiceRunAuthorization.CreateApproval(Owner, Guid.NewGuid().ToString("N"), config));
        Assert.True(error is InvalidDataException or JsonException, error?.ToString() ?? "Invalid schedule was approved");
    }
}
