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
/// Pattern syntax supported (mirrors <see cref="FilterSimulator"/>):
///   - Lines starting with <c>#</c> or whitespace-only: ignored.
///   - Restic glob exclusions, including character classes and ordered
///     <c>!</c> exceptions within each case-sensitivity list.
///   - Supported imported <c>e:&lt;regex&gt;</c> shapes are translated
///     by FilterTranslator. Imported include hints are not executed.
///   - Paths are relative to each source root, with forward slashes.
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
            var rel = TreeWalk.Relative(rootFull, entry.FullPath);
            if (entry.IsDirectory) return IsExcluded(rules, rel + "/");
            if (IsExcluded(rules, rel)) Interlocked.Add(ref excluded, entry.Length);
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
    internal static bool IsExcluded(IReadOnlyList<Rule> rules, string relativePath)
        => FilterTranslator.Evaluate(rules, relativePath).Excluded;

    internal static List<Rule> ParseRules(string? text) => FilterTranslator.CompileRules(text);
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
