using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>
/// The backup card's "Last backup" line (U160, 7 November 2026). A finished
/// backup reads "15 min ago · 27.4 MB uploaded"; the counts and duration that the
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
    // "waited 51m 46s": time queued behind another backup (U173).
    [GeneratedRegex(@"(?:^|· )waited (?<dur>\d[^·]*?)\s*(?:·|$)")]
    private static partial Regex Waited();
    // One " · " part that is only a duration: "in 1s", "Completed in 2m", "waited 3m".
    [GeneratedRegex(@"^(?:(?:Completed |skipped after |failed after )?in \d[\w ]*|skipped after \d[\w ]*|waited \d[\w ]*)$")]
    private static partial Regex TookPart();
    [GeneratedRegex(@"^waited \d[\w ]*$")]
    private static partial Regex WaitedPart();
    [GeneratedRegex(@"(?<n>\d+) (?<kind>warning|error)s?")]
    private static partial Regex Problems();
    // "8,335 online-only files skipped" (U168), in any digit grouping.
    [GeneratedRegex(@"(?:^|· )(?<phrase>\d[\d,.  ' ]* online-only files? skipped)")]
    private static partial Regex CloudSkipped();

    /// <summary>The card text and its hover text for a run that ended.</summary>
    public static (string Label, string Hover) For(string summary, BackupRunStatus status, DateTime endedUtc, DateTime? nowUtc = null)
    {
        var ago = Ago(endedUtc, nowUtc ?? DateTime.UtcNow);
        if (status is not (BackupRunStatus.Success or BackupRunStatus.Warning))
        {
            // The reason stays on the card, the time it took goes to the hover,
            // as for a finished run (owner, 28 November 2026: local_dropbox's
            // line said "1 error · in 1s" where the others show no duration).
            var parts = summary.Split(" · ");
            var timed = parts.Where(p => TookPart().IsMatch(p) || WaitedPart().IsMatch(p)).ToList();
            var reason = string.Join(" · ", parts.Where(p => !timed.Contains(p)));
            if (reason.Length == 0) reason = summary;
            var reasonLine = $"{reason} · {ago}";
            var details = new System.Collections.Generic.List<string> { reason, "ended " + endedUtc.ToLocalTime().ToString("ddd d MMM, HH:mm", CultureInfo.CurrentCulture) };
            if (Took().Match(summary) is { Success: true } tookFailed) details.Add("took " + tookFailed.Groups["dur"].Value.Trim());
            if (Waited().Match(summary) is { Success: true } waitedFailed) details.Add("waited " + waitedFailed.Groups["dur"].Value.Trim() + " for its turn before that");
            return (reasonLine, string.Join(" · ", details));
        }
        var size = Uploaded().Match(summary) is { Success: true } u ? u.Groups["size"].Value : null;
        var counts = Counts().Match(summary);
        var took = Took().Match(summary) is { Success: true } t ? t.Groups["dur"].Value.Trim() : null;
        var problems = Problems().Match(summary) is { Success: true } p ? p.Value : null;
        var cloud = CloudSkipped().Match(summary) is { Success: true } c ? c.Groups["phrase"].Value : null;
        // A summary in another shape (an older version's, a fixture's) is
        // shown as it is rather than guessed at.
        if (size is null && !counts.Success && took is null)
            return ($"{ago} · {summary}", $"{summary} · finished " + endedUtc.ToLocalTime().ToString("ddd d MMM, HH:mm", CultureInfo.CurrentCulture));

        var label = ago + " · " + Change(size, counts);
        if (problems is not null) label += " · " + problems;

        var hover = new System.Collections.Generic.List<string>
        {
            "Finished " + endedUtc.ToLocalTime().ToString("ddd d MMM, HH:mm", CultureInfo.CurrentCulture),
        };
        if (took is not null) hover.Add("took " + took);
        if (Waited().Match(summary) is { Success: true } w) hover.Add("waited " + w.Groups["dur"].Value.Trim() + " for its turn before that");
        if (counts.Success)
            hover.Add($"{counts.Groups["new"].Value} new, {counts.Groups["mod"].Value} changed and {counts.Groups["rem"].Value} deleted files since the previous backup");
        hover.Add(size is null ? "nothing new to upload" : size + " of new data uploaded");
        if (cloud is not null) hover.Add(cloud + ", kept only in the cloud");
        if (problems is not null) hover.Add(problems + " (see Activity)");
        return (label, string.Join(" · ", hover));
    }

    /// <summary>What the run changed, in the card's few words. A few hundred
    /// bytes is only the backup's own bookkeeping, not your files (owner, 15
    /// November 2026: "293 B uploaded: it is ridiculous to show such a low
    /// amount"), so under 1 MB the card counts files instead: "no changes",
    /// "3 files changed". The amount stays on hover.</summary>
    private static string Change(string? size, Match counts)
    {
        var files = counts.Success
            ? int.Parse(counts.Groups["new"].Value) + int.Parse(counts.Groups["mod"].Value) + int.Parse(counts.Groups["rem"].Value)
            : (int?)null;
        if (files == 0) return "no changes";
        if (size is null) return "nothing new to upload";
        if (BytesOf(size) is { } bytes && bytes < 1024 * 1024 && files is { } n)
            return n == 1 ? "1 file changed" : $"{n:N0} files changed";
        return size + " uploaded";
    }

    private static double? BytesOf(string size)
    {
        var parts = size.Split(' ');
        if (parts.Length != 2 || !double.TryParse(parts[0].Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
        return parts[1] switch { "B" => v, "KB" => v * 1024, "MB" => v * 1024 * 1024, "GB" => v * 1024 * 1024 * 1024, "TB" => v * 1024d * 1024 * 1024 * 1024, _ => null };
    }

    // Capitalised: it starts the line.
    private static string Ago(DateTime utc, DateTime now) => HumanFormat.Ago(utc, now) is var ago && ago == "just now" ? "Just now" : ago;
}
