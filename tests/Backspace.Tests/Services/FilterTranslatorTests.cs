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
    // Duplicacy migration — `e:regex` lines translate to a best-effort
    // restic glob so an "Import from existing Duplicacy" round-trips
    // without manual edits.
    // -----------------------------------------------------------------

    [Fact]
    public void Duplicacy_RegexAnchoredExact_BecomesLiteral()
    {
        var r = FilterTranslator.Translate(@"e:(?i)^pagefile\.sys$", defaultsToCaseInsensitive: false);
        Assert.Contains("pagefile.sys", r.IExcludes);
    }

    [Fact]
    public void Duplicacy_RegexAnywhereDirectory_BecomesDoubleStarGlob()
    {
        var r = FilterTranslator.Translate(@"e:(?i)(^|/)System Volume Information/", defaultsToCaseInsensitive: false);
        Assert.Contains("**/System Volume Information/**", r.IExcludes);
    }

    [Fact]
    public void Duplicacy_RegexAnchoredDirectory_BecomesRootDoubleStarGlob()
    {
        var r = FilterTranslator.Translate(@"e:(?i)^Windows/", defaultsToCaseInsensitive: false);
        Assert.Contains("Windows/**", r.IExcludes);
    }

    [Fact]
    public void Duplicacy_RegexEscapedDollarLiteral_RoundTrips()
    {
        var r = FilterTranslator.Translate(@"e:(?i)(^|/)\$RECYCLE\.BIN/", defaultsToCaseInsensitive: false);
        Assert.Contains("**/$RECYCLE.BIN/**", r.IExcludes);
    }

    [Fact]
    public void Duplicacy_RegexCaseSensitive_NoIFlag_RoutesToExcludes()
    {
        var r = FilterTranslator.Translate(@"e:^FooBar$", defaultsToCaseInsensitive: false);
        Assert.Contains("FooBar", r.Excludes);
        Assert.Empty(r.IExcludes);
    }

    [Fact]
    public void Duplicacy_RegexSingleSegmentWildcard_Translates()
    {
        var r = FilterTranslator.Translate(@"e:(?i)^Users/[^/]+/AppData/Local/", defaultsToCaseInsensitive: false);
        Assert.Contains("Users/*/AppData/Local/**", r.IExcludes);
    }

    [Fact]
    public void Duplicacy_IncludeHint_IsIgnored()
    {
        // `i:` is the Duplicacy include-regex marker; restic has no
        // exclude-list analog so the line drops silently.
        var r = FilterTranslator.Translate("i:never-exclude\nfoo", defaultsToCaseInsensitive: false);
        Assert.Single(r.Excludes);
        Assert.Equal("foo", r.Excludes[0]);
    }

    [Fact]
    public void Duplicacy_RegexUntranslatableAlternation_DropsLine()
    {
        // We don't expand `(tmp|temp)` alternations — emitters split
        // those into two separate lines.
        var r = FilterTranslator.Translate(@"e:(?i)\.(tmp|temp)$", defaultsToCaseInsensitive: false);
        Assert.Empty(r.Excludes);
        Assert.Empty(r.IExcludes);
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
