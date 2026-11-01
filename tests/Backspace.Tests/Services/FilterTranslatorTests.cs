using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Pins the Backspace filter text → restic exclude/iexclude contract.
/// Two shapes are accepted:
///   • bare restic glob lines (the canonical source format)
///   • Duplicacy <c>e:&lt;regex&gt;</c> lines (only because the
///     "Import from existing Duplicacy" flow reads filter files
///     verbatim; everywhere Backspace EMITS filter text it writes
///     bare globs).
/// </summary>
public class FilterTranslatorTests
{
    [Theory]
    [InlineData("drop[12].txt", "drop1.txt", false)]
    [InlineData("drop[12].txt", "drop3.txt", true)]
    [InlineData("[a-c].bin", "b.bin", false)]
    [InlineData("[^a-c].bin", "b.bin", true)]
    [InlineData("[^a-c].bin", "z.bin", false)]
    [InlineData("name[[]1].txt", "name[1].txt", false)]
    [InlineData("*.log\n!keep.log", "nested/keep.log", true)]
    [InlineData("*.log\n!keep.log", "nested/other.log", false)]
    [InlineData("*.log\n!keep.log\nkeep.*", "keep.log", false)]
    [InlineData("!keep.log\n*.log", "keep.log", false)]
    [InlineData("a**b.txt", "a/child/b.txt", true)]
    [InlineData("cache", "nested/cache/", false)]
    [InlineData("cache\n!cache", "nested/cache/", true)]
    public void PreviewAndEstimateShareOrderedGlobMatching(string filters, string path, bool included)
    {
        var preview = Assert.Single(new FilterSimulator().Preview(filters, new[] { path }));
        Assert.Equal(included, preview.Included);
        Assert.Equal(!included, SavingsEstimator.IsExcluded(SavingsEstimator.ParseRules(filters), path));
    }

    [Fact]
    public void ExceptionInInsensitiveListCannotUndoSensitiveExclusion()
    {
        var rules = new[] {
            new FilterTranslator.CompiledRule("keep.log", new System.Text.RegularExpressions.Regex("^keep[.]log$"), false, false),
            new FilterTranslator.CompiledRule("!keep.log", new System.Text.RegularExpressions.Regex("^keep[.]log$"), true, true)
        };
        Assert.True(FilterTranslator.Evaluate(rules, "keep.log").Excluded);
    }

    [Fact]
    public void SourceBinding_PreservesRootedAndNestedPatternsAndLiteralBrackets()
    {
        var source = System.IO.Path.GetFullPath("temp/source[1]");
        var root = source.Replace('\\', '/').Replace("[", "[[]");
        var r = FilterTranslator.TranslateForSource("temp/\n/root.txt\nsub\\*.tmp\n!keep.txt", source);
        var patterns = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Concat(r.Excludes, r.IExcludes));
        Assert.Equal(new[] { root + "/**/temp/", root + "/root.txt", root + "/**/sub/*.tmp", "!" + root + "/**/keep.txt" }, patterns);
    }

    [Fact]
    public void SourceBinding_DriveOrFilesystemRootDoesNotIntroduceDoubleSeparator()
    {
        var source = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath("."))!;
        var root = source.Replace('\\', '/').TrimEnd('/');
        var r = FilterTranslator.TranslateForSource("/pagefile.sys", source);
        Assert.Equal(root + "/pagefile.sys", Assert.Single(System.Linq.Enumerable.Concat(r.Excludes, r.IExcludes)));
    }

    // -----------------------------------------------------------------
    // Restic-native: one bare glob per line, # comments.
    // -----------------------------------------------------------------

    [Fact]
    public void BareGlob_RoutesToIExcludes_WhenPlatformDefaultIsInsensitive()
    {
        // On Windows the filesystem is case-insensitive, so bare globs
        // route to iexcludes (restic --iexclude) — a case-sensitive
        // pattern would silently miss files whose stored case differs.
        var r = FilterTranslator.Translate("foo/*.tmp", defaultsToCaseInsensitive: true);
        Assert.Contains("foo/*.tmp", r.IExcludes);
        Assert.Empty(r.Excludes);
    }

    [Fact]
    public void BareGlob_RoutesToExcludes_WhenPlatformDefaultIsSensitive()
    {
        var r = FilterTranslator.Translate("foo/*.tmp", defaultsToCaseInsensitive: false);
        Assert.Contains("foo/*.tmp", r.Excludes);
        Assert.Empty(r.IExcludes);
    }

    [Fact]
    public void Comments_AreIgnored()
    {
        var r = FilterTranslator.Translate("# this is a comment\nfoo", defaultsToCaseInsensitive: false);
        Assert.Single(r.Excludes);
        Assert.Equal("foo", r.Excludes[0]);
    }

    [Fact]
    public void BlankLines_AreIgnored()
    {
        var r = FilterTranslator.Translate("\n\nfoo\n\n", defaultsToCaseInsensitive: false);
        Assert.Single(r.Excludes);
        Assert.Equal("foo", r.Excludes[0]);
    }

    [Fact]
    public void NullAndEmpty_ReturnEmptyLists()
    {
        Assert.Empty(FilterTranslator.Translate(null).Excludes);
        Assert.Empty(FilterTranslator.Translate("").Excludes);
        Assert.Empty(FilterTranslator.Translate("   \n  \n").Excludes);
    }

    // -----------------------------------------------------------------
    // Duplicacy migration: e:/i: regex lines translate to restic globs with
    // the same meaning (DuplicacyFilterTranslatorTests holds the semantics).
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(@"e:(?i)^pagefile\.sys$", "/pagefile.sys")]
    [InlineData(@"e:(?i)(^|/)System Volume Information/", "System Volume Information/*")]
    [InlineData(@"e:(?i)^Windows/", "/Windows/*")]
    [InlineData(@"e:(?i)(^|/)\$RECYCLE\.BIN/", "$RECYCLE.BIN/*")]
    [InlineData(@"e:(?i)^Users/[^/]+/AppData/Local/", "/Users/*/AppData/Local/*")]
    [InlineData(@"e:(?i)/node_modules/", "*/**/node_modules/*")]
    [InlineData(@"e:(?i)^Python[^/]*/", "/Python*/*")]
    public void Duplicacy_Regex_TranslatesWithItsAnchor(string line, string glob)
        => Assert.Equal(new[] { glob }, FilterTranslator.Translate(line, defaultsToCaseInsensitive: false).IExcludes);

    [Fact]
    public void Duplicacy_RegexCaseSensitive_NoIFlag_RoutesToExcludes()
    {
        var r = FilterTranslator.Translate(@"e:^FooBar$", defaultsToCaseInsensitive: false);
        Assert.Equal(new[] { "/FooBar" }, r.Excludes);
        Assert.Empty(r.IExcludes);
    }

    [Fact]
    public void Duplicacy_Alternatives_BecomeOneGlobEach()
    {
        var r = FilterTranslator.Translate(@"e:(?i)\.(tmp|temp)$", defaultsToCaseInsensitive: false);
        Assert.Equal(new[] { "*.tmp", "*.temp" }, r.IExcludes);
    }

    [Fact]
    public void Duplicacy_UntranslatableLine_IsReportedNotGuessed()
    {
        var untranslated = new System.Collections.Generic.List<string>();
        var r = FilterTranslator.Translate("e:(?i)^logs/\\d+/\nfoo", defaultsToCaseInsensitive: false, untranslated);
        Assert.Equal(new[] { "foo" }, r.Excludes);
        Assert.Empty(r.IExcludes);
        Assert.Equal(new[] { @"e:(?i)^logs/\d+/" }, untranslated);
    }

    // -----------------------------------------------------------------
    // RecommendedFilters round-trip — every recommended pattern must
    // come through unchanged (they're already pure restic globs).
    // -----------------------------------------------------------------

    [Fact]
    public void RecommendedFiltersEssentials_AllProduceRestcNativeGlobs()
    {
        var lines = string.Join("\n", RecommendedFilters.EssentialPatterns);
        var r = FilterTranslator.Translate(lines, defaultsToCaseInsensitive: true);
        var total = RecommendedFilters.EssentialPatterns.Count;
        var produced = r.Excludes.Count + r.IExcludes.Count;
        Assert.Equal(total, produced);
    }

    [Fact]
    public void RecommendedFiltersEssentials_AreResticNativeStrings()
    {
        // Every recommended pattern is a bare restic glob — no
        // Backspace-specific prefixes that would surprise someone
        // taking the file to vanilla restic.
        foreach (var p in RecommendedFilters.EssentialPatterns)
        {
            Assert.False(p.StartsWith("-", System.StringComparison.Ordinal),
                $"Pattern uses legacy `-` prefix: {p}");
            Assert.False(p.StartsWith("e:", System.StringComparison.Ordinal),
                $"Pattern uses legacy Duplicacy `e:` prefix: {p}");
            Assert.False(p.StartsWith("i:", System.StringComparison.Ordinal),
                $"Pattern uses legacy Duplicacy `i:` prefix: {p}");
            Assert.False(p.StartsWith("+", System.StringComparison.Ordinal),
                $"Pattern uses legacy `+` prefix: {p}");
        }
    }
}
