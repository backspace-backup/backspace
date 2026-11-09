using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Rule = Backspace.Services.FilterTranslator.CompiledRule;
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
/// The rules are read as restic reads the exclude file, and matched on full
/// paths with forward slashes (<see cref="FilterTranslator"/>).
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

    /// <summary>
    /// What each change to <paramref name="filtersText"/> would change in a
    /// backup of <paramref name="sourcePaths"/>, in one walk (U165): for a rule
    /// added, the files it leaves out that today's rules keep; for a rule
    /// removed, the files backed up again. Each change is measured on its own
    /// against today's rules. Folders every variant leaves out are not read.
    /// </summary>
    public async Task<SuggestionEffects> MeasureChangesAsync(IReadOnlyList<string> sourcePaths, string filtersText,
        IReadOnlyList<(string Rule, bool Add)> changes, CancellationToken ct)
    {
        var baseRules = ParseRules(filtersText);
        var lines = filtersText.Replace("\r\n", "\n").Split('\n');
        // An added rule is written last, so where it matches it decides: the
        // variant only needs the rule itself. A removed rule needs the rest.
        var variants = changes.Select(c => c.Add
            ? FilterTranslator.CompileRules(c.Rule)
            : FilterTranslator.CompileRules(string.Join("\n", lines.Where(l => !string.Equals(l.Trim(), c.Rule, StringComparison.OrdinalIgnoreCase)))))
            .ToList();
        var files = new long[changes.Count];
        var bytes = new long[changes.Count];
        var truncated = false;
        foreach (var src in sourcePaths)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(src) || !Directory.Exists(src)) continue;
            var root = TreeWalk.Root(src);
            // Per folder: excluded by today's rules, then by each variant.
            var states = new System.Collections.Concurrent.ConcurrentDictionary<string, bool[]>(StringComparer.OrdinalIgnoreCase);
            var none = new bool[changes.Count + 1];
            var finished = await Task.Run(() => TreeWalk.Walk(root, (entry, _) =>
            {
                var parent = states.TryGetValue(Path.GetDirectoryName(entry.FullPath) ?? "", out var p) ? p : none;
                var path = FilterTranslator.ResticPath(entry.FullPath) + (entry.IsDirectory ? "/" : "");
                var own = FilterTranslator.Evaluate(baseRules, path).Excluded;
                var state = new bool[changes.Count + 1];
                state[0] = parent[0] || own;
                for (var i = 0; i < changes.Count; i++)
                    state[i + 1] = parent[i + 1] || (changes[i].Add
                        ? own || FilterTranslator.Evaluate(variants[i], path).Excluded
                        : FilterTranslator.Evaluate(variants[i], path).Excluded);
                if (entry.IsDirectory)
                {
                    if (state.All(s => s)) return null;
                    states[entry.FullPath] = state;
                    return false;
                }
                for (var i = 0; i < changes.Count; i++)
                    if (state[i + 1] != state[0])
                    {
                        Interlocked.Increment(ref files[i]);
                        Interlocked.Add(ref bytes[i], entry.Length);
                    }
                return null;
            }, DateTime.UtcNow + MaxScanDuration, ct), ct);
            if (!finished) truncated = true;
        }
        return new SuggestionEffects(changes.Select((_, i) => new SuggestionEffect(files[i], bytes[i])).ToList(), truncated);
    }

    // -----------------------------------------------------------------
    // Per-source walker
    // -----------------------------------------------------------------

    private (long Excluded, long Included, bool Truncated) WalkOne(
        string sourceRoot, IReadOnlyList<Rule> rules, CancellationToken ct)
    {
        long excluded = 0;
        long included = 0;
        var rootFull = TreeWalk.Root(sourceRoot);

        // The mark is true inside an excluded folder: everything there
        // counts as saved without applying per-file rules (the user wanted
        // an accurate savings number, even on large excluded trees).
        var finished = TreeWalk.Walk(rootFull, (entry, insideExcluded) =>
        {
            if (insideExcluded)
            {
                if (!entry.IsDirectory) Interlocked.Add(ref excluded, entry.Length);
                return true;
            }
            var path = FilterTranslator.ResticPath(entry.FullPath);
            if (entry.IsDirectory) return IsExcluded(rules, path + "/");
            if (IsExcluded(rules, path)) Interlocked.Add(ref excluded, entry.Length);
            else Interlocked.Add(ref included, entry.Length);
            return null;
        }, DateTime.UtcNow + MaxScanDuration, ct);

        return (excluded, included, !finished);
    }

    /// <summary>
    /// Any matching exclude rule excludes the path. Restic has no
    /// exclude-list include semantics, so a single match is enough —
    /// nothing later can re-include the file. Default for a path that
    /// no rule touches: INCLUDED.
    /// </summary>
    internal static bool IsExcluded(IReadOnlyList<Rule> rules, string path)
        => FilterTranslator.Evaluate(rules, path).Excluded;

    internal static List<Rule> ParseRules(string? text) => FilterTranslator.CompileRules(text);
}

/// <param name="ExcludedBytes">Total file bytes the rules would skip.</param>
/// <param name="IncludedBytes">Total file bytes the rules would keep.</param>
/// <param name="Truncated">True if any source's walk hit the time cap.</param>
/// <param name="SourcesScanned">Number of source paths actually walked.</param>
/// <summary>What one suggested change does to a backup: files and bytes it
/// leaves out (added rule) or backs up again (removed rule).</summary>
public readonly record struct SuggestionEffect(long Files, long Bytes);

/// <param name="Changes">One per change, in order.</param>
/// <param name="Truncated">True if a walk hit the time cap: the counts are lower bounds.</param>
public sealed record SuggestionEffects(IReadOnlyList<SuggestionEffect> Changes, bool Truncated);

public readonly record struct SavingsEstimate(
    long ExcludedBytes,
    long IncludedBytes,
    bool Truncated,
    int SourcesScanned);
