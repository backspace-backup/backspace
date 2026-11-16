using System;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U253 (owner, 16 November 2026): App updates shows only what is new
/// since the installed version, not every earlier release's notes.</summary>
public sealed class ReleaseNotesTests
{
    private const string Notes = """
        ## What's new in 1.1.54

        - Icon baked in.

        ### From 1.1.53

        - Smooth motion.

        ### From 1.1.52

        - Green stopped tile.
        """;

    [Fact]
    public void Only_the_versions_newer_than_the_installed_one_are_shown()
    {
        var one = ReleaseNotes.Since(Notes, new Version(1, 1, 53));
        Assert.Contains("Icon baked in", one);
        Assert.DoesNotContain("From 1.1.53", one);
        Assert.DoesNotContain("Smooth motion", one);

        var two = ReleaseNotes.Since(Notes, new Version(1, 1, 52, 0));
        Assert.Contains("Icon baked in", two);
        Assert.Contains("Smooth motion", two);
        Assert.DoesNotContain("Green stopped tile", two);

        Assert.Contains("Green stopped tile", ReleaseNotes.Since(Notes, new Version(1, 0, 2)));
    }

    [Fact]
    public void Notes_without_version_headings_are_kept_whole()
        => Assert.Equal("A clearer interface and safer updates.", ReleaseNotes.Since("A clearer interface and safer updates.", new Version(1, 1, 53)));
}
