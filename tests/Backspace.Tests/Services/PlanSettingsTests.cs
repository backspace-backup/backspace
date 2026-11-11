using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class PlanSettingsTests
{
    /// <summary>The forget restic runs (U172, U175): this plan's snapshots
    /// only (both labels, AND-ed), one group, the keep options; nothing at all
    /// when the policy keeps everything; refused when anything could widen it.</summary>
    [Fact]
    public void RetentionCommandIsScopedAndEmptyPoliciesDoNothing()
    {
        Assert.Equal(new[] { "forget", "--tag", "plan:my-plan,created-by:my-instance", "--group-by=", "--keep-within=14d",
                "--keep-within-daily=90d", "--keep-within-weekly=180d", "--keep-within-monthly=365d" },
            ResticBackrestEngine.RetentionArguments("my-plan", "my-instance",
                KeepPolicyRules.ResticOptions(KeepPolicyRules.Parse("0:365 30:180 7:90 1:14"))));
        Assert.Equal(new[] { "forget", "--tag", "plan:p,created-by:i", "--group-by=", "--keep-last=2" },
            ResticBackrestEngine.RetentionArguments("p", "i", KeepPolicyRules.ResticOptions(KeepPolicyRules.Parse("2"))));
        Assert.Null(ResticBackrestEngine.RetentionArguments("plan", "instance", KeepPolicyRules.ResticOptions(KeepPolicyRules.Parse(""))));
        Assert.Null(ResticBackrestEngine.RetentionArguments("plan", "instance", KeepPolicyRules.ResticOptions(KeepPolicyRules.Parse("0:30 7:90"))));
        Assert.Null(ResticBackrestEngine.RetentionArguments("plan", "instance", []));
        Assert.Throws<System.InvalidOperationException>(() => ResticBackrestEngine.RetentionArguments("plan", "", ["--keep-last=1"]));
        Assert.Throws<System.InvalidOperationException>(() => ResticBackrestEngine.RetentionArguments("plan,another-plan", "instance", ["--keep-last=1"]));
        Assert.Throws<System.InvalidOperationException>(() => ResticBackrestEngine.RetentionArguments("plan", "instance", ["--keep-last=1 --prune"]));
        Assert.Throws<System.InvalidOperationException>(() => ResticBackrestEngine.RetentionArguments("plan", "instance", ["unlock"]));
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
}
