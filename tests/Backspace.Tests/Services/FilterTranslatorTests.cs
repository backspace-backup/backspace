using System.Linq;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Pins how Backspace reads a Filters text: exactly as restic reads the
/// exclude file the engine hands it (U151), on full paths with forward
/// slashes. E57 checks the same decisions against the real restic.
/// </summary>
public class FilterTranslatorTests
{
    [Theory]
    [InlineData("drop[12].txt", "C:/src/drop1.txt", false)]
    [InlineData("drop[12].txt", "C:/src/drop3.txt", true)]
    [InlineData("[a-c].bin", "C:/src/b.bin", false)]
    [InlineData("[^a-c].bin", "C:/src/b.bin", true)]
    [InlineData("[^a-c].bin", "C:/src/z.bin", false)]
    [InlineData("name[[]1].txt", "C:/src/name[1].txt", false)]
    [InlineData("*.log\n!keep.log", "C:/src/nested/keep.log", true)]
    [InlineData("*.log\n!keep.log", "C:/src/nested/other.log", false)]
    [InlineData("*.log\n!keep.log\nkeep.*", "C:/src/keep.log", false)]
    [InlineData("!keep.log\n*.log", "C:/src/keep.log", false)]
    [InlineData("a**b.txt", "C:/src/a/child/b.txt", true)]
    [InlineData("a**b.txt", "C:/src/axyzb.txt", false)]
    [InlineData("cache", "C:/src/nested/cache/", false)]
    [InlineData("cache\n!cache", "C:/src/nested/cache/", true)]
    public void PreviewAndEstimateShareOrderedGlobMatching(string filters, string path, bool included)
    {
        var preview = Assert.Single(new FilterSimulator().Preview(filters, new[] { path }));
        Assert.Equal(included, preview.Included);
        Assert.Equal(!included, SavingsEstimator.IsExcluded(SavingsEstimator.ParseRules(filters), path));
    }

    private static bool Excluded(string rules, string path, bool ignoreCase = true)
        => FilterTranslator.Evaluate(FilterTranslator.CompileRules(rules, ignoreCase), path).Excluded;

    [Fact]
    public void The_top_of_a_drive_is_written_with_a_drive_wildcard()
    {
        Assert.True(Excluded("?:/Users/*/AppData/Local/Temp/", "C:/Users/me/AppData/Local/Temp/x.tmp"));
        Assert.True(Excluded("?:/Users/*/AppData/Local/Temp/", "D:/Users/me/AppData/Local/Temp"));
        // Only the drive holds ':', so the rule never starts deeper down.
        Assert.False(Excluded("?:/Users/*/AppData/Local/Temp/", "C:/Backups/OldPC/Users/bob/AppData/Local/Temp/x"));
        // restic sees Windows paths with their drive: a plain leading / matches nothing.
        Assert.False(Excluded("/Users/", "C:/Users/me/x"));
        Assert.False(Excluded("/pagefile.sys", "C:/pagefile.sys"));
        Assert.True(Excluded("C:/pagefile.sys", "c:/PAGEFILE.SYS"));
    }

    [Fact]
    public void A_rule_without_an_anchor_matches_at_any_depth_including_the_drive_and_the_folders_above_the_source()
    {
        Assert.True(Excluded("node_modules/", "C:/code/app/node_modules/lib.js"));
        // "*" is one name, and the drive counts as one.
        Assert.True(Excluded("*/**/npm-cache/*", "C:/npm-cache/x"));
        Assert.False(Excluded("*/**/npm-cache/*", "C:/npm-cache"));
        // A rule matching a folder above the backed-up one excludes all of it, as in restic.
        Assert.True(Excluded("temp/", "C:/temp/project/file.txt"));
    }

    [Fact]
    public void On_Linux_and_macOS_a_leading_slash_is_the_top_of_the_file_system()
    {
        Assert.True(Excluded("/home/*/.cache/", "/home/me/.cache/x", ignoreCase: false));
        Assert.False(Excluded("/home/*/.cache/", "/data/home/me/.cache/x", ignoreCase: false));
        Assert.False(Excluded("/home/*/.cache/", "/home/me/.CACHE/x", ignoreCase: false));
        // restic never starts an unanchored rule at the "/" itself.
        Assert.False(Excluded("*/**/x", "/x", ignoreCase: false));
        Assert.True(Excluded("*/**/x", "/a/x", ignoreCase: false));
    }

    [Fact]
    public void A_trailing_slash_changes_nothing_as_restic_cleans_it_off()
    {
        Assert.True(Excluded("*Cache/", "C:/a/ModuleAnalysisCache"));
        Assert.True(Excluded("Cache/", "C:/a/Cache/"));
    }

    [Fact]
    public void Lines_are_read_as_restic_reads_an_exclude_file()
    {
        var rules = FilterTranslator.ReadRules("  # comment\n\n  foo  \r\n$$RECYCLE.BIN\n$HOME_X/a\n${HOME_X}/b\nprice$\n", n => n == "HOME_X" ? "C:/me" : null);
        Assert.Equal(new[] { "foo", "$RECYCLE.BIN", "C:/me/a", "C:/me/b", "price$" }, rules.Select(r => r.Pattern));
        Assert.Equal("$$RECYCLE.BIN", rules[1].Line);
        // An unset variable is empty: "?:/$WinREAgent/" would be "?://", the whole drive.
        Assert.Equal("?://", FilterTranslator.Expand("?:/$WinREAgent/", _ => null));
        Assert.True(FilterTranslator.Evaluate(FilterTranslator.CompileRules("?:/$WinREAgent/", ignoreCase: true), "C:/Users/me/x").Excluded);
        Assert.Equal("?:/$WinREAgent/", FilterTranslator.Expand("?:/$$WinREAgent/", _ => null));
        Assert.Empty(FilterTranslator.ReadRules(null));
        Assert.Empty(FilterTranslator.ReadRules("   \n  \n"));
    }

    [Fact]
    public void Degenerate_rules_match_nothing_as_in_restic_and_never_throw()
    {
        // restic cleans "!" and "." to ".", a name no path has.
        foreach (var rule in new[] { "!", ".", "./", "!." })
            Assert.False(Excluded(rule, "C:/a/b.txt"), rule);
        // A byte-order mark at the start is dropped, as restic's textfile.Read does.
        Assert.Equal("*.tmp", FilterTranslator.ReadRules("\uFEFF*.tmp\n").Single().Pattern);
    }

    [Fact]
    public void Case_follows_the_flag_restic_gets()
    {
        Assert.True(Excluded("*.TMP", "C:/a/b.tmp", ignoreCase: true));
        Assert.False(Excluded("*.TMP", "/a/b.tmp", ignoreCase: false));
        Assert.Equal(Backspace.Services.Platform.PlatformInfo.IsWindows ? "--iexclude-file" : "--exclude-file", FilterTranslator.ExcludeFileFlag);
    }

    [Fact]
    public void Rules_restic_refuses_are_named()
    {
        Assert.Equal(new[] { "bad[abc" }, FilterTranslator.InvalidRules("ok\nbad[abc\n# [comment"));
        Assert.Empty(FilterTranslator.InvalidRules(string.Join("\n", RecommendedFilters.EssentialPatterns
            .Concat(RecommendedFilters.WindowsSystemPatterns).Concat(RecommendedFilters.CachesAndJunkPatterns).Concat(RecommendedFilters.AlwaysExcludedPatterns))));
    }

    [Fact]
    public void Recommended_rules_are_plain_restic_rules_with_every_dollar_doubled()
    {
        foreach (var p in RecommendedFilters.EssentialPatterns.Concat(RecommendedFilters.WindowsSystemPatterns)
                     .Concat(RecommendedFilters.CachesAndJunkPatterns).Concat(RecommendedFilters.AlwaysExcludedPatterns))
        {
            Assert.False(p.StartsWith("e:") || p.StartsWith("i:") || p.StartsWith('-') || p.StartsWith('+') || p.StartsWith('^'), p);
            // "$" only ever as "$$": restic would read "$Name" as a variable.
            Assert.Equal(p.Replace("$$", "$"), FilterTranslator.Expand(p, _ => "X"));
            // Drive rules start at the drive; a leading "/" matches nothing on Windows.
            if (Backspace.Services.Platform.PlatformInfo.IsWindows) Assert.False(p.StartsWith('/'), p);
        }
    }

    /// <summary>A rule with two ** follows restic's own steps (U151,
    /// 7 November 2026; the same rule is checked against the real restic in
    /// E57). Official restic tries the first ** on too few folders (restic
    /// issue 22131) and leaves C:/a/node_modules/x in; Backspace's Windows
    /// restic carries the fix.</summary>
    [Theory]
    [InlineData("C:/a/node_modules/x", true, false)]
    [InlineData("C:/a/node_modules/x/y", true, true)]
    [InlineData("C:/c/d/node_modules/z", true, true)]
    [InlineData("C:/a/other/x", false, false)]
    public void A_rule_with_two_double_stars_matches_as_restic_does(string path, bool fixedRestic, bool officialRestic)
    {
        var rules = FilterTranslator.CompileRules("C:/**/*/**/node_modules/*", ignoreCase: true);
        var parts = rules.Single().Parts!;
        Assert.NotNull(parts);
        var names = path.Split('/');
        Assert.Equal(fixedRestic, FilterTranslator.ResticMatch(parts, names, true, fixedBound: true));
        Assert.Equal(officialRestic, FilterTranslator.ResticMatch(parts, names, true, fixedBound: false));
        if (Backspace.Services.Platform.PlatformInfo.IsWindows) Assert.Equal(fixedRestic, FilterTranslator.Evaluate(rules, path).Excluded);
    }

    /// <summary>The upstream fix's own cases (restic pull request 22134).</summary>
    [Theory]
    [InlineData("/foo/**/bar/**/*.go", "/foo/bar/test.go", true)]
    [InlineData("/foo/**/bar/**/baz/**/*.go", "/foo/bar/baz/test.go", true)]
    [InlineData("/foo/**/bar/**/*.go", "/foo/test.go", false)]
    [InlineData("foo/**/bar/**/x", "/home/user/foo/blaaa/blaz/bar/shared/work/x", true)]
    [InlineData("user/**/hidden*/**/c", "/home/user/work/x/y/hidden/z/a/b/c", true)]
    public void The_fixed_restic_cases_match(string rule, string path, bool matched)
    {
        var parts = FilterTranslator.ResticParts(rule, System.Text.RegularExpressions.RegexOptions.CultureInvariant)!;
        var names = path.Split('/'); names[0] = "/";
        Assert.Equal(matched, FilterTranslator.ResticMatch(parts, names, false, fixedBound: true));
    }

    [Fact]
    public void A_rule_with_one_double_star_keeps_the_ordinary_pattern()
        => Assert.Null(FilterTranslator.CompileRules("Deep/**/drop.me", ignoreCase: true).Single().Parts);

    /// <summary>On Windows a rule starting with / matches nothing in restic;
    /// the editor lists it (U151, 7 November 2026).</summary>
    [Fact]
    public void Rules_starting_with_a_slash_are_reported_on_Windows()
    {
        var found = FilterTranslator.RulesMatchingNothing("# comment\n/top.txt\n!/Keep\n?:/Users/*/x\nC:/y\n*.tmp");
        if (Backspace.Services.Platform.PlatformInfo.IsWindows) Assert.Equal(new[] { "/top.txt", "!/Keep" }, found);
        else Assert.Empty(found);
    }
}
