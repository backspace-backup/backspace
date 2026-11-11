using System.Linq;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U172 (9 November 2026): the retention policy is applied as its
/// Duplicacy rules say, not as a fixed daily/weekly/monthly count.</summary>
public sealed class KeepPolicyRulesTests
{
    private static string Options(string policy) => string.Join(' ', KeepPolicyRules.ResticOptions(KeepPolicyRules.Parse(policy)));

    [Theory]
    // The owner's three backups, imported from Duplicacy.
    [InlineData("0:365 30:180 7:90 1:14", "--keep-within=14d --keep-within-daily=90d --keep-within-weekly=180d --keep-within-monthly=365d")]
    [InlineData("0:365 30:90 7:30 1:7", "--keep-within=7d --keep-within-daily=30d --keep-within-weekly=90d --keep-within-monthly=365d")]
    [InlineData("0:365 30:180 7:30 1:7", "--keep-within=7d --keep-within-daily=30d --keep-within-weekly=180d --keep-within-monthly=365d")]
    // Order and separators don't matter.
    [InlineData("1:7 7:30 30:90 0:365", "--keep-within=7d --keep-within-daily=30d --keep-within-weekly=90d --keep-within-monthly=365d")]
    [InlineData("0:365,30:90  7:30\t1:7", "--keep-within=7d --keep-within-daily=30d --keep-within-weekly=90d --keep-within-monthly=365d")]
    // No delete rule: the oldest rule keeps going for ever.
    [InlineData("7:30 1:7", "--keep-within=7d --keep-within-daily=30d --keep-weekly=unlimited")]
    [InlineData("365:730", "--keep-within=730d --keep-yearly=unlimited")]
    // Only a delete rule: keep everything younger.
    [InlineData("0:90", "--keep-within=90d")]
    // A rule from day 0: no keep-within, thinning starts at once.
    [InlineData("1:0 0:30", "--keep-within-daily=30d")]
    // n that isn't 1/7/30/365: the next finer calendar period, which keeps more.
    [InlineData("0:365 10:30 3:7", "--keep-within=7d --keep-within-daily=30d --keep-within-weekly=365d")]
    [InlineData("0:730 90:180 45:60", "--keep-within=60d --keep-within-monthly=730d")]
    [InlineData("0:3650 400:365", "--keep-within=365d --keep-within-yearly=3650d")]
    // Two rules sharing a period merge into the longer reach.
    [InlineData("0:365 2:30 1:7", "--keep-within=7d --keep-within-daily=365d")]
    // A finer rule further back extends that period to the end.
    [InlineData("1:90 7:30 0:3650", "--keep-within=30d --keep-within-daily=3650d --keep-within-weekly=90d")]
    // Bare count and empty.
    [InlineData("3", "--keep-last=3")]
    [InlineData(" 1 ", "--keep-last=1")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Policies_map_to_the_restic_options_that_apply_them(string? policy, string expected) =>
        Assert.Equal(expected, Options(policy!));

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("abc")]
    [InlineData("7:")]
    [InlineData(":30")]
    [InlineData("7:30:1")]
    [InlineData("-1:30")]
    [InlineData("7:-30")]
    [InlineData("1.5:30")]
    [InlineData("7:30 1:30")]   // two rules from the same age
    [InlineData("0:30 7:90")]   // delete rule not the oldest
    [InlineData("0:0")]         // would delete everything
    [InlineData("0:365 3")]
    public void Odd_or_unsafe_policies_are_refused_and_delete_nothing(string policy)
    {
        var parsed = KeepPolicyRules.Parse(policy);
        Assert.False(parsed.Valid);
        Assert.False(string.IsNullOrWhiteSpace(parsed.Error));
        Assert.Empty(KeepPolicyRules.ResticOptions(parsed));
        Assert.Null(ResticEngine.RetentionArguments("plan", "instance", KeepPolicyRules.ResticOptions(parsed)));
        Assert.False(KeepPolicyHumanizer.Describe(policy).Valid);
    }

    [Fact]
    public void Every_policy_the_editor_accepts_keeps_the_newest_window_whole()
    {
        // The rule younger than the smallest m keeps everything, so the
        // options always start with keep-within unless thinning starts at 0.
        foreach (var policy in new[] { "0:365 30:180 7:90 1:14", "0:365 30:90 7:30 1:7", "0:365 30:180 7:30 1:7", "7:30", "0:90" })
            Assert.StartsWith("--keep-within=", Options(policy));
    }

    [Fact]
    public void The_editor_explains_what_runs_for_the_owners_local_c_policy()
    {
        var text = KeepPolicyHumanizer.Describe("0:365 30:180 7:90 1:14");
        Assert.True(text.Valid);
        Assert.Equal(new[]
        {
            "• Up to 14 days old: every backup is kept.",
            "• 14 to 90 days old: the last backup of each day is kept.",
            "• 90 to 180 days old: the last backup of each week is kept.",
            "• 180 to 365 days old: the last backup of each month is kept.",
            "• Older than 365 days: deleted.",
        }, text.Lines);
        Assert.Contains("Ages count back from the latest backup", text.Summary);
        Assert.DoesNotContain("every 1", text.Summary);
    }

    [Fact]
    public void The_editor_says_when_an_odd_interval_keeps_more_than_asked()
    {
        var text = KeepPolicyHumanizer.Describe("0:365 10:30 3:7");
        Assert.Equal(new[]
        {
            "• Up to 7 days old: every backup is kept.",
            "• 7 to 30 days old: the last backup of each day is kept.",
            "• 30 to 365 days old: the last backup of each week is kept.",
            "• Older than 365 days: deleted.",
        }, text.Lines);
        Assert.Contains("One every 3 days is kept as one per day", text.Summary);
        Assert.Contains("One every 10 days is kept as one per week", text.Summary);
        // Exact periods get no such note.
        Assert.DoesNotContain("is kept as one per", KeepPolicyHumanizer.Describe("0:365 30:90 7:30 1:7").Summary);
    }

    [Fact]
    public void The_editor_explains_counts_and_empty_policies()
    {
        Assert.Equal("The latest 3 backups are kept; older ones are deleted.", KeepPolicyHumanizer.Describe("3").Summary);
        Assert.Equal("Only the latest backup is kept; older ones are deleted.", KeepPolicyHumanizer.Describe("1").Summary);
        Assert.True(KeepPolicyHumanizer.Describe("").Valid);
        Assert.Contains("every backup is kept forever", KeepPolicyHumanizer.Describe(null).Summary);
        var open = KeepPolicyHumanizer.Describe("7:30 1:7").Lines;
        Assert.Equal("• Older than 30 days: the last backup of each week is kept.", open.Last());
    }

    [Fact]
    public void A_finer_rule_further_back_shows_as_kept_daily_where_it_reaches()
    {
        var bands = KeepPolicyRules.Bands(KeepPolicyRules.Parse("1:90 7:30 0:3650"));
        Assert.Equal(KeepPolicyRules.Period.Day, bands.Single(b => b.From == 30).KeepOnePer);
        Assert.True(bands.Single(b => b.From == 30).MoreOftenThanAsked);
        Assert.Equal(KeepPolicyRules.Period.Day, bands.Single(b => b.From == 90).KeepOnePer);
        Assert.True(bands.Single(b => b.From == 3650).Deleted);
    }
}
