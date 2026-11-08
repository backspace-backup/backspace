using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>
/// The backup card's "Last backup" line (U160, 7 November 2026). A finished
/// backup reads "15m ago · 27.4 MB uploaded"; the counts and duration that the
/// run summary carries ("27.42 MB uploaded · 150 new / 195 mod / 0 rem · in
/// 1m 29s") are shown on hover, in words, and in Activity. A failed or skipped
/// run keeps its reason on the card: that is what the person has to act on.
/// </summary>
public static partial class LastRunLine
{
    [GeneratedRegex(@"(?<size>[\d.,]+ [KMGT]?B) uploaded")]
    private static partial Regex Uploaded();
    [GeneratedRegex(@"(?<new>\d+) new / (?<mod>\d+) mod / (?<rem>\d+) rem")]
    private static partial Regex Counts();
    [GeneratedRegex(@"(?:^|· )(?:Completed )?in (?<dur>\d[^·]*?)\s*(?:·|$)")]
    private static partial Regex Took();
    [GeneratedRegex(@"(?<n>\d+) (?<kind>warning|error)s?")]
    private static partial Regex Problems();

    /// <summary>The card text and its hover text for a run that ended.</summary>
    public static (string Label, string Hover) For(string summary, BackupRunStatus status, DateTime endedUtc, DateTime? nowUtc = null)
    {
        var ago = Ago(endedUtc, nowUtc ?? DateTime.UtcNow);
        if (status is not (BackupRunStatus.Success or BackupRunStatus.Warning))
        {
            var full = $"{summary} · {ago}";
            return (full, full);
        }
        var size = Uploaded().Match(summary) is { Success: true } u ? u.Groups["size"].Value : null;
        var counts = Counts().Match(summary);
        var took = Took().Match(summary) is { Success: true } t ? t.Groups["dur"].Value.Trim() : null;
        var problems = Problems().Match(summary) is { Success: true } p ? p.Value : null;
        // A summary in another shape (an older version's, a fixture's) is
        // shown as it is rather than guessed at.
        if (size is null && !counts.Success && took is null)
            return ($"{ago} · {summary}", $"{summary} · finished " + endedUtc.ToLocalTime().ToString("ddd d MMM, HH:mm", CultureInfo.CurrentCulture));

        var label = ago + " · " + (size is null ? "nothing new to upload" : size + " uploaded");
        if (problems is not null) label += " · " + problems;

        var hover = new System.Collections.Generic.List<string>
        {
            "Finished " + endedUtc.ToLocalTime().ToString("ddd d MMM, HH:mm", CultureInfo.CurrentCulture),
        };
        if (took is not null) hover.Add("took " + took);
        if (counts.Success)
            hover.Add($"{counts.Groups["new"].Value} new, {counts.Groups["mod"].Value} changed and {counts.Groups["rem"].Value} deleted files since the previous backup");
        hover.Add(size is null ? "nothing new to upload" : size + " of new data uploaded");
        if (problems is not null) hover.Add(problems + " (see Activity)");
        return (label, string.Join(" · ", hover));
    }

    private static string Ago(DateTime utc, DateTime now)
    {
        var d = now - utc;
        if (d < TimeSpan.FromMinutes(1)) return "Just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes}m ago";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }
}
