using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Backspace.Services;

/// <summary>
/// Walks the configured source paths and totals the bytes the supplied
/// filter rules would EXCLUDE. Used by the BackupEditor to show
/// "you're saving ~12.4 GB by skipping these" after the user applies
/// the recommended exclusions block.
///
/// Accuracy vs. speed: we walk every file under each source and run the
/// filter rules (translated by <see cref="FilterTranslator"/> — the same
/// translator the engine uses, so the preview can't drift from reality).
/// Walks are cancellable, run off the UI thread, and hard-cap at
/// <see cref="MaxScanDuration"/> per source so a pathological tree
/// (1M+ small files) can't hang the estimate forever — when truncated,
/// the result carries a flag the UI surfaces as a "~" prefix to make
/// clear the number is a lower bound.
///
/// Pattern syntax supported (mirrors <see cref="FilterSimulator"/>):
///   - Lines starting with <c>#</c> or whitespace-only: ignored.
///   - <c>e:&lt;regex&gt;</c> excludes a path that matches.
///   - <c>i:&lt;regex&gt;</c> includes a path that matches (overrides
///     a prior <c>e:</c> if it comes later in the rule list).
///   - <c>+&lt;glob&gt;</c> / <c>-&lt;glob&gt;</c> (glob includes /
///     excludes) — translated to regex via the same logic
///     <see cref="FilterSimulator"/> uses.
///   - Regex matches against the path RELATIVE to the source root,
///     with forward-slash separators (Duplicacy convention).
/// </summary>
public sealed class SavingsEstimator
{
    private static ILogger _log => AppLogger.For<SavingsEstimator>();

    /// <summary>Per-source-root cap on how long the walk runs before
    /// giving up with a partial result. The UI badge prefixes the
    /// truncated number with "~" so users know it's a lower bound.</summary>
    public TimeSpan MaxScanDuration { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Computes the total bytes that the supplied filter text would
    /// have excluded across all sourcePaths. Returns ExcludedBytes,
    /// IncludedBytes, and a Truncated flag if the time cap kicked in
    /// for any source.
    /// </summary>
    public async Task<SavingsEstimate> ComputeAsync(
        IEnumerable<string> sourcePaths,
        string filtersText,
        CancellationToken ct)
    {
        var rules = ParseRules(filtersText);
        if (rules.Count == 0)
        {
            return new SavingsEstimate(ExcludedBytes: 0, IncludedBytes: 0, Truncated: false, SourcesScanned: 0);
        }

        long excluded = 0;
        long included = 0;
        var truncated = false;
        var scanned = 0;

        foreach (var src in sourcePaths)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(src) || !Directory.Exists(src)) continue;
            scanned++;

            var perSource = await Task.Run(() => WalkOne(src, rules, ct), ct);
            excluded += perSource.Excluded;
            included += perSource.Included;
            if (perSource.Truncated) truncated = true;
        }

        _log.Debug("Savings estimate: excluded={Excl:N0} included={Incl:N0} truncated={Trunc} sources={N}",
            excluded, included, truncated, scanned);

        return new SavingsEstimate(excluded, included, truncated, scanned);
    }

    // -----------------------------------------------------------------
    // Per-source walker
    // -----------------------------------------------------------------

    private (long Excluded, long Included, bool Truncated) WalkOne(
        string sourceRoot, IReadOnlyList<Rule> rules, CancellationToken ct)
    {
        long excluded = 0;
        long included = 0;
        var deadline = DateTime.UtcNow + MaxScanDuration;
        var truncated = false;

        var rootFull = Path.GetFullPath(sourceRoot).TrimEnd('\\', '/');
        var stack = new Stack<string>();
        stack.Push(rootFull);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline) { truncated = true; break; }

            var dir = stack.Pop();
            IEnumerable<string>? files = null;
            IEnumerable<string>? subs  = null;
            try
            {
                files = Directory.EnumerateFiles(dir);
                subs  = Directory.EnumerateDirectories(dir);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException)  { continue; }
            catch (PathTooLongException)        { continue; }
            catch (IOException)                 { continue; }

            // For directories: descend if NOT excluded. If excluded,
            // sum all bytes inside as Excluded (the user wanted an
            // accurate savings number, even on large excluded trees).
            foreach (var sub in subs)
            {
                try
                {
                    var info = new DirectoryInfo(sub);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }

                var rel = MakeRelative(rootFull, sub) + "/";
                if (IsExcluded(rules, rel))
                {
                    // Bulk-count an excluded subtree without applying
                    // per-file rules inside. Honours the deadline so a
                    // huge exclusion (e.g. ^Windows/) doesn't prevent
                    // the rest of the source from being scanned.
                    excluded += SumBytesUnder(sub, deadline, ref truncated, ct);
                }
                else
                {
                    stack.Push(sub);
                }
            }

            foreach (var f in files)
            {
                long len;
                try
                {
                    var info = new FileInfo(f);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    len = info.Length;
                }
                catch { continue; }

                var rel = MakeRelative(rootFull, f);
                if (IsExcluded(rules, rel)) excluded += len;
                else included += len;
            }
        }

        return (excluded, included, truncated);
    }

    /// <summary>
    /// Recursively sums every regular-file byte under <paramref name="root"/>.
    /// Used when a directory is filter-excluded — we still want to count
    /// the bytes-saved, but per-file rule evaluation inside is moot.
    /// </summary>
    private static long SumBytesUnder(string root, DateTime deadline, ref bool truncated, CancellationToken ct)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline) { truncated = true; break; }
            var dir = stack.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    try
                    {
                        var info = new FileInfo(f);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        total += info.Length;
                    }
                    catch { /* file gone mid-walk */ }
                }
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    try
                    {
                        var info = new DirectoryInfo(sub);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }
                    stack.Push(sub);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (DirectoryNotFoundException)  { }
            catch (PathTooLongException)        { }
            catch (IOException)                 { }
        }
        return total;
    }

    private static string MakeRelative(string root, string full)
    {
        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            full = full.Substring(root.Length).TrimStart('\\', '/');
        return full.Replace('\\', '/');
    }

    /// <summary>
    /// Any matching exclude rule excludes the path. Restic has no
    /// exclude-list include semantics, so a single match is enough —
    /// nothing later can re-include the file. Default for a path that
    /// no rule touches: INCLUDED.
    /// </summary>
    internal static bool IsExcluded(IReadOnlyList<Rule> rules, string relativePath)
    {
        for (int i = 0; i < rules.Count; i++)
            if (rules[i].Pattern.IsMatch(relativePath)) return true;
        return false;
    }

    // -----------------------------------------------------------------
    // Rule compilation. Source format and translation live in
    // FilterTranslator (the single parser shared with the engine and
    // FilterSimulator). Here we only compile the resulting bare globs
    // into matchable regexes for the local walk.
    // -----------------------------------------------------------------

    internal readonly record struct Rule(Regex Pattern);

    /// <summary>
    /// Translate the filter text and compile each exclude glob into a
    /// matchable regex. Restic has no exclude-list include analog so
    /// only the exclude buckets contribute. Case sensitivity mirrors
    /// the engine: IExcludes match case-insensitively.
    /// </summary>
    internal static List<Rule> ParseRules(string? text)
    {
        var list = new List<Rule>();
        if (string.IsNullOrEmpty(text)) return list;

        var translated = FilterTranslator.Translate(text);
        foreach (var g in translated.Excludes)
            list.Add(new Rule(Compile(g, caseInsensitive: false)));
        foreach (var g in translated.IExcludes)
            list.Add(new Rule(Compile(g, caseInsensitive: true)));
        return list;
    }

    private static Regex Compile(string glob, bool caseInsensitive)
    {
        var opts = RegexOptions.Compiled;
        if (caseInsensitive) opts |= RegexOptions.IgnoreCase;
        try { return new Regex(FilterTranslator.GlobToRegex(glob), opts); }
        catch { return new Regex("(?!)", RegexOptions.Compiled); }
    }
}

/// <param name="ExcludedBytes">Total file bytes the rules would skip.</param>
/// <param name="IncludedBytes">Total file bytes the rules would keep.</param>
/// <param name="Truncated">True if any source's walk hit the time cap.</param>
/// <param name="SourcesScanned">Number of source paths actually walked.</param>
public readonly record struct SavingsEstimate(
    long ExcludedBytes,
    long IncludedBytes,
    bool Truncated,
    int SourcesScanned);
