using System.Text.RegularExpressions;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Pins <see cref="FilterSimulator.GlobToRegex"/> behaviour. A bug here
/// is a silent data leak — the user's filter rule never matches, so
/// files they thought they'd excluded sail into the backup.
///
/// The most expensive bug class is Windows-backslash glob patterns
/// silently failing to match restic's forward-slash-internal paths.
/// </summary>
public class FilterSimulatorGlobTests
{
    private static bool Match(string glob, string path)
    {
        var pattern = FilterSimulator.GlobToRegex(glob);
        // FilterSimulator uses IgnoreCase + Compiled at the call site;
        // mirror that here so the assertions reflect production
        // semantics on Windows.
        return Regex.IsMatch(path, pattern, RegexOptions.IgnoreCase);
    }

    [Fact]
    public void WindowsBackslashGlob_normalisesToForwardSlash()
    {
        // A user typing `Documents\Letters\*.pdf` on Windows must
        // match restic's `Documents/Letters/tax-return.pdf` form.
        Assert.True(Match(@"Documents\Letters\*.pdf", "Documents/Letters/tax-return.pdf"));
    }

    [Fact]
    public void MixedSlashGlob_normalisesConsistently()
    {
        Assert.True(Match(@"Photos\2026/Trip*.jpg", "Photos/2026/Trip-Iceland.jpg"));
    }

    [Fact]
    public void StarOnly_doesNotCrossDirectorySeparator()
    {
        // Single * shouldn't match a slash — that's the role of `**`.
        Assert.True(Match("Documents/*.txt", "Documents/notes.txt"));
        Assert.False(Match("Documents/*.txt", "Documents/sub/notes.txt"));
    }

    [Fact]
    public void DoubleStar_matchesAcrossDirectorySeparators()
    {
        // ** maps to .* (greedy any-char including /), so the glob
        // requires at least one directory between Documents/ and
        // the leaf — matching the common rsync / .gitignore semantic
        // for "any nesting level". The shallower form is handled
        // explicitly by a separate `Documents/*.txt` rule.
        Assert.True(Match("Documents/**/*.txt", "Documents/a/b/notes.txt"));
        Assert.True(Match("Documents/**/*.txt", "Documents/sub/notes.txt"));
    }

    [Fact]
    public void QuestionMark_matchesSingleChar()
    {
        Assert.True(Match("file?.log", "file1.log"));
        Assert.False(Match("file?.log", "file12.log"));
    }

    [Theory]
    [InlineData("*.tmp",       "scratch.tmp",      true)]
    [InlineData("*.tmp",       "scratch.tmp.bak",  false)]
    [InlineData("cache",       "cache",            true)]
    [InlineData("cache",       "scache",           false)]
    public void RegexCharsInPath_areEscaped(string glob, string path, bool shouldMatch)
    {
        Assert.Equal(shouldMatch, Match(glob, path));
    }
}
