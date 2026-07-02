using System;
using System.Linq;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

public class RecommendedFiltersTests
{
    [Fact]
    public void RenderBlock_includesHeaderAndFooterMarkers()
    {
        var block = RecommendedFilters.RenderBlock(
            includeWindowsSystem: true,
            includeCachesAndJunk: false,
            cloudExcludes: System.Array.Empty<RecommendedFilters.CloudSyncFolder>());
        Assert.Contains(RecommendedFilters.AutoBlockHeader, block);
        Assert.Contains(RecommendedFilters.AutoBlockFooter, block);
    }

    [Fact]
    public void RenderBlock_includesAtLeastOnePagefilePattern_whenWindowsSelected()
    {
        var block = RecommendedFilters.RenderBlock(
            includeWindowsSystem: true,
            includeCachesAndJunk: false,
            cloudExcludes: System.Array.Empty<RecommendedFilters.CloudSyncFolder>());
        Assert.Contains("pagefile", block);
        Assert.Contains("hiberfil", block);
        Assert.Contains("System Volume Information", block);
    }

    [Fact]
    public void RenderBlock_omitsBroadWindowsPatterns_whenNotSelected_butKeepsEssentials()
    {
        // After the refactor, "essentials" (pagefile, recycle bin,
        // $WinREAgent, etc.) are ALWAYS emitted regardless of toggles —
        // they protect the user even if they later add a drive root as
        // a Source. The toggle now only gates the broader Windows
        // system patterns (the entire Windows/ folder, MSOCache,
        // thumbs.db).
        var block = RecommendedFilters.RenderBlock(
            includeWindowsSystem: false,
            includeCachesAndJunk: true,
            cloudExcludes: System.Array.Empty<RecommendedFilters.CloudSyncFolder>());

        // Toggleable broad-system patterns are absent. We check the
        // exact `-i:/Windows/` line (anchored root-Windows-dir
        // exclude) — substring "/Windows/" alone also appears in
        // CachesAndJunk patterns like `Microsoft/Windows/Recent/`,
        // so the discriminator is the leading `-i:/`.
        Assert.DoesNotContain("-i:/Windows/", block);
        Assert.DoesNotContain("MSOCache", block);

        // Essentials remain.
        Assert.Contains("pagefile", block);
        Assert.Contains("$RECYCLE.BIN", block);
        Assert.Contains("$WinREAgent", block);

        // Caches block (from the second toggle) is present.
        Assert.Contains("temp", block);
    }

    [Fact]
    public void RenderBlock_emitsSourceRelativeCloudPaths()
    {
        var folder = new RecommendedFilters.CloudSyncFolder(
            RecommendedFilters.CloudSyncProvider.Dropbox,
            "Dropbox",
            @"C:\Users\me\Dropbox");
        var block = RecommendedFilters.RenderBlock(
            includeWindowsSystem: false,
            includeCachesAndJunk: false,
            cloudExcludes: new[] { folder });
        // The cloud-exclude line is a source-anchored bare glob
        // (`/Users/me/Dropbox/`) so only the specific cloud-sync
        // folder is excluded, not a same-named subdir elsewhere.
        // FilterTranslator routes the line through restic's
        // --iexclude on Windows because bare globs are platform-default.
        Assert.Contains("/Users/me/Dropbox/", block);
        // The label comment shows the original path so the user can
        // identify what was excluded.
        Assert.Contains(@"C:\Users\me\Dropbox", block);
    }

    [Fact]
    public void MergeIntoFilters_emitsCustomBlockAboveRecommendedBlock()
    {
        // New layout: the user-edit zone sits at the top, the
        // Backspace-generated block parks below it. Loose rules the
        // user wrote outside any marker pair get pulled INTO the
        // custom block on the first re-apply — that's their new edit
        // zone.
        var existing = "# my own rules\ne:(?i)/private/";
        var block = "AUTO-BLOCK";
        var merged = RecommendedFilters.MergeIntoFilters(existing, block);
        Assert.StartsWith(RecommendedFilters.CustomBlockHeader, merged);
        // The user's rules live inside the custom block now (pulled in
        // from the loose-content migration on first apply).
        var customStart = merged.IndexOf(RecommendedFilters.CustomBlockHeader, StringComparison.Ordinal);
        var customEnd = merged.IndexOf(RecommendedFilters.CustomBlockFooter, customStart, StringComparison.Ordinal);
        Assert.True(customEnd > customStart, "custom-block markers must be present");
        var customBody = merged[customStart..customEnd];
        Assert.Contains("# my own rules", customBody);
        Assert.Contains("e:(?i)/private/", customBody);
        // The recommended block appears AFTER the custom block.
        var autoIdx = merged.IndexOf("AUTO-BLOCK", StringComparison.Ordinal);
        Assert.True(autoIdx > customEnd, "AUTO-BLOCK should come after the custom-block footer");
    }

    [Fact]
    public void MergeIntoFilters_replacesExistingBlock_inPlace()
    {
        // The OLD block had only the broader Windows-system rules
        // (toggle ON, caches OFF). The NEW block has caches ON,
        // Windows-system OFF. Replacement should swap the toggleable
        // section while keeping the always-on essentials in both.
        var oldBlock = RecommendedFilters.RenderBlock(true, false, System.Array.Empty<RecommendedFilters.CloudSyncFolder>());
        var existing = oldBlock + "\n# user rules below\ne:/scratch/";

        var newBlock = RecommendedFilters.RenderBlock(false, true, System.Array.Empty<RecommendedFilters.CloudSyncFolder>());
        var merged = RecommendedFilters.MergeIntoFilters(existing, newBlock);

        // The replaced block has the new (caches) content.
        Assert.Contains("temp", merged);
        // The toggleable Windows-system patterns are gone (since the
        // new block has includeWindowsSystem=false).
        Assert.DoesNotContain("^Windows/", merged);
        Assert.DoesNotContain("MSOCache", merged);
        // Essentials remain — they're emitted in EVERY block render.
        Assert.Contains("pagefile", merged);
        // User's own rules below the markers are preserved (pulled into
        // the custom block on the new edit-zone-at-top layout).
        Assert.Contains("# user rules below", merged);
        Assert.Contains("e:/scratch/", merged);
        // No double-block — only one recommended header/footer pair
        // and one custom header/footer pair survive after merge.
        Assert.Equal(1, CountOccurrences(merged, RecommendedFilters.AutoBlockHeader));
        Assert.Equal(1, CountOccurrences(merged, RecommendedFilters.AutoBlockFooter));
        Assert.Equal(1, CountOccurrences(merged, RecommendedFilters.CustomBlockHeader));
        Assert.Equal(1, CountOccurrences(merged, RecommendedFilters.CustomBlockFooter));
    }

    [Fact]
    public void MergeIntoFilters_emptyExisting_yieldsCustomBlockThenAutoBlock()
    {
        var block = "AUTO";
        var merged = RecommendedFilters.MergeIntoFilters("", block);
        // Custom block (empty placeholder) comes first; recommended
        // block follows.
        Assert.StartsWith(RecommendedFilters.CustomBlockHeader, merged);
        Assert.Contains(RecommendedFilters.CustomBlockFooter, merged);
        Assert.EndsWith("AUTO", merged);
    }

    [Fact]
    public void ToSourceRelativePath_stripsDriveLetterAndForwardSlashes()
    {
        var rel = RecommendedFilters.ToSourceRelativePath(@"C:\Users\me\Dropbox");
        Assert.Equal(@"Users/me/Dropbox", rel);
    }

    [Fact]
    public void ToSourceRelativePath_leavesPathLiteralsAsGlobLiterals()
    {
        // Restic globs don't need regex escaping; dots and parens are
        // plain literal characters in glob land. We keep them verbatim.
        var rel = RecommendedFilters.ToSourceRelativePath(@"C:\Users\j.doe (work)\OneDrive");
        Assert.Equal(@"Users/j.doe (work)/OneDrive", rel);
    }

    [Fact]
    public void ToSourceRelativePath_emptyOrInvalid_returnsNull()
    {
        Assert.Null(RecommendedFilters.ToSourceRelativePath(""));
        Assert.Null(RecommendedFilters.ToSourceRelativePath("   "));
        Assert.Null(RecommendedFilters.ToSourceRelativePath(@"C:\"));
    }

    [Fact]
    public void SuggestForSources_driveRoot_enablesWindowsSystem()
    {
        var d = RecommendedFilters.SuggestForSources(new[] { @"C:\" });
        Assert.True(d.ExcludeWindowsSystem);
        Assert.True(d.ExcludeCachesAndJunk);
    }

    [Fact]
    public void SuggestForSources_userFolderOnly_keepsCachesButSkipsWindowsSystem()
    {
        var d = RecommendedFilters.SuggestForSources(new[] { @"C:\Users\me\Documents" });
        Assert.False(d.ExcludeWindowsSystem);
        Assert.True(d.ExcludeCachesAndJunk);
    }

    [Fact]
    public void NtuserDatPattern_coversTheWholeFamilyAsOneGlob()
    {
        // Regression for two things:
        //   1. The old `e:(?i)ntuser\.dat` regex matched ANY path
        //      containing the literal "ntuser.dat" — so a user's
        //      `notes\ntuser.dat.backup` got silently excluded. The
        //      glob form `ntuser.dat*` is anchored to the leaf name
        //      only.
        //   2. We used to emit four separate lines (`ntuser.dat`,
        //      `ntuser.dat.LOG`, `.LOG1`, `.LOG2`) when the single
        //      `ntuser.dat*` glob covers the whole family AND any
        //      future siblings (.blf, .regtrans-ms) Windows adds.
        var block = RecommendedFilters.RenderBlock(
            includeWindowsSystem: true,
            includeCachesAndJunk: true,
            cloudExcludes: System.Array.Empty<RecommendedFilters.CloudSyncFolder>());

        var normalised = block.Replace("\r\n", "\n");

        // Exactly one line should match the family — not four.
        Assert.Contains("ntuser.dat*\n", normalised);
        Assert.DoesNotContain("ntuser.dat.LOG\n", normalised);
        Assert.DoesNotContain("ntuser.dat.LOG1\n", normalised);
        Assert.DoesNotContain("ntuser.dat.LOG2\n", normalised);

        // The OLD Duplicacy regex must be gone — a future refactor
        // shouldn't silently reintroduce it without us noticing.
        Assert.DoesNotContain(@"e:(?i)ntuser\.dat", block);
    }

    [Fact]
    public void SuggestForSources_emptyOrNull_safe()
    {
        var d = RecommendedFilters.SuggestForSources(System.Array.Empty<string>());
        Assert.False(d.ExcludeWindowsSystem);
        Assert.True(d.ExcludeCachesAndJunk);
        var d2 = RecommendedFilters.SuggestForSources(null!);
        Assert.False(d2.ExcludeWindowsSystem);
        Assert.True(d2.ExcludeCachesAndJunk);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
