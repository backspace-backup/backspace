using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Threading;

namespace Backspace.Services;

/// <summary>
/// "Exclude this folder" in What this backup added (U178, 10 November 2026):
/// the rule that leaves one folder out of a backup's next runs, added to the
/// backup's own rules (the custom block of its Filters text) and taken out
/// again by Undo. The rule is written as the filter editor writes a folder:
/// from the top of the drive, <c>?:/Users/me/Projects/app/bin/</c>, with
/// glob characters escaped and <c>$</c> doubled
/// (<see cref="RecommendedFilters.FolderLine"/>). Snapshots already made are
/// not touched.
/// </summary>
public static class FolderExclusion
{
    /// <summary>The rule for the folder at <paramref name="fullPath"/>, or null
    /// for a drive or share root, or when the rule would not leave the folder
    /// out as restic reads it (a network path on Windows, whose <c>//</c>
    /// rule matches nothing there).</summary>
    public static string? Rule(string fullPath)
        => RecommendedFilters.FolderLine(fullPath) is { } rule && ExcludedBy(rule, fullPath) is not null ? rule : null;

    /// <summary>The rule that already leaves <paramref name="fullPath"/> out of
    /// the next backup with these filters (as written), or null when it is
    /// backed up.</summary>
    public static string? ExcludedBy(string? filters, string fullPath)
        => FilterTranslator.Evaluate(FilterTranslator.CompileRules(filters), FilterTranslator.ResticPath(fullPath)) is { Excluded: true, Rule: { } rule } ? rule : null;

    /// <summary>When these filters still back up <paramref name="fullPath"/>,
    /// the last exception (<c>!</c> rule) that keeps it in, as written; null
    /// when the folder is excluded or no exception matches it.</summary>
    public static string? KeptBy(string? filters, string fullPath)
    {
        var rules = FilterTranslator.CompileRules(filters);
        var path = FilterTranslator.ResticPath(fullPath);
        if (FilterTranslator.Evaluate(rules, path).Excluded) return null;
        string? keeper = null;
        foreach (var rule in rules.Where(r => r.Negated))
            if (FilterTranslator.Evaluate(new[] { rule with { Negated = false } }, path).Excluded) keeper = rule.Glob;
        return keeper;
    }

    /// <summary>
    /// <paramref name="filters"/> with <paramref name="rule"/> as the last line
    /// of its custom block, everything else unchanged. Without a complete
    /// custom block, one holding just the rule is put at the top, where the
    /// filter editor keeps it. Unchanged when the rule is already a line.
    /// </summary>
    public static string Add(string? filters, string rule)
    {
        var text = filters ?? "";
        // Lines keep their own endings ("\r" stays on the line), so every
        // other byte of the text is unchanged.
        var lines = text.Split('\n').ToList();
        if (lines.Any(l => Same(l, rule))) return text;
        var (start, end) = CustomBlock(lines);
        if (start >= 0)
        {
            lines.Insert(end, rule + (lines[end - 1].EndsWith('\r') ? "\r" : ""));
            return string.Join('\n', lines);
        }
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var block = string.Join(newline, RecommendedFilters.CustomBlockHeader, rule, RecommendedFilters.CustomBlockFooter);
        return text.Trim().Length == 0 ? block + newline : block + newline + newline + text;
    }

    /// <summary><paramref name="filters"/> without <paramref name="rule"/>: its
    /// last line in the custom block, or failing that its last line anywhere.
    /// Every other line is kept.</summary>
    public static string Remove(string? filters, string rule)
    {
        var text = filters ?? "";
        var lines = text.Split('\n').ToList();
        var (start, end) = CustomBlock(lines);
        var index = -1;
        if (start >= 0)
            for (var i = end - 1; i > start && index < 0; i--) if (Same(lines[i], rule)) index = i;
        if (index < 0) index = lines.FindLastIndex(l => Same(l, rule));
        if (index < 0) return text;
        lines.RemoveAt(index);
        return string.Join('\n', lines);
    }

    // The first complete custom block: its header and footer lines.
    private static (int Start, int End) CustomBlock(IReadOnlyList<string> lines)
    {
        var start = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line == RecommendedFilters.CustomBlockHeader) start = i;
            else if (line == RecommendedFilters.CustomBlockFooter && start >= 0) return (start, i);
        }
        return (-1, -1);
    }

    private static bool Same(string line, string rule)
        => string.Equals(line.Trim(), rule, FilterTranslator.IgnoresCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>The files in the folder now and their bytes, or null when the
    /// folder is missing or counting takes longer than <paramref name="limit"/>.
    /// Linked folders are not followed.</summary>
    public static (long Files, long Bytes)? Measure(string fullPath, TimeSpan limit, CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(fullPath)) return null;
            var clock = Stopwatch.StartNew();
            long files = 0, bytes = 0;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
            var entries = new FileSystemEnumerable<long>(fullPath, (ref FileSystemEntry e) => e.Length, options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
                ShouldRecursePredicate = (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0,
            };
            foreach (var length in entries)
            {
                files++;
                bytes += length;
                if ((files & 255) == 0 && (clock.Elapsed > limit || ct.IsCancellationRequested)) return null;
            }
            return clock.Elapsed > limit ? null : (files, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
