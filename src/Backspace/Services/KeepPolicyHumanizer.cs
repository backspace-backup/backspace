using System;
using System.Collections.Generic;
using System.Linq;

namespace Backspace.Services;

/// <summary>
/// Explains a backup's retention policy in plain English for the Backup
/// editor, as it actually runs (<see cref="KeepPolicyRules"/>, U172): the
/// policy keeps Duplicacy's <c>n:m</c> syntax (ConfigMigrator imports it
/// directly), and Backspace applies it with restic's keep-within options.
/// Ages count back from the latest backup, so when backups stop nothing
/// more is removed.
/// </summary>
public static class KeepPolicyHumanizer
{
    public static readonly string Example = "e.g. 0:365 30:90 7:30 1:7";

    public static KeepPolicyDescription Describe(string? policy)
    {
        var parsed = KeepPolicyRules.Parse(policy);
        if (!parsed.Valid)
            return new KeepPolicyDescription(false, parsed.Error!, Array.Empty<string>(), parsed.Error);
        if (parsed.KeepLast is { } last)
        {
            var only = last == 1 ? "Only the latest backup is kept; older ones are deleted." : $"The latest {last} backups are kept; older ones are deleted.";
            return new KeepPolicyDescription(true, only, new[] { "• " + only }, null);
        }
        if (parsed.Rules.Count == 0)
            return new KeepPolicyDescription(true, "No policy set: every backup is kept forever.", Array.Empty<string>(), null);

        var lines = new List<string>();
        foreach (var band in KeepPolicyRules.Bands(parsed))
        {
            var when = band.To is { } to
                ? band.From == 0 ? $"Up to {Days(to)} old" : $"{band.From} to {Days(to)} old"
                : $"Older than {Days(band.From)}";
            var what = band.Deleted ? "deleted"
                : band.KeepOnePer is { } period ? "the last backup of each " + Name(period) + " is kept"
                : "every backup is kept";
            lines.Add($"• {when}: {what}.");
        }
        var notes = new List<string>();
        foreach (var rule in parsed.Rules.Where(r => r.Every > 0 && (int)KeepPolicyRules.PeriodFor(r.Every) != r.Every).Select(r => r.Every).Distinct())
            notes.Add($"One every {rule} days is kept as one per {Name(KeepPolicyRules.PeriodFor(rule))}, so never fewer than the rule asks.");
        notes.Add("Ages count back from the latest backup, so if backups stop, nothing more is deleted.");
        var summary = string.Join("\n", lines) + "\n" + string.Join(" ", notes);
        return new KeepPolicyDescription(true, summary, lines, null);
    }

    private static string Days(int days) => days == 1 ? "1 day" : $"{days} days";

    private static string Name(KeepPolicyRules.Period period) => period switch
    {
        KeepPolicyRules.Period.Day => "day",
        KeepPolicyRules.Period.Week => "week",
        KeepPolicyRules.Period.Month => "month",
        _ => "year",
    };
}

public readonly record struct KeepPolicyDescription(
    bool Valid,
    string Summary,
    IReadOnlyList<string> Lines,
    string? Error);
