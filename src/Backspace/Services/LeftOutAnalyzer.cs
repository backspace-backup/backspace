using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Rule = Backspace.Services.FilterTranslator.CompiledRule;

namespace Backspace.Services;

/// <summary>
/// What a backup's rules leave out of its folders (U177, 10 November 2026):
/// the result shown on Filters › Advanced › Left out.
///
/// It walks the sources as restic does with the same exclude file
/// (<see cref="FilterTranslator.Decide"/>: full paths, last matching rule
/// wins, a folder decided before going into it): one row per topmost item
/// left out. A folder is listed once with its file count and size, never its
/// contents (restic never goes into it); a file is listed alone only when its
/// folder is kept. Each row names the rule responsible, the last that matched.
/// Links (junctions, symbolic links) are never followed: one that a rule
/// leaves out is a row with nothing counted. Online-only files are counted
/// separately. Folders that can't be read are listed, since their contents
/// are not counted. There is no cap: collapsed folders keep the list short.
///
/// With <see cref="LeftOutRequest.SavedFilters"/>, the same walk also reads
/// the saved rules: rows the edited rules newly leave out are marked, and what
/// the saved rules leave out but the edited ones keep is listed as back in.
/// </summary>
public sealed class LeftOutAnalyzer
{
    public Task<LeftOutResult> AnalyzeAsync(LeftOutRequest request, LeftOutCounters? counters, CancellationToken ct)
        => Task.Run(() => Analyze(request, counters ?? new LeftOutCounters(), ct), ct);

    /// <summary>The walk's mark for a folder: the left-out row whose contents
    /// are being counted, or the back-in row the folder belongs to.</summary>
    private sealed record Mark(LeftOutItem? Count, BackInItem? BackIn);

    private static readonly Mark Plain = new(null, null);

    /// <summary>Holds each run before it reads anything, until set: lets a
    /// test show the running state and cancel it. Null in the app.</summary>
    internal static ManualResetEventSlim? HoldForTests;

    internal static LeftOutResult Analyze(LeftOutRequest request, LeftOutCounters counters, CancellationToken ct)
    {
        var rules = FilterTranslator.CompileRules(request.Filters);
        var lines = FilterTranslator.RuleLines(request.Filters);
        var compare = request.SavedFilters is { } saved
            && Normalized(saved) != Normalized(request.Filters);
        var savedRules = compare ? FilterTranslator.CompileRules(request.SavedFilters) : null;
        var matched = new bool[rules.Count];
        var items = new ConcurrentBag<LeftOutItem>();
        var backIn = new ConcurrentBag<BackInItem>();
        var unreadable = new ConcurrentBag<string>();

        (bool Descend, Mark Mark) Visit(TreeWalk.Entry entry, Mark mark)
        {
            var folder = entry.IsDirectory && !entry.IsLink;
            var cloud = !entry.IsDirectory && TreeWalk.IsCloudOnly(entry.Attributes);
            if (folder) Interlocked.Increment(ref counters.Folders); else Interlocked.Increment(ref counters.Files);
            if (mark.Count is { } inside)
            {
                // Inside a folder left out: counted, not decided (restic never looks).
                if (!entry.IsDirectory && (!entry.IsLink || cloud))
                {
                    inside.Add(entry.Length, cloud);
                    Interlocked.Add(ref counters.LeftOutBytes, entry.Length);
                }
                return (folder, mark);
            }
            var path = FilterTranslator.ResticPath(entry.FullPath);
            var decided = FilterTranslator.Decide(rules, path, matched);
            var savedOut = savedRules is not null && mark.BackIn is null && FilterTranslator.Evaluate(savedRules, path).Excluded;
            if (decided >= 0 && !rules[decided].Negated)
            {
                var item = new LeftOutItem(entry.FullPath, folder, entry.IsLink && !cloud, decided, rules[decided].Glob,
                    decided < lines.Count ? lines[decided] : -1, compare && mark.BackIn is null && !savedOut);
                if (!entry.IsDirectory && (!entry.IsLink || cloud)) item.Add(entry.Length, cloud);
                items.Add(item);
                if (!entry.IsDirectory) Interlocked.Add(ref counters.LeftOutBytes, entry.Length);
                return (folder, new Mark(item, null));
            }
            if (entry.IsLink && !cloud) return (false, mark); // stored as a link, never followed
            if (cloud && request.SkipCloudOnly)
            {
                Interlocked.Increment(ref counters.CloudOnlySkipped);
                return (false, mark);
            }
            var into = mark.BackIn;
            if (into is null && savedOut)
            {
                into = new BackInItem(entry.FullPath, folder, SavedRule(savedRules!, path));
                backIn.Add(into);
            }
            if (!entry.IsDirectory) into?.Add(entry.Length);
            return (folder, into is null ? Plain : new Mark(null, into));
        }

        HoldForTests?.Wait(ct);
        foreach (var source in request.Sources.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            string root;
            try { root = TreeWalk.Root(source); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { unreadable.Add(source); continue; }
            if (!Directory.Exists(root)) { unreadable.Add(root); continue; }
            // Restic decides on the backed-up folder itself too.
            var (descend, mark) = Visit(new TreeWalk.Entry(root, true, 0, false), Plain);
            if (!descend) continue;
            TreeWalk.Walk(root, mark, Visit, DateTime.MaxValue, ct, (dir, m) =>
            {
                if (m.Count is { } inside) Interlocked.Increment(ref inside.UnreadableFolders);
                else unreadable.Add(dir);
            });
        }

        var sorted = items.OrderByDescending(i => i.Bytes).ThenByDescending(i => i.Files)
            .ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var byRule = sorted.GroupBy(i => i.RuleIndex)
            .Select(g => new RuleTotal(g.Key, rules[g.Key].Glob, g.First().RuleLine, g.Count(), g.Sum(i => i.Files), g.Sum(i => i.Bytes)))
            .OrderByDescending(r => r.Bytes).ThenByDescending(r => r.Files).ThenBy(r => r.RuleIndex).ToList();
        var rooted = new HashSet<string>(FilterTranslator.RulesMatchingNothing(request.Filters), StringComparer.Ordinal);
        var block = RecommendedLines(request.Filters);
        var unused = Enumerable.Range(0, rules.Count).Where(i => !matched[i])
            .Select(i => new UnusedRule(rules[i].Glob, i < lines.Count ? lines[i] : -1, rooted.Contains(rules[i].Glob),
                i < lines.Count && lines[i] > block.Start && lines[i] < block.End))
            .ToList();
        return new LeftOutResult(sorted, backIn.OrderByDescending(b => b.Bytes).ThenBy(b => b.Path, StringComparer.OrdinalIgnoreCase).ToList(),
            byRule, unused, unreadable.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            Volatile.Read(ref counters.CloudOnlySkipped), Volatile.Read(ref counters.Files), Volatile.Read(ref counters.Folders), compare);
    }

    private static string? SavedRule(IReadOnlyList<Rule> rules, string path) => FilterTranslator.Evaluate(rules, path).Rule;

    private static string Normalized(string text) => text.Replace("\r\n", "\n").TrimEnd('\n', ' ', '\t');

    /// <summary>The lines of the recommended block's markers, or (-1, -1).</summary>
    private static (int Start, int End) RecommendedLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => l.Trim() == RecommendedFilters.AutoBlockHeader);
        var end = start < 0 ? -1 : Array.FindIndex(lines, start + 1, l => l.Trim() == RecommendedFilters.AutoBlockFooter);
        return end < 0 ? (-1, -1) : (start, end);
    }
}

/// <param name="Sources">The backup's folders.</param>
/// <param name="Filters">The rules to read, as the editor holds them.</param>
/// <param name="SavedFilters">The saved rules, to compare with; null for none.</param>
/// <param name="SkipCloudOnly">The backup leaves online-only files out (U168).</param>
public sealed record LeftOutRequest(IReadOnlyList<string> Sources, string Filters, string? SavedFilters, bool SkipCloudOnly);

/// <summary>Running totals, read by the progress line while the walk goes on.</summary>
public sealed class LeftOutCounters
{
    public long Files;
    public long Folders;
    public long LeftOutBytes;
    public long CloudOnlySkipped;
}

/// <summary>One topmost item left out: a folder with everything in it, or a
/// file whose folder is kept.</summary>
/// <param name="RuleIndex">The rule responsible, among the compiled rules.</param>
/// <param name="RuleLine">Its line in the text (from 0), or -1.</param>
/// <param name="IsNew">Left out by the edited rules but not by the saved ones.</param>
public sealed class LeftOutItem(string path, bool isFolder, bool isLink, int ruleIndex, string rule, int ruleLine, bool isNew)
{
    public string Path { get; } = path;
    public bool IsFolder { get; } = isFolder;
    public bool IsLink { get; } = isLink;
    public int RuleIndex { get; } = ruleIndex;
    public string Rule { get; } = rule;
    public int RuleLine { get; } = ruleLine;
    public bool IsNew { get; } = isNew;
    private long _files, _bytes, _cloudOnly;
    internal int UnreadableFolders;
    public long Files => Volatile.Read(ref _files);
    public long Bytes => Volatile.Read(ref _bytes);
    /// <summary>Online-only files among <see cref="Files"/>: their size is not on this computer.</summary>
    public long CloudOnly => Volatile.Read(ref _cloudOnly);
    /// <summary>Folders inside that couldn't be read: the totals are a minimum.</summary>
    public int Unreadable => Volatile.Read(ref UnreadableFolders);

    internal void Add(long bytes, bool cloud)
    {
        Interlocked.Increment(ref _files);
        Interlocked.Add(ref _bytes, bytes);
        if (cloud) Interlocked.Increment(ref _cloudOnly);
    }
}

/// <summary>What the saved rules leave out but the edited ones keep.</summary>
public sealed class BackInItem(string path, bool isFolder, string? savedRule)
{
    public string Path { get; } = path;
    public bool IsFolder { get; } = isFolder;
    public string? SavedRule { get; } = savedRule;
    private long _files, _bytes;
    public long Files => Volatile.Read(ref _files);
    public long Bytes => Volatile.Read(ref _bytes);

    internal void Add(long bytes)
    {
        Interlocked.Increment(ref _files);
        Interlocked.Add(ref _bytes, bytes);
    }
}

/// <summary>One rule's share of what is left out.</summary>
public sealed record RuleTotal(int RuleIndex, string Rule, int Line, int Items, long Files, long Bytes);

/// <summary>A rule that matched nothing in the backup's folders.</summary>
/// <param name="Rooted">Starts with / on Windows: it can never match.</param>
/// <param name="Recommended">Inside the recommended block.</param>
public sealed record UnusedRule(string Rule, int Line, bool Rooted, bool Recommended);

/// <param name="Compared">The saved rules differed and were read too.</param>
public sealed record LeftOutResult(
    IReadOnlyList<LeftOutItem> Items,
    IReadOnlyList<BackInItem> BackIn,
    IReadOnlyList<RuleTotal> ByRule,
    IReadOnlyList<UnusedRule> MatchNothing,
    IReadOnlyList<string> Unreadable,
    long CloudOnlySkipped,
    long FilesScanned,
    long FoldersScanned,
    bool Compared)
{
    public long TotalFiles => Items.Sum(i => i.Files);
    public long TotalBytes => Items.Sum(i => i.Bytes);
}
