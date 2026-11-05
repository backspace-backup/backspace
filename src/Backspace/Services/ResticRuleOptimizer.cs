using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Backspace.Services;

/// <summary>
/// Gives restic a cheaper list of exclusion rules that excludes exactly the
/// same paths (U133, 4 November 2026). Restic checks every rule against every
/// path it walks, twice (once to count, once to back up). With local_c's 273
/// rules that was 93% of the time to walk C:\: 330 µs a path, mostly in rules
/// such as <c>C:/**/*.tmp</c>, whose <c>**</c> restic tries at every depth.
///
/// Two changes only, each made when a complete check proves it changes
/// nothing for any path restic can be given in this backup (any path under
/// the source folder, of any depth and any names):
/// <list type="bullet">
/// <item>a rule's leading <c>&lt;source&gt;/**/</c> is dropped when the
/// rule then still matches exactly the same paths;</item>
/// <item>a rule is left out when another rule already excludes everything it
/// excludes, and no "!" rule between them can bring any of it back.</item>
/// </list>
/// Rules keep their order. The check models restic's own matching
/// (restic internal/filter, 0.19) exactly, including the length limits its
/// <c>**</c> expansion applies, as a finite automaton over every kind of
/// path component the rules can tell apart, so it covers paths of any depth.
/// A rule it can't model (non-ASCII text, character classes, backslashes,
/// "." or "..") is passed on unchanged and is never used to leave another
/// out. When in doubt (a check too large to finish), nothing changes.
/// </summary>
internal static class ResticRuleOptimizer
{
    private static readonly ConcurrentDictionary<string, string[]> Cache = new();

    /// <summary>The rules to give restic for <paramref name="sourcePath"/>:
    /// <paramref name="patterns"/> in their order, some shortened or left out.
    /// <paramref name="caseInsensitive"/> for <c>--iexclude</c>.</summary>
    internal static IReadOnlyList<string> Optimize(IReadOnlyList<string> patterns, bool caseInsensitive, string sourcePath)
    {
        if (patterns.Count == 0 || !Platform.PlatformInfo.IsWindows) return patterns.ToArray();
        var key = (caseInsensitive ? "i|" : "s|") + sourcePath + "|" + string.Join("\n", patterns);
        return Cache.GetOrAdd(key, _ => Compute(patterns, caseInsensitive, sourcePath));
    }

    internal static string[] Compute(IReadOnlyList<string> patterns, bool caseInsensitive, string sourcePath)
    {
        var root = RootPrefix(sourcePath, caseInsensitive);
        var rules = patterns.Where(p => p.Length > 0).Select(p => Rule.Of(p, caseInsensitive)).ToList();

        // 1. Cheaper equivalents of "<root>/**/rest": "rest" alone, or for
        // "<root>/**/*/**/rest" (one "**" fewer each, so restic tries far fewer
        // depths) "*/…/*/**/rest" together with "*/…/rest/*". Each is used only
        // when proven to match exactly the same paths.
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (rule.Parts is not { } parts || parts[0] == "/" || root.Length == 0) continue;
            var lead = 0;
            while (lead < parts.Length && lead < root.Length && parts[lead] == root[lead]) lead++;
            if (lead == 0 || lead + 1 >= parts.Length || parts[lead] != "**") continue;
            var sign = rule.Negated ? "!" : "";
            var tail = OriginalTail(rule.Text, rule.Negated, lead + 1);
            var candidate = Rule.Of(sign + tail, caseInsensitive);
            if (candidate.Parts is { } shorter && shorter.SequenceEqual(parts[(lead + 1)..]) && Check.Equivalent(parts, shorter, root))
            {
                rules[i] = candidate;
                continue;
            }
            if (lead + 3 >= parts.Length || parts[lead + 1] != "*" || parts[lead + 2] != "**" || parts[(lead + 3)..].Contains("**")) continue;
            var rest = OriginalTail(rule.Text, rule.Negated, lead + 3).TrimEnd('/');
            string Stars(int n) => string.Concat(Enumerable.Repeat("*/", n));
            var deeper = Rule.Of(sign + Stars(lead + 2) + "**/" + rest, caseInsensitive);
            var level = Rule.Of(sign + Stars(lead + 1) + rest + "/*", caseInsensitive);
            if (deeper.Parts is null || level.Parts is null) continue;
            if (Check.CoveredBy(parts, [deeper.Parts, level.Parts], root)
                && Check.Includes(parts, deeper.Parts, root) && Check.Includes(parts, level.Parts, root))
            {
                rules[i] = deeper;
                rules.Insert(i + 1, level);
                i++;
            }
        }

        // 2. Leave out rules another rule already covers.
        for (var i = 0; i < rules.Count; i++)
        {
            var small = rules[i];
            if (small.Negated || small.Parts is null) continue;
            for (var j = 0; j < rules.Count; j++)
            {
                var big = rules[j];
                if (j == i || big.Negated || big.Parts is null || !MayCover(big.Parts, small.Parts, root)) continue;
                if (!Check.Includes(big.Parts, small.Parts, root)) continue;
                // A "!" rule after the covering one but before this one could
                // bring back what this one excludes: only if it can't match any of it.
                var between = j < i ? rules.Skip(j + 1).Take(i - j - 1).Where(r => r.Negated).ToList() : new List<Rule>();
                if (between.Any(n => n.Parts is null || !Check.Disjoint(n.Parts, small.Parts, root))) continue;
                rules.RemoveAt(i);
                i--;
                break;
            }
        }
        return rules.Select(r => r.Text).ToArray();
    }

    /// <summary>The text after the first <paramref name="count"/> components
    /// of a rule, as written (so its case is kept).</summary>
    private static string OriginalTail(string text, bool negated, int count)
    {
        var body = negated ? text[1..] : text;
        var at = 0;
        for (var n = 0; n < count; n++) at = body.IndexOf('/', at) + 1;
        return body[at..];
    }

    /// <summary>The components every path restic checks in this backup
    /// starts with: the source folder's, as restic splits it. Only a drive
    /// path is used; anything else gives no restriction (every path).</summary>
    internal static string[] RootPrefix(string sourcePath, bool lower)
    {
        string full;
        try { full = Path.GetFullPath(sourcePath); } catch (Exception) { return []; }
        var comps = full.Replace('\\', '/').Split('/');
        if (comps.Length == 0 || comps[0].Length != 2 || comps[0][1] != ':' || !char.IsAsciiLetter(comps[0][0])) return [];
        var root = new List<string>();
        foreach (var comp in comps)
        {
            if (comp.Length == 0 || !Rule.IsPlainAscii(comp) || comp.IndexOfAny(['*', '?', '[', ']']) >= 0) break;
            root.Add(lower ? comp.ToLowerInvariant() : comp);
        }
        return root.ToArray();
    }

    /// <summary>A cheap necessary condition for <paramref name="big"/> to
    /// cover <paramref name="small"/>, to skip most pairs: every plain name in
    /// <paramref name="big"/> appears in <paramref name="small"/> or the source
    /// path, and every other part matches one of <paramref name="small"/>'s.</summary>
    private static bool MayCover(string[] big, string[] small, string[] root)
    {
        foreach (var part in big)
        {
            if (part is "**" or "*" || part == "/" && small[0] == "/") continue;
            if (part == "/") return false;
            if (Check.IsLiteral(part))
            {
                if (!small.Contains(part) && !root.Contains(part)) return false;
            }
            else if (!small.Any(s => s == part || s != "**" && s != "/" && Check.IsLiteral(s) && Check.GlobMatch(part, s))
                     && !root.Any(r => Check.GlobMatch(part, r))) return false;
        }
        return true;
    }

    internal sealed record Rule(string Text, bool Negated, string[]? Parts)
    {
        /// <summary>Parses a rule as restic does (filepath.Clean, split on
        /// "/", "**" whole components), or Parts null when it can't be modelled.</summary>
        internal static Rule Of(string text, bool lower)
        {
            var negated = text.StartsWith('!');
            return new(text, negated, Parse(negated ? text[1..] : text, lower));
        }

        internal static bool IsPlainAscii(string s) => s.All(c => c >= 0x20 && c <= 0x7E);

        private static string[]? Parse(string body, bool lower)
        {
            if (body.Length == 0 || !IsPlainAscii(body) || body.IndexOfAny(['\\', '[', ']']) >= 0) return null;
            if (lower) body = body.ToLowerInvariant();
            if (body.EndsWith('/')) body = body[..^1];
            if (body.Length == 0) return null;
            var comps = body.Split('/');
            var parts = new List<string>(comps.Length);
            for (var i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (i == 0 && c.Length == 0) { parts.Add("/"); continue; }
                if (c.Length == 0 || c is "." or ".." || c.Length > 60) return null;
                if (c.Contains(':') && !(i == 0 && c.Length == 2 && char.IsAsciiLetter(c[0]))) return null;
                parts.Add(c);
            }
            if (parts.Count == 1 && (parts[0] == "/" || parts[0].EndsWith(':'))) return null;
            if (parts.Count(p => p == "**") > 6 || parts.Count > 60) return null;
            return parts.ToArray();
        }
    }

    /// <summary>
    /// Exact questions about restic's <c>match</c> (internal/filter/filter.go)
    /// for paths under a source folder. A pattern's parts p₁…p_r contain m
    /// "**". Restic expands the first "**" into i = 0…|S|−|P|+1 "*" parts and
    /// recurses, so a choice k₁…k_m matches only if, after expanding the first
    /// j of them, the pattern is no longer than the path: for every j ≥ 1,
    /// T_j = Σ_{l&gt;j}(1 − k_l) ≤ the number of path components outside the
    /// matched window. The fully expanded pattern must then match a window of
    /// the path's components: at its start when the pattern starts with "/",
    /// else anywhere except over a leading "/". Component tests are Go's
    /// filepath.Match on Windows (no escapes), or equality for plain parts.
    ///
    /// Paths are read last component first, so T can be tracked from T_m = 0;
    /// the counts involved are bounded by m, which makes it a finite automaton
    /// over component kinds: the sets of rule parts a component matches that
    /// some string actually produces. Inclusion and disjointness are then
    /// decided by exploring the product of the automata, which covers every
    /// path of every length.
    /// </summary>
    internal static class Check
    {
        private const int Budget = 400_000;

        internal static bool IsLiteral(string part) => part.IndexOfAny(['*', '?', '[', ']', '\\']) < 0;

        internal static bool Includes(string[] big, string[] small, string[] root) =>
            Search(root, [small], [big]) == false;

        internal static bool Equivalent(string[] a, string[] b, string[] root) => Includes(a, b, root) && Includes(b, a, root);

        /// <summary>Whether every path <paramref name="small"/> matches is matched by one of <paramref name="bigs"/>.</summary>
        internal static bool CoveredBy(string[] small, string[][] bigs, string[] root) => Search(root, [small], bigs) == false;

        internal static bool Disjoint(string[] a, string[] b, string[] root) => Search(root, [a, b], []) == false;

        /// <summary>Whether some path under the root matches every pattern of
        /// <paramref name="all"/> and none of <paramref name="none"/>; null when
        /// the search is too large to finish (callers treat that as "yes").
        /// The patterns that must match are followed one state at a time; those
        /// that must not, as the set of states they could be in, and a set that
        /// contains one already explored is skipped: it can only reject more
        /// (the antichain method for automata inclusion).</summary>
        private static bool? Search(string[] root, string[][] all, string[][] none)
        {
            var preds = new List<string> { "*" };
            foreach (var part in all.Concat(none).SelectMany(p => p).Concat(root))
                if (part is not ("**" or "/") && !preds.Contains(part)) preds.Add(part);
            if (preds.Count > 63) return null;
            var kinds = Kinds(preds);
            if (kinds is null) return null;
            var domain = new Domain(root, preds);
            var yes = all.Select(p => new Machine(p, preds)).ToArray();
            var no = none.Select(p => new Machine(p, preds)).ToArray();
            var symbols = kinds.Select(k => (long)k).Append(Machine.Slash).ToArray();

            var explored = new Dictionary<string, List<int[][]>>();
            var queue = new Queue<(int D, int[] Y, int[][] N)>();
            var count = 0;
            void Visit(IEnumerable<int> ds, int[][] ys, int[][] ns)
            {
                foreach (var d in ds)
                    foreach (var y in Combinations(ys, 0, new int[ys.Length]))
                    {
                        var key = d + ":" + string.Join(',', y);
                        if (!explored.TryGetValue(key, out var sets)) explored[key] = sets = new List<int[][]>();
                        if (sets.Any(old => old.Select((o, i) => IsSubset(o, ns[i])).All(x => x))) continue;
                        sets.RemoveAll(old => ns.Select((n, i) => IsSubset(n, old[i])).All(x => x));
                        sets.Add(ns);
                        if (++count > Budget) throw new BudgetExceeded();
                        queue.Enqueue((d, y, ns));
                    }
            }
            try
            {
                Visit(domain.Start(), yes.Select(m => m.Start()).ToArray(), no.Select(m => m.Start()).ToArray());
                while (queue.Count > 0)
                {
                    var (d, y, n) = queue.Dequeue();
                    if (domain.Accepts([d]) && y.Select((s, i) => yes[i].Accepts([s])).All(x => x) && !n.Select((s, i) => no[i].Accepts(s)).Any(x => x))
                        return true;
                    foreach (var symbol in symbols)
                    {
                        var nd = domain.Step([d], symbol);
                        if (nd.Length == 0) continue;
                        var ny = new int[y.Length][];
                        var dead = false;
                        for (var i = 0; i < y.Length && !dead; i++) dead = (ny[i] = yes[i].Step([y[i]], symbol)).Length == 0;
                        if (dead) continue;
                        Visit(nd, ny, n.Select((s, i) => no[i].Step(s, symbol)).ToArray());
                    }
                }
                return false;
            }
            catch (BudgetExceeded) { return null; }
        }

        private sealed class BudgetExceeded : Exception;

        private static IEnumerable<int[]> Combinations(int[][] sets, int at, int[] pick)
        {
            if (at == sets.Length) { yield return (int[])pick.Clone(); yield break; }
            foreach (var s in sets[at])
            {
                pick[at] = s;
                foreach (var c in Combinations(sets, at + 1, pick)) yield return c;
            }
        }

        /// <summary>Whether sorted <paramref name="a"/> is contained in sorted <paramref name="b"/>.</summary>
        private static bool IsSubset(int[] a, int[] b)
        {
            if (a.Length > b.Length) return false;
            var j = 0;
            foreach (var x in a)
            {
                while (j < b.Length && b[j] < x) j++;
                if (j == b.Length || b[j] != x) return false;
                j++;
            }
            return true;
        }

        /// <summary>Go's filepath.Match on Windows for a part without
        /// character classes, against a string without separators.</summary>
        internal static bool GlobMatch(string pattern, string text)
        {
            ulong set = Close(pattern, 1);
            foreach (var c in text) set = Advance(pattern, set, c);
            return (set & (1UL << pattern.Length)) != 0;
        }

        private static ulong Close(string p, ulong set)
        {
            for (var i = 0; i < p.Length; i++)
                if ((set & (1UL << i)) != 0 && p[i] == '*') set |= 1UL << (i + 1);
            return set;
        }

        private static ulong Advance(string p, ulong set, char c)
        {
            ulong next = 0;
            for (var i = 0; i < p.Length; i++)
            {
                if ((set & (1UL << i)) == 0) continue;
                if (p[i] == '*') next |= 1UL << i;
                else if (p[i] == '?' || p[i] == c) next |= 1UL << (i + 1);
            }
            return Close(p, next);
        }

        /// <summary>Every set of <paramref name="preds"/> that some component
        /// string matches exactly (a bit per predicate), found by exploring all
        /// strings: the characters the predicates name, plus one other.</summary>
        private static List<ulong>? Kinds(List<string> preds)
        {
            var chars = preds.SelectMany(p => p).Where(c => c is not ('*' or '?')).Distinct().Append('\u0001').ToArray();
            var start = preds.Select(p => Close(p, 1)).ToArray();
            var seen = new HashSet<string> { string.Join(',', start) };
            var queue = new Queue<ulong[]>();
            queue.Enqueue(start);
            var kinds = new HashSet<ulong>();
            while (queue.Count > 0)
            {
                var state = queue.Dequeue();
                ulong kind = 0;
                for (var i = 0; i < preds.Count; i++)
                    if ((state[i] & (1UL << preds[i].Length)) != 0) kind |= 1UL << i;
                kinds.Add(kind);
                foreach (var c in chars)
                {
                    var next = new ulong[preds.Count];
                    for (var i = 0; i < preds.Count; i++) next[i] = Advance(preds[i], state[i], c);
                    if (seen.Add(string.Join(',', next)))
                    {
                        if (seen.Count > Budget) return null;
                        queue.Enqueue(next);
                    }
                }
            }
            return kinds.ToList();
        }

        /// <summary>Paths under the source: its components, then anything,
        /// read last component first. State 0: below the root; i (1…n): root
        /// component i−1 comes next; n+1: the whole root was read. An empty
        /// root allows every path.</summary>
        private sealed class Domain(string[] root, List<string> preds)
        {
            private readonly int[] _bits = root.Select(r => preds.IndexOf(r)).ToArray();
            private int End => root.Length + 1;

            internal int[] Start() => root.Length == 0 ? [0] : [0, root.Length];

            internal bool Accepts(int[] set) => root.Length == 0 || set.Contains(End);

            internal int[] Step(int[] set, long symbol)
            {
                var next = new SortedSet<int>();
                foreach (var s in set)
                {
                    if (symbol == Machine.Slash)
                    {
                        // "/" can only be a path's first component (read last).
                        if (root.Length == 0 && s == 0) next.Add(End);
                        continue;
                    }
                    if (s == 0)
                    {
                        next.Add(0);
                        if (root.Length > 0) next.Add(root.Length);
                    }
                    else if (s <= root.Length && (symbol & (1L << _bits[s - 1])) != 0)
                        next.Add(s == 1 ? End : s - 1);
                }
                return next.ToArray();
            }
        }

        /// <summary>One pattern as an automaton reading a path's components
        /// last first. See <see cref="Check"/> for the semantics.</summary>
        internal sealed class Machine
        {
            internal const long Slash = -1;
            private const int Suf = 0, Win = 1, Pre = 2, Final = 3;
            private readonly string[] _parts;
            private readonly int[] _pred;
            private readonly int[] _star;
            private readonly bool _absolute;
            private readonly int _m, _kCap;

            internal Machine(string[] parts, List<string> preds)
            {
                _parts = parts;
                _pred = parts.Select(p => p is "**" or "/" ? -1 : preds.IndexOf(p)).ToArray();
                _star = new int[parts.Length];
                var j = 0;
                for (var q = 0; q < parts.Length; q++) if (parts[q] == "**") _star[q] = ++j;
                _m = j;
                _kCap = _m + 2;
                _absolute = parts[0] == "/";
            }

            // phase 2 bits | q+1 7 bits | k 4 bits | T+m+1 5 bits | Mx 4 bits | c 4 bits | nonEmpty 1 bit
            private static int Encode(int phase, int q, int k, int t, int mx, int c, bool ne, int m) =>
                phase | (q + 1) << 2 | k << 9 | (t + m + 1) << 13 | mx << 18 | c << 22 | (ne ? 1 : 0) << 26;

            private (int Phase, int Q, int K, int T, int Mx, int C, bool Ne) Decode(int s) =>
                (s & 3, ((s >> 2) & 127) - 1, (s >> 9) & 15, ((s >> 13) & 31) - _m - 1, (s >> 18) & 15, (s >> 22) & 15, ((s >> 26) & 1) == 1);

            private int State(int phase, int q, int k, int t, int mx, int c, bool ne) => Encode(phase, q, k, t, mx, c, ne, _m);

            internal int[] Start()
            {
                var set = new HashSet<int>();
                Close(set, State(Suf, -1, 0, 0, 0, 0, false));
                return Sorted(set);
            }

            private static int[] Sorted(HashSet<int> set) { var a = set.ToArray(); Array.Sort(a); return a; }

            private void Close(HashSet<int> set, int s)
            {
                if (!set.Add(s)) return;
                var (phase, q, k, t, mx, c, ne) = Decode(s);
                if (phase == Suf)
                    Close(set, State(Win, _parts.Length - 1, 0, 0, 0, c, false));
                else if (phase == Win)
                {
                    if (q < 0) { if (!_absolute) Close(set, State(Pre, -1, 0, 0, mx, c, ne)); }
                    else if (_parts[q] == "**")
                    {
                        var nt = Math.Max(t + 1 - k, -(_m + 1));
                        var nmx = _star[q] >= 2 ? Math.Max(mx, nt) : mx;
                        Close(set, State(Win, q - 1, 0, nt, nmx, c, ne));
                    }
                }
            }

            internal int[] Step(int[] states, long symbol)
            {
                var next = new HashSet<int>();
                foreach (var s in states)
                {
                    var (phase, q, k, t, mx, c, ne) = Decode(s);
                    var cc = Math.Min(c + 1, _m);
                    if (symbol == Slash)
                    {
                        if (phase == Win && q == 0 && _parts[0] == "/") Close(next, State(Final, -1, 0, 0, mx, c, true));
                        else if (phase == Pre && !_absolute) Close(next, State(Final, -1, 0, 0, mx, cc, ne));
                        continue;
                    }
                    switch (phase)
                    {
                        case Suf: Close(next, State(Suf, -1, 0, 0, 0, cc, false)); break;
                        case Pre: Close(next, State(Pre, -1, 0, 0, mx, cc, ne)); break;
                        case Win when q >= 0:
                            if (_parts[q] == "**")
                            {
                                if ((symbol & 1) != 0) Close(next, State(Win, q, Math.Min(k + 1, _kCap), t, mx, c, true));
                            }
                            else if (_parts[q] != "/" && (symbol & (1L << _pred[q])) != 0)
                                Close(next, State(Win, q - 1, 0, t, mx, c, true));
                            break;
                    }
                }
                return Sorted(next);
            }

            internal bool Accepts(int[] states)
            {
                foreach (var s in states)
                {
                    var (phase, _, _, _, mx, c, ne) = Decode(s);
                    if ((phase == Pre || phase == Final) && ne && c >= mx) return true;
                }
                return false;
            }
        }
    }
}
