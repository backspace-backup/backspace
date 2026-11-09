using System.Collections.Generic;
using System.Linq;
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
            KeepPolicyRules.ResticOptions(KeepPolicyRules.Parse("0:365 30:180 7:90 1:14")));
        Assert.Equal("forget --tag 'plan:my-plan,created-by:my-instance' --group-by= --keep-within=14d --keep-within-daily=90d --keep-within-weekly=180d --keep-within-monthly=365d", command);
        Assert.Equal("forget --tag 'plan:p,created-by:i' --group-by= --keep-last=2",
            ResticBackrestEngine.BuildRetentionCommand("p", "i", KeepPolicyRules.ResticOptions(KeepPolicyRules.Parse("2"))));
        Assert.Null(ResticBackrestEngine.BuildRetentionCommand("plan", "instance", KeepPolicyRules.ResticOptions(KeepPolicyRules.Parse(""))));
        Assert.Null(ResticBackrestEngine.BuildRetentionCommand("plan", "instance", KeepPolicyRules.ResticOptions(KeepPolicyRules.Parse("0:30 7:90"))));
        Assert.Null(ResticBackrestEngine.BuildRetentionCommand("plan", "instance", []));
        Assert.Throws<System.InvalidOperationException>(() => ResticBackrestEngine.BuildRetentionCommand("plan", "", ["--keep-last=1"]));
        Assert.Throws<System.InvalidOperationException>(() => ResticBackrestEngine.BuildRetentionCommand("plan,another-plan", "instance", ["--keep-last=1"]));
        Assert.Throws<System.InvalidOperationException>(() => ResticBackrestEngine.BuildRetentionCommand("plan", "instance", ["--keep-last=1 --prune"]));
        Assert.Throws<System.InvalidOperationException>(() => ResticBackrestEngine.BuildRetentionCommand("plan", "instance", ["unlock"]));
    }

    /// <summary>U131 (4 November 2026): a backup cut short leaves data restic
    /// sent but had not listed yet, and only a prune removes it. A prune runs
    /// after the next backup, and at least weekly in any case.</summary>
    [Fact]
    public void A_prune_follows_a_backup_cut_short_and_runs_at_least_weekly()
    {
        var now = new System.DateTime(2026, 11, 5, 12, 0, 0, System.DateTimeKind.Utc);
        long Ms(System.DateTime at) => new System.DateTimeOffset(at).ToUnixTimeMilliseconds();
        var backup = JsonDocument.Parse("{}").RootElement;
        BackrestOperation Backup(string plan, string status, double hoursAgo) =>
            new() { PlanId = plan, Status = status, UnixTimeStartMs = Ms(now.AddHours(-hoursAgo)), OperationBackup = backup };
        BackrestOperation Prune(double daysAgo, string status = "STATUS_SUCCESS") =>
            new() { Status = status, UnixTimeStartMs = Ms(now.AddDays(-daysAgo)), OperationRunCommand = new() { Command = "prune" } };
        var before = Ms(now.AddMinutes(-90)); // when this backup started

        // Previous run finished, pruned two days ago: nothing to do.
        Assert.False(ResticBackrestEngine.NeedsPrune([Backup("p", "STATUS_SUCCESS", 24), Prune(2)], "p", before, now));
        // Previous run cut short (error, cancelled by Windows or by the user): prune.
        foreach (var status in new[] { "STATUS_ERROR", "STATUS_SYSTEM_CANCELLED", "STATUS_USER_CANCELLED" })
            Assert.True(ResticBackrestEngine.NeedsPrune([Backup("p", "STATUS_SUCCESS", 48), Backup("p", status, 3), Prune(2)], "p", before, now));
        // Only the previous run of this plan counts, not this run or another plan's.
        Assert.False(ResticBackrestEngine.NeedsPrune([Backup("p", "STATUS_SUCCESS", 24), Backup("p", "STATUS_INPROGRESS", 1), Backup("q", "STATUS_ERROR", 3), Prune(2)], "p", before, now));
        // Never pruned, or more than a week ago, or the last attempt failed: prune.
        Assert.True(ResticBackrestEngine.NeedsPrune([Backup("p", "STATUS_SUCCESS", 24)], "p", before, now));
        Assert.True(ResticBackrestEngine.NeedsPrune([Backup("p", "STATUS_SUCCESS", 24), Prune(8)], "p", before, now));
        Assert.True(ResticBackrestEngine.NeedsPrune([Backup("p", "STATUS_SUCCESS", 24), Prune(1, "STATUS_ERROR"), Prune(9)], "p", before, now));
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
        // Backrest's own after-backup forget must never delete: Backspace
        // applies the KeepPolicy itself (U172).
        Assert.True(plan.Retention!.PolicyKeepAll);
        Assert.Equal("preserved", plan.AdditionalFields["futureSetting"].GetString());
        Assert.False(ResticBackrestEngine.ApplyPlanSettings(plan, wanted));
    }

    [Fact]
    public void ClearingExclusionsAndDisablingRetentionReplacesEarlierValues()
    {
        var backup = new Backup { FiltersText = "old.txt", KeepPolicy = "1" };
        var plan = ResticBackrestEngine.BuildPlanFromBackup("plan", "repo", backup, "source");
        // A plan saved by an older version carried a count-based policy.
        plan.Retention = new() { PolicyTimeBucketed = new() { KeepLastN = 10, Daily = 7 } };
        backup.FiltersText = "";
        var wanted = ResticBackrestEngine.BuildPlanFromBackup("plan", "repo", backup, "source");
        Assert.True(ResticBackrestEngine.ApplyPlanSettings(plan, wanted));
        // Only the essential exclusions remain, which always apply (U106).
        Assert.DoesNotContain(plan.Excludes.Concat(plan.IExcludes), p => p.EndsWith("old.txt"));
        Assert.Equal(wanted.Excludes, plan.Excludes);
        Assert.Equal(wanted.IExcludes, plan.IExcludes);
        // "source" is a folder, not a drive: no drive-top rules either.
        Assert.DoesNotContain(plan.IExcludes.Concat(plan.Excludes), p => p.EndsWith("/hiberfil.sys"));
        Assert.True(plan.Retention!.PolicyKeepAll);
        Assert.Null(plan.Retention.PolicyTimeBucketed);
    }
}
