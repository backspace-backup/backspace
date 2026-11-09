using System;
using System.Collections.Generic;
using System.IO;
using Rule = Backspace.Services.FilterTranslator.CompiledRule;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Serilog;

namespace Backspace.Services;

/// <summary>
/// Shows what restic's filter file would actually include / exclude.
///
/// Two modes:
///  • SimulateAsync — walks the backup's real source trees on disk and
///    evaluates every path against the translated rules. No engine, no
///    repo, no destination required: a filter preview must work before
///    the first backup ever runs, and must not depend on the
///    destination being reachable. (The previous implementation ran
///    <c>restic backup --dry-run</c> through Backrest's RunCommand RPC;
///    that broke on Windows because Backrest splits the command string
///    with POSIX shlex rules — <c>C:\ntws</c> arrived as <c>C:ntws</c> —
///    and restic's dry-run output doesn't name the matching rule
///    anyway, which is the whole point of the pane.)
///  • Preview       — runs the same matcher against a caller-supplied
///    list of candidate paths without touching the disk. Instant;
///    useful while typing.
///
/// Both modes read the Filters text as restic reads the exclude file the
/// engine hands it (<see cref="FilterTranslator"/>) and match full paths
/// with forward slashes (<c>C:/Users/me/x</c>), as restic does. The
/// "matching rule" string surfaced for each excluded path is the line as
/// written.
/// </summary>
public sealed class FilterSimulator
{
    private static ILogger _log => AppLogger.For<FilterSimulator>();

    /// <summary>Per-source cap on how long the walk runs before giving
    /// up with a partial listing. Mirrors <see cref="SavingsEstimator.MaxScanDuration"/>.</summary>
    public TimeSpan MaxScanDuration { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Hard cap on emitted entries across all sources so a huge
    /// tree can't flood the results pane (and the UI string behind it).</summary>
    public int MaxEntries { get; init; } = 5000;

    /// <summary>
    /// Walks every source path of the backup, evaluates each file and
    /// directory against the backup's translated filter rules, and
    /// feeds results through the callback. Files are emitted
    /// individually; an excluded directory is emitted once (with a
    /// trailing separator) and its subtree is not entered — matching
    /// restic, which never descends into an excluded directory.
    /// The callback runs on a worker thread.
    /// </summary>
    public async Task<SimulateResult> SimulateAsync(Backup backup, Action<SimulatedEntry> onEntry, CancellationToken ct)
    {
        var rules = CompileRules(backup.FiltersText);

        _log.Information("Filter simulation start: backup={Backup} sources={Count} rules={Rules}",
            backup.Name, backup.SourcePaths.Count, rules.Count);

        var emitted = 0;
        var truncated = false;
        long cloudOnly = 0;

        foreach (var sourcePath in backup.SourcePaths)
        {
            if (ct.IsCancellationRequested || emitted >= MaxEntries) break;
            if (string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(sourcePath)) continue;

            var (count, hitCap, cloud) = await Task.Run(
                () => WalkOne(sourcePath, rules, backup.ExcludeCloudFiles, onEntry, MaxEntries - emitted, ct), ct);
            emitted += count;
            cloudOnly += cloud;
            if (hitCap) truncated = true;
        }

        _log.Information("Filter simulation finished for {Backup}: entries={Entries} truncated={Truncated} cloudOnly={CloudOnly}",
            backup.Name, emitted, truncated, cloudOnly);
        return new SimulateResult(emitted, truncated, cloudOnly);
    }

    private (int Emitted, bool Truncated, long CloudOnly) WalkOne(
        string sourceRoot, IReadOnlyList<Rule> rules, bool skipCloudOnly, Action<SimulatedEntry> onEntry,
        int budget, CancellationToken ct)
    {
        var emitted = 0;
        var truncated = false;
        long cloudOnly = 0;
        var deadline = DateTime.UtcNow + MaxScanDuration;

        var rootFull = TreeWalk.Root(sourceRoot);
        var stack = new Stack<string>();
        stack.Push(rootFull);

        bool Emit(SimulatedEntry entry)
        {
            if (emitted >= budget) { truncated = true; return false; }
            onEntry(entry);
            emitted++;
            return true;
        }

        while (stack.Count > 0 && !truncated)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline) { truncated = true; break; }

            var entries = TreeWalk.List(stack.Pop());
            foreach (var f in entries)
            {
                if (truncated) break;
                if (f.IsDirectory) continue;
                // Online-only files (placeholders are links): counted, not
                // listed one by one; the backup skips them quietly (U168).
                var cloud = TreeWalk.IsCloudOnly(f.Attributes);
                if (f.IsLink && !cloud) continue;

                var (included, rule) = Evaluate(rules, FilterTranslator.ResticPath(f.FullPath));
                if (cloud && included)
                {
                    cloudOnly++;
                    if (skipCloudOnly || f.IsLink) continue;
                }
                if (f.IsLink) continue;
                Emit(new SimulatedEntry(f.FullPath, included, rule));
            }

            foreach (var d in entries)
            {
                if (truncated) break;
                if (!d.IsDirectory || d.IsLink) continue;
                var sub = d.FullPath;

                var (included, rule) = Evaluate(rules, FilterTranslator.ResticPath(sub) + "/");
                if (included)
                {
                    stack.Push(sub);            // descend silently — files report themselves
                }
                else
                {
                    // One line for the whole skipped subtree, like restic.
                    Emit(new SimulatedEntry(sub + Path.DirectorySeparatorChar, Included: false, rule));
                }
            }
        }

        return (emitted, truncated, cloudOnly);
    }

    /// <summary>
    /// Synchronous preview: runs the supplied filter text against
    /// <paramref name="candidatePaths"/>. The rule string returned in
    /// <see cref="SimulatedEntry.MatchingRule"/> is the rule as written.
    /// Each candidate is a full path, as restic sees it (<c>C:/Users/me/x</c>).
    /// </summary>
    public IReadOnlyList<SimulatedEntry> Preview(string filtersText, IEnumerable<string> candidatePaths)
    {
        var rules = CompileRules(filtersText);
        var results = new List<SimulatedEntry>();
        foreach (var p in candidatePaths)
        {
            var (kept, rule) = Evaluate(rules, p);
            results.Add(new SimulatedEntry(p, kept, rule));
        }
        return results;
    }

    private static List<Rule> CompileRules(string filtersText) => FilterTranslator.CompileRules(filtersText);

    private static (bool included, string? rule) Evaluate(IReadOnlyList<Rule> rules, string path)
    {
        var result = FilterTranslator.Evaluate(rules, path);
        return (!result.Excluded, result.Rule);
    }
    /// <summary>
    /// Thin pass-through to <see cref="FilterTranslator.GlobToRegex"/>
    /// — kept so the existing FilterSimulatorGlobTests don't need to
    /// switch namespace. The single canonical implementation lives on
    /// FilterTranslator.
    /// </summary>
    internal static string GlobToRegex(string glob) => FilterTranslator.GlobToRegex(glob);
}

public readonly record struct SimulatedEntry(string Path, bool Included, string? MatchingRule);

/// <param name="Entries">How many entries were fed to the callback.</param>
/// <param name="Truncated">True if the walk hit <see cref="FilterSimulator.MaxEntries"/>
/// or <see cref="FilterSimulator.MaxScanDuration"/> before finishing.</param>
/// <param name="CloudOnly">Online-only files the rules keep, not listed (U168).</param>
public readonly record struct SimulateResult(int Entries, bool Truncated, long CloudOnly = 0);
