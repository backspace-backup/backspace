using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U133 (4 November 2026): restic gets a cheaper rule list that excludes
/// exactly the same paths. The optimizer's proofs are checked here against a
/// line-for-line copy of restic's own matching (internal/filter/filter.go,
/// <see cref="ResticFilterReference"/>) on random rule lists and paths.
/// </summary>
public sealed class ResticRuleOptimizerTests(ITestOutputHelper output)
{
    private static bool Rejects(IReadOnlyList<string> rules, string path, bool insensitive) =>
        ResticFilterReference.List(insensitive ? rules.Select(r => r.ToLowerInvariant()).ToList() : rules,
            insensitive ? path.ToLowerInvariant() : path);

    [Fact]
    public void A_leading_source_and_double_star_is_dropped_only_when_nothing_changes()
    {
        var result = ResticRuleOptimizer.Compute(["C:/**/*.tmp", "C:/**/*/**/node_modules/*", "C:/Users/*/AppData/*/Cache/*"], true, @"C:\");
        // "C:/**/*.tmp" excludes a .tmp file at any depth below C:, as "*.tmp"
        // does for paths on C:. Restic's "**" limits make
        // "C:/**/*/**/node_modules/*" miss C:\a\node_modules\x but not
        // C:\a\node_modules\x\y; the two rules that replace it do the same with
        // one "**" fewer. No "**" after the source: unchanged.
        Assert.Equal(["*.tmp", "*/*/*/**/node_modules/*", "*/*/node_modules/*/*", "C:/Users/*/AppData/*/Cache/*"], result);
        Assert.False(Rejects(["C:/**/*/**/node_modules/*"], @"C:\a\node_modules\x", true));
        Assert.False(Rejects(result, @"C:\a\node_modules\x", true));
        Assert.True(Rejects(["*/**/node_modules/*"], @"C:\a\node_modules\x", true));
        Assert.True(Rejects(["C:/**/*/**/node_modules/*"], @"C:\a\node_modules\x\y", true));
        Assert.True(Rejects(result, @"C:\a\node_modules\x\y", true));
    }

    [Fact]
    public void A_rule_another_covers_is_left_out_unless_a_not_rule_between_them_could_bring_it_back()
    {
        var covered = ResticRuleOptimizer.Compute(["C:/**/*/**/node_modules/*", "C:/**/node_modules/"], true, @"C:\");
        Assert.Equal(["node_modules/"], covered);
        Assert.Equal(["*.tmp"], ResticRuleOptimizer.Compute(["C:/**/x.tmp", "C:/**/*.tmp", "C:/**/*.tmp"], true, @"C:\"));

        // "!" between the two: the later one restores some of what the first excluded.
        var list = new[] { "C:/**/node_modules/", "!C:/keep/**", "C:/**/*/**/node_modules/*" };
        var kept = ResticRuleOptimizer.Compute(list, true, @"C:\");
        Assert.Equal(["node_modules/", "!C:/keep/**", "*/*/*/**/node_modules/*", "*/*/node_modules/*/*"], kept);
        foreach (var path in new[] { @"C:\keep\a\node_modules\x", @"C:\b\c\node_modules\x", @"C:\keep\node_modules" })
            Assert.Equal(Rejects(list, path, true), Rejects(kept, path, true));
    }

    [Fact]
    public void A_folder_below_the_drive_keeps_rules_that_would_match_inside_the_source_path()
    {
        // For C:\Users\erwin, "C:/Users/erwin/**/Users/*" must not become
        // "Users/*": that would exclude C:\Users\erwin itself.
        var result = ResticRuleOptimizer.Compute(["C:/Users/erwin/**/Users/*", "C:/Users/erwin/**/*.tmp"], true, @"C:\Users\erwin");
        Assert.Equal("C:/Users/erwin/**/Users/*", result[0]);
        Assert.Equal("*.tmp", result[1]);
        Assert.True(Rejects(["Users/*"], @"C:\Users\erwin", true));
        Assert.False(Rejects(["C:/Users/erwin/**/Users/*"], @"C:\Users\erwin", true));
    }

    /// <summary>Random rule lists from the shapes Backspace writes, random
    /// paths below the source made of the rules' own names: the optimized list
    /// must give restic's answer for every path.</summary>
    [Theory]
    [InlineData(1, @"C:\", true)]
    [InlineData(2, @"C:\", false)]
    [InlineData(3, @"C:\Users\erwin", true)]
    [InlineData(4, @"D:\", true)]
    public void Random_rule_lists_exclude_exactly_the_same_paths(int seed, string source, bool insensitive)
    {
        var random = new Random(seed);
        var root = source.TrimEnd('\\').Replace('\\', '/');
        string[] names = ["a", "b", "Cache", "cache", "node_modules", "x.tmp", "Users", "erwin", "c:", "AppData", "keep"];
        string[] globs = ["*", "*.tmp", "*cache", "*Cache*", "?", "a*", "**"];
        string Part() => random.Next(3) == 0 ? globs[random.Next(globs.Length)] : names[random.Next(names.Length)];
        string RulePattern()
        {
            var parts = Enumerable.Range(0, 1 + random.Next(4)).Select(_ => Part()).ToList();
            var body = string.Join('/', parts) + (random.Next(3) == 0 ? "/" : "");
            return random.Next(6) switch
            {
                0 => body,
                1 => root + "/" + body,
                2 => root + "/**/*/**/" + string.Join('/', parts.Where(p => p != "**").DefaultIfEmpty("x")) + (random.Next(2) == 0 ? "/*" : ""),
                _ => root + "/**/" + body,
            };
        }
        string RandomPath()
        {
            var depth = random.Next(0, 9);
            var parts = Enumerable.Range(0, depth).Select(_ => random.Next(5) == 0 ? "other" : names[random.Next(names.Length)].Replace(":", ""));
            return source.TrimEnd('\\') + (depth == 0 ? (source.EndsWith('\\') ? "\\" : "") : "\\" + string.Join('\\', parts));
        }
        var changedLists = 0;
        var removed = 0;
        // For the same check with restic's own Go code (U133), when the scratch folder exists.
        var dump = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "audit-dashboard", "private", "temp", "claude-20261104", "scan-test");
        var lists = new List<string>();
        for (var round = 0; round < 400; round++)
        {
            var rules = Enumerable.Range(0, 3 + random.Next(8)).Select(_ => (random.Next(6) == 0 ? "!" : "") + RulePattern()).ToList();
            var optimized = ResticRuleOptimizer.Compute(rules, insensitive, source);
            if (!optimized.SequenceEqual(rules)) changedLists++;
            removed += rules.Count - optimized.Length;
            lists.Add($"=== {source} {(insensitive ? "i" : "s")}\n{string.Join("\n", rules)}\n---\n{string.Join("\n", optimized)}");
            for (var p = 0; p < 600; p++)
            {
                var path = RandomPath();
                var expected = Rejects(rules, path, insensitive);
                var actual = Rejects(optimized, path, insensitive);
                if (expected != actual)
                    Assert.Fail($"{path}: restic {(expected ? "excludes" : "keeps")} it with\n  {string.Join("\n  ", rules)}\nbut not with\n  {string.Join("\n  ", optimized)}");
            }
        }
        output.WriteLine($"{changedLists} of 400 lists changed, {removed} rules left out");
        if (Directory.Exists(dump)) File.WriteAllText(Path.Combine(dump, $"fuzz-{seed}.txt"), string.Join("\n", lists) + "\n");
        Assert.True(changedLists > 50);
    }

    /// <summary>Writes local_c's list as restic would get it, for the check
    /// with restic's own code on every path of C:\ (U133).</summary>
    [Fact]
    public void Local_c_rules()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "audit-dashboard", "private", "temp", "claude-20261104", "scan-test");
        if (!File.Exists(Path.Combine(dir, "iexcludes.txt"))) return;
        foreach (var (name, insensitive) in new[] { ("iexcludes", true), ("excludes", false) })
        {
            var rules = File.ReadAllLines(Path.Combine(dir, name + ".txt")).Select(l => l.Trim().TrimStart('\uFEFF')).Where(l => l.Length > 0).ToList();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var optimized = ResticRuleOptimizer.Compute(rules, insensitive, @"C:\");
            output.WriteLine($"{name}: {rules.Count} → {optimized.Length} in {watch.Elapsed.TotalSeconds:0.0} s");
            File.WriteAllLines(Path.Combine(dir, name + "-optimized.txt"), optimized);
        }
    }
}

/// <summary>Restic 0.19's internal/filter matching (filter.go: preparePattern,
/// splitPath, match, List), copied line for line, with Go's filepath.Match on
/// Windows. The reference the optimizer is tested against.</summary>
internal static class ResticFilterReference
{
    private sealed record Part(string Pattern, bool IsSimple);

    private sealed record Pattern(Part[] Parts, bool IsNegated);

    private static string[] SplitPath(string p)
    {
        var parts = p.Replace('\\', '/').Split('/');
        if (parts[0] == "") parts[0] = "/";
        return parts;
    }

    // filepath.Clean on Windows for the rule shapes in these tests: trailing
    // separators removed, "/" as "\".
    private static string Clean(string p)
    {
        p = p.Replace('/', '\\');
        while (p.Length > 1 && p.EndsWith('\\') && !(p.Length == 3 && p[1] == ':')) p = p[..^1];
        return p;
    }

    private static Pattern Prepare(string text)
    {
        var negate = text[0] == '!';
        if (negate) text = text[1..];
        var parts = SplitPath(Clean(text)).Select(part =>
        {
            var simple = part.IndexOfAny(['\\', '[', ']', '*', '?']) < 0;
            return new Part(part == "**" ? "" : part, simple);
        }).ToArray();
        return new Pattern(parts, negate);
    }

    private static bool Match(Pattern pattern, string[] strs)
    {
        var pos = Array.FindIndex(pattern.Parts, p => p.Pattern == "");
        if (pos >= 0)
        {
            for (var i = 0; i <= strs.Length - pattern.Parts.Length + 1; i++)
            {
                var expanded = pattern.Parts.Take(pos).Concat(Enumerable.Repeat(new Part("*", false), i)).Concat(pattern.Parts.Skip(pos + 1)).ToArray();
                if (Match(pattern with { Parts = expanded }, strs)) return true;
            }
            return false;
        }
        if (pattern.Parts.Length == 0) return false;
        if (pattern.Parts.Length <= strs.Length)
        {
            var minOffset = 0;
            var maxOffset = strs.Length - pattern.Parts.Length;
            if (pattern.Parts[0].Pattern == "/") maxOffset = 0;
            else if (strs[0] == "/") minOffset = 1;
            for (var offset = maxOffset; offset >= minOffset; offset--)
            {
                var ok = true;
                for (var i = pattern.Parts.Length - 1; i >= 0 && ok; i--)
                {
                    var part = pattern.Parts[i];
                    ok = part.IsSimple ? part.Pattern == strs[offset + i] : ResticRuleOptimizer.Check.GlobMatch(part.Pattern, strs[offset + i]);
                }
                if (ok) return true;
            }
        }
        return false;
    }

    internal static bool List(IReadOnlyList<string> patterns, string path)
    {
        var parsed = patterns.Where(p => p.Length > 0).Select(Prepare).ToList();
        var strs = SplitPath(path);
        var matched = false;
        foreach (var pat in parsed)
        {
            var m = Match(pat, strs);
            matched = pat.IsNegated ? matched && !m : matched || m;
        }
        return matched;
    }
}
