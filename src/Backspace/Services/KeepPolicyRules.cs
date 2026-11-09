using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Backspace.Services;

/// <summary>
/// A backup's retention policy (<see cref="Models.Backup.KeepPolicy"/>) and
/// the restic <c>forget</c> options that apply it (U172, 9 November 2026).
///
/// <para>Accepted forms:</para>
/// <list type="bullet">
/// <item>Empty: keep everything (no forget runs).</item>
/// <item>A whole number N: keep the latest N snapshots.</item>
/// <item>Duplicacy's <c>n:m</c> pairs, e.g. <c>0:365 30:180 7:90 1:14</c>:
///   for snapshots older than m days keep one every n days; n = 0 deletes
///   snapshots older than m days; everything younger than the smallest m
///   is kept.</item>
/// </list>
///
/// <para>Restic mapping: everything within the smallest m is kept with
/// <c>--keep-within=&lt;m&gt;d</c>. Each rule n:m with n &gt; 0 keeps the last
/// snapshot of each calendar period with <c>--keep-within-daily|weekly|monthly|yearly</c>
/// up to the next larger m (or for ever with <c>--keep-daily|...=unlimited</c>
/// when no larger m follows). The period is the longest calendar period
/// that is not longer than n days (1 day, 7 days, a month = 30, a year = 365),
/// so a rule whose n is none of those keeps at least as many snapshots as
/// Duplicacy would (n = 10 keeps one per week). Restic measures every
/// duration back from the latest snapshot, Duplicacy from the current time:
/// when backups stop, restic removes nothing more.</para>
/// </summary>
public static class KeepPolicyRules
{
    /// <summary>The calendar periods restic can thin to, finest first.</summary>
    public enum Period { Day = 1, Week = 7, Month = 30, Year = 365 }

    public readonly record struct Rule(int Every, int OlderThan);

    /// <summary>One age band of the policy as it runs: from
    /// <paramref name="From"/> days to <paramref name="To"/> days (null: no
    /// end), what is kept there (null period: all; <paramref name="Deleted"/>:
    /// nothing).</summary>
    public readonly record struct Band(int From, int? To, Period? KeepOnePer, bool Deleted, bool MoreOftenThanAsked);

    public sealed record Parsed(bool Valid, string? Error, int? KeepLast, IReadOnlyList<Rule> Rules)
    {
        public bool KeepsEverything => Valid && KeepLast is null && Rules.Count == 0;
    }

    /// <summary>The period restic uses for "one every <paramref name="every"/>
    /// days": the longest one not longer than that, so it never keeps fewer.</summary>
    public static Period PeriodFor(int every) => every switch
    {
        >= 365 => Period.Year,
        >= 30 => Period.Month,
        >= 7 => Period.Week,
        _ => Period.Day,
    };

    public static Parsed Parse(string? policy)
    {
        var text = (policy ?? "").Trim();
        if (text.Length == 0) return new(true, null, null, Array.Empty<Rule>());
        if (!text.Contains(':'))
        {
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var last) && last > 0)
                return new(true, null, last, Array.Empty<Rule>());
            return Invalid($"“{text}” isn't a valid policy. Use a number of backups to keep, or n:m rules such as 0:365 30:90 7:30 1:7.");
        }

        var rules = new List<Rule>();
        foreach (var part in text.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = part.Split(':');
            if (bits.Length != 2
                || !int.TryParse(bits[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                || !int.TryParse(bits[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m))
                return Invalid($"“{part}” isn't a valid rule. Use space-separated n:m pairs where n and m are whole numbers (n = keep one per N days, m = age in days).");
            rules.Add(new Rule(n, m));
        }
        rules.Sort((a, b) => a.OlderThan.CompareTo(b.OlderThan));
        for (var i = 1; i < rules.Count; i++)
            if (rules[i].OlderThan == rules[i - 1].OlderThan)
                return Invalid($"Two rules apply from {rules[i].OlderThan} days. Give each rule its own age.");
        var deleteAt = rules.FindIndex(r => r.Every == 0);
        if (deleteAt >= 0 && deleteAt != rules.Count - 1)
            return Invalid($"“0:{rules[deleteAt].OlderThan}” deletes everything older than {rules[deleteAt].OlderThan} days, so it must have the largest age of all the rules.");
        if (deleteAt == 0 && rules[0].OlderThan == 0)
            return Invalid("“0:0” would delete every backup. Use a larger age.");
        return new(true, null, null, rules);
    }

    private static Parsed Invalid(string error) => new(false, error, null, Array.Empty<Rule>());

    /// <summary>The restic forget options for a valid policy; empty when it
    /// keeps everything (or is invalid: nothing must be deleted then).</summary>
    public static IReadOnlyList<string> ResticOptions(Parsed policy)
    {
        if (!policy.Valid) return Array.Empty<string>();
        if (policy.KeepLast is { } last) return new[] { "--keep-last=" + last.ToString(CultureInfo.InvariantCulture) };
        var rules = policy.Rules;
        if (rules.Count == 0) return Array.Empty<string>();
        var options = new List<string>();
        if (rules[0].OlderThan > 0) options.Add("--keep-within=" + Days(rules[0].OlderThan));
        foreach (var (period, until) in Thinning(rules))
        {
            var name = period switch { Period.Day => "daily", Period.Week => "weekly", Period.Month => "monthly", _ => "yearly" };
            options.Add(until is { } days ? $"--keep-within-{name}={Days(days)}" : $"--keep-{name}=unlimited");
        }
        return options;
    }

    /// <summary>For each period in use, how far back (days; null = no limit)
    /// restic keeps one snapshot per period. Rules sharing a period merge
    /// into the longest reach, which only ever keeps more.</summary>
    private static IEnumerable<(Period Period, int? Until)> Thinning(IReadOnlyList<Rule> rules)
    {
        var reach = new SortedDictionary<Period, int?>();
        for (var i = 0; i < rules.Count; i++)
        {
            if (rules[i].Every == 0) continue;
            var period = PeriodFor(rules[i].Every);
            int? until = i + 1 < rules.Count ? rules[i + 1].OlderThan : null;
            if (reach.TryGetValue(period, out var was))
                reach[period] = was is null || until is null ? null : Math.Max(was.Value, until.Value);
            else
                reach[period] = until;
        }
        return reach.Select(kv => (kv.Key, kv.Value));
    }

    /// <summary>What the policy keeps in each age band, as restic applies it:
    /// the finest period whose reach covers the band.
    /// <see cref="Band.MoreOftenThanAsked"/> is true when that period is
    /// shorter than the rule's n days.</summary>
    public static IReadOnlyList<Band> Bands(Parsed policy)
    {
        if (!policy.Valid || policy.Rules.Count == 0) return Array.Empty<Band>();
        var rules = policy.Rules;
        var reach = Thinning(rules).ToList();
        var bands = new List<Band>();
        if (rules[0].OlderThan > 0) bands.Add(new Band(0, rules[0].OlderThan, null, false, false));
        for (var i = 0; i < rules.Count; i++)
        {
            int? to = i + 1 < rules.Count ? rules[i + 1].OlderThan : null;
            if (rules[i].Every == 0) { bands.Add(new Band(rules[i].OlderThan, null, null, true, false)); continue; }
            var period = reach.Where(r => r.Until is null || r.Until > rules[i].OlderThan).Select(r => r.Period).Min();
            bands.Add(new Band(rules[i].OlderThan, to, period, false, (int)period != rules[i].Every));
        }
        return bands;
    }

    private static string Days(int days) => days.ToString(CultureInfo.InvariantCulture) + "d";
}
