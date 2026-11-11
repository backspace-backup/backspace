using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class PlanSettingsTests
{
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
        // Previous run finished, pruned two days ago: nothing to do.
        Assert.False(ResticBackrestEngine.NeedsPrune(previousUnfinished: false, now.AddDays(-2), now));
        Assert.False(ResticBackrestEngine.NeedsPrune(previousUnfinished: false, now.AddDays(-6.9), now));
        // Previous run cut short (an error, a stop, a crash): prune.
        Assert.True(ResticBackrestEngine.NeedsPrune(previousUnfinished: true, now.AddDays(-2), now));
        // Never pruned (as far as Backspace's records say), or a week ago or more: prune.
        Assert.True(ResticBackrestEngine.NeedsPrune(previousUnfinished: false, null, now));
        Assert.True(ResticBackrestEngine.NeedsPrune(previousUnfinished: false, now.AddDays(-7), now));
        Assert.True(ResticBackrestEngine.NeedsPrune(previousUnfinished: false, now.AddDays(-9), now));
    }

    /// <summary>U175: the forget run directly is the same command as the one
    /// Backrest ran (its string, split as Backrest split it).</summary>
    [Theory]
    [InlineData("0:365 30:180 7:90 1:14")]
    [InlineData("2")]
    [InlineData("0:365 30:90 7:30 1:7")]
    public void The_direct_forget_is_the_command_Backrest_ran(string policy)
    {
        var options = KeepPolicyRules.ResticOptions(KeepPolicyRules.Parse(policy));
        var planId = ResticBackrestEngine.MakePlanId(new Backup { Id = "b" }, @"C:\Users\me", new Destination { Id = "d" }, "default");
        var command = ResticBackrestEngine.BuildRetentionCommand(planId, ResticBackrestEngine.InstanceTag, options)!;
        Assert.Equal(DirectBackupTests.ShellSplit(command), ResticBackrestEngine.RetentionArguments(planId, ResticBackrestEngine.InstanceTag, options));
        Assert.Null(ResticBackrestEngine.RetentionArguments(planId, ResticBackrestEngine.InstanceTag, []));
        Assert.Throws<System.InvalidOperationException>(() => ResticBackrestEngine.RetentionArguments("a,b", ResticBackrestEngine.InstanceTag, options));
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
