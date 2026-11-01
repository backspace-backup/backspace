using System;
using System.Collections.Generic;
using System.Linq;

namespace Backspace.Services;

/// <summary>
/// Collapses a per-leaf selection into the minimal cover of include
/// patterns. When the user picks a folder in the restore picker, the UI
/// flips <c>IsSelected = true</c> on every descendant leaf in the flat
/// <c>AllFiles</c> projection — sending each leaf to restic as a
/// separate <c>--include</c> flag produces three problems:
///
///   1. <b>Surprise.</b> The engine log says "restoring 2397 files" when
///      the user picked 2 folders. Visible in transcripts and progress
///      counters.
///   2. <b>Crash.</b> Each <c>--include</c> flag is ~40 bytes; ~800
///      patterns crosses Windows' ~32 KB <c>CreateProcess</c> ceiling
///      and restic fails to launch with "The filename or extension is
///      too long". <c>--include-file</c> sidesteps that ceiling but not
///      problem 1.
///   3. <b>Cost.</b> Restic processes one pattern per match attempt;
///      a longer list is linearly slower to walk the snapshot tree.
///
/// This service computes the minimal pattern set: for every directory in
/// the snapshot's file universe, if every leaf AND every subdirectory
/// inside it is in the selected set, the directory itself is emitted as
/// a single pattern (restic's <c>--include /dir</c> matches the dir and
/// everything under it via the absolute-pattern prefix-match path in
/// <c>filter.match</c>). Selected leaves with partially-selected parents
/// stay as per-file patterns.
/// </summary>
public static class RestoreSelectionMinimizer
{
    /// <summary>
    /// Compute the minimal set of include patterns covering
    /// <paramref name="selectedPaths"/> given the full snapshot file
    /// universe in <paramref name="allPaths"/>. Both inputs are
    /// snapshot-relative absolute paths (leading "/"); the output
    /// preserves that shape.
    ///
    /// Determinism: the output is sorted, so a stable selection
    /// produces stable engine logs and test assertions.
    /// </summary>
    public static IReadOnlyList<string> Compute(
        IEnumerable<string> allPaths,
        IEnumerable<string> selectedPaths)
    {
        var allSet = new HashSet<string>(allPaths.Select(Normalize), StringComparer.Ordinal);
        var selectedSet = new HashSet<string>(selectedPaths.Select(Normalize), StringComparer.Ordinal);

        if (selectedSet.Count == 0) return Array.Empty<string>();

        // childLeavesByDir: dir path → list of leaf paths that sit
        // DIRECTLY in this dir (not in subdirs).
        // childDirsByDir:   dir path → set of subdir paths sitting
        // DIRECTLY in this dir.
        var childLeavesByDir = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var childDirsByDir = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var leaf in allSet)
        {
            var parent = Parent(leaf);
            if (!childLeavesByDir.TryGetValue(parent, out var leaves))
            {
                leaves = new List<string>();
                childLeavesByDir[parent] = leaves;
            }
            leaves.Add(leaf);

            // Register each ancestor dir as a child of ITS parent so
            // bottom-up coverage propagation knows about every node.
            var cur = parent;
            while (cur.Length > 0)
            {
                var grand = Parent(cur);
                if (!childDirsByDir.TryGetValue(grand, out var dirs))
                {
                    dirs = new HashSet<string>(StringComparer.Ordinal);
                    childDirsByDir[grand] = dirs;
                }
                dirs.Add(cur);
                if (grand.Length == 0) break;
                cur = grand;
            }
        }

        // Bottom-up coverage: deepest dirs first so a parent's
        // computation can read its children's already-set verdicts.
        var allDirs = childLeavesByDir.Keys
            .Concat(childDirsByDir.Keys)
            .Distinct()
            .OrderByDescending(d => DepthOf(d))
            .ToList();

        var dirFullyCovered = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var d in allDirs)
        {
            bool leavesOk = !childLeavesByDir.TryGetValue(d, out var leaves)
                || leaves.All(selectedSet.Contains);
            bool subdirsOk = !childDirsByDir.TryGetValue(d, out var subs)
                || subs.All(s => dirFullyCovered.GetValueOrDefault(s, false));
            dirFullyCovered[d] = leavesOk && subdirsOk;
        }

        // Top-down emit: walk from the implicit root ("") and emit
        // either the dir path (if fully covered) or recurse into its
        // children for partial coverage.
        var result = new List<string>();
        Emit("", result, dirFullyCovered, childLeavesByDir, childDirsByDir, selectedSet);
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static void Emit(
        string dir,
        List<string> result,
        Dictionary<string, bool> dirFullyCovered,
        Dictionary<string, List<string>> childLeavesByDir,
        Dictionary<string, HashSet<string>> childDirsByDir,
        HashSet<string> selectedSet)
    {
        // Root "" has no path to emit even if everything below is
        // selected — caller is expected to handle "restore the whole
        // snapshot" via the request shape, not via an empty --include.
        if (dir.Length > 0 && dirFullyCovered.GetValueOrDefault(dir, false))
        {
            result.Add(dir);
            return;
        }

        if (childLeavesByDir.TryGetValue(dir, out var leaves))
        {
            foreach (var leaf in leaves)
            {
                if (selectedSet.Contains(leaf)) result.Add(leaf);
            }
        }

        if (childDirsByDir.TryGetValue(dir, out var subs))
        {
            foreach (var sub in subs) Emit(sub, result, dirFullyCovered, childLeavesByDir, childDirsByDir, selectedSet);
        }
    }

    /// <summary>
    /// Normalise to forward-slash, strip trailing slashes, ensure
    /// leading slash. Restic's snapshot paths use forward slashes; the
    /// VM may hand us either separator depending on platform origin.
    /// </summary>
    private static string Normalize(string path)
    {
        var p = path.Replace('\\', '/');
        if (p.Length > 1 && p.EndsWith('/')) p = p.TrimEnd('/');
        if (!p.StartsWith('/')) p = "/" + p;
        return p;
    }

    private static string Parent(string path)
    {
        var idx = path.LastIndexOf('/');
        if (idx <= 0) return "";
        return path[..idx];
    }

    private static int DepthOf(string path)
    {
        int depth = 0;
        for (int i = 0; i < path.Length; i++) if (path[i] == '/') depth++;
        return depth;
    }
}
