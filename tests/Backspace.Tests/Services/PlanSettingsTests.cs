using System.Collections.Generic;
using System.Text.Json;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class PlanSettingsTests
{
    [Theory]
    [InlineData("STATUS_ERROR", "command failed: exit status 11", true)]
    [InlineData("STATUS_ERROR", "command failed: exit status 11\n", true)]
    [InlineData("STATUS_ERROR", "command failed: exit status 1", false)]
    [InlineData("STATUS_ERROR", "command failed: exit status 110", false)]
    [InlineData("STATUS_ERROR", "snapshot end hook: exit status 11", false)]
    [InlineData("STATUS_SUCCESS", "exit status 11", false)]
    [InlineData("STATUS_INPROGRESS", "exit status 11", false)]
    [InlineData("STATUS_CANCELLED", "exit status 11", false)]
    [InlineData("STATUS_ERROR", "A previous command had exit status 11; current failure is unknown", false)]
    public void OnlyAConfirmedLockFailureCanStartRecovery(string status, string message, bool expected) =>
        Assert.Equal(expected, ResticBackrestEngine.IsConfirmedLockFailure(new() { Status = status, DisplayMessage = message }));

    [Fact]
    public void RetentionCommandIsScopedAndEmptyPoliciesDoNothing()
    {
        var command = ResticBackrestEngine.BuildRetentionCommand("my-plan", "my-instance",
            new() { PolicyTimeBucketed = new() { KeepLastN = 2, Daily = 7, Monthly = 12 } });
        Assert.Equal("forget --tag 'plan:my-plan,created-by:my-instance' --group-by= --keep-last=2 --keep-daily=7 --keep-monthly=12", command);
        Assert.Null(ResticBackrestEngine.BuildRetentionCommand("plan", "instance", new() { PolicyKeepAll = true }));
        Assert.Null(ResticBackrestEngine.BuildRetentionCommand("plan", "instance", new()));
        Assert.Throws<System.InvalidOperationException>(() => ResticBackrestEngine.BuildRetentionCommand("plan", "", new() { PolicyKeepLastN = 1 }));
        Assert.Throws<System.InvalidOperationException>(() => ResticBackrestEngine.BuildRetentionCommand("plan,another-plan", "instance", new() { PolicyKeepLastN = 1 }));
    }

    [Theory]
    [InlineData("write config file: cannot replace config.json with tempfile: Access is denied.", true)]
    [InlineData("write config file: replace: file is being used by another process", true)]
    [InlineData("failed to update config: backup config file: cannot replace config.json.bak.2026-10-16-20-02-08 with tempfile: Access is denied.", true)]
    [InlineData("backup config file: cannot replace config.json.bak: sharing violation", true)]
    [InlineData("backup config file: disk full", false)]
    [InlineData("backup config file: Access is denied", false)]
    [InlineData("backup failed: Access is denied", false)]
    [InlineData("write config file: disk full", false)]
    [InlineData("invalid config: missing repository", false)]
    public void ConfigWriteRetryOnlyRecognizesAnAtomicReplaceDenial(string message, bool retry) =>
        Assert.Equal(retry, ResticBackrestEngine.IsRetryableConfigWrite(message));

    [Fact]
    public void ExistingPlanReceivesEditedOptions_WithoutDroppingUnknownDaemonFields()
    {
        var backup = new Backup { FiltersText = "old.txt", KeepPolicy = "1", UseVss = false };
        var plan = ResticBackrestEngine.BuildPlanFromBackup("plan", "repo", backup, "source");
        plan.AdditionalFields = new Dictionary<string, JsonElement> { ["futureSetting"] = JsonSerializer.SerializeToElement("preserved") };
        backup.FiltersText = "new.txt";
        backup.KeepPolicy = "3";
        backup.UseVss = true;
        var wanted = ResticBackrestEngine.BuildPlanFromBackup("plan", "repo", backup, "source");
        Assert.True(ResticBackrestEngine.ApplyPlanSettings(plan, wanted));
        Assert.Equal(wanted.Excludes, plan.Excludes);
        Assert.Equal(wanted.IExcludes, plan.IExcludes);
        Assert.DoesNotContain("old.txt", plan.Excludes);
        Assert.DoesNotContain("old.txt", plan.IExcludes);
        Assert.Equal(wanted.BackupFlags, plan.BackupFlags);
        Assert.Equal(3, plan.Retention!.PolicyTimeBucketed!.KeepLastN);
        Assert.Equal("preserved", plan.AdditionalFields["futureSetting"].GetString());
        Assert.False(ResticBackrestEngine.ApplyPlanSettings(plan, wanted));
    }

    [Fact]
    public void ClearingExclusionsAndDisablingRetentionReplacesEarlierValues()
    {
        var backup = new Backup { FiltersText = "old.txt", KeepPolicy = "1" };
        var plan = ResticBackrestEngine.BuildPlanFromBackup("plan", "repo", backup, "source");
        backup.FiltersText = "";
        var wanted = ResticBackrestEngine.BuildPlanFromBackup("plan", "repo", backup, "source");
        wanted.Retention = new() { PolicyKeepAll = true };
        Assert.True(ResticBackrestEngine.ApplyPlanSettings(plan, wanted));
        Assert.Empty(plan.Excludes);
        Assert.Empty(plan.IExcludes);
        Assert.True(plan.Retention!.PolicyKeepAll);
        Assert.Null(plan.Retention.PolicyTimeBucketed);
    }
}
