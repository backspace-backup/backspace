using System;
using System.Linq;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

public class RecommendedFiltersTests
{
    [Fact]
    public void MergeIntoFilters_preservesAllCopiedCustomRulesAndReplacesAllRecommendedBlocks()
    {
        var custom = RecommendedFilters.CustomBlockHeader + "\r\nfirst/\r\n" + RecommendedFilters.CustomBlockFooter;
        var automatic = RecommendedFilters.AutoBlockHeader + "\nstale-generated/\n" + RecommendedFilters.AutoBlockFooter;
        var existing = "loose-before/\n" + custom + "\n" + automatic + "\nloose-middle/\n" +
            custom.Replace("first/", "second/") + "\n" + automatic + "\nloose-after/";
        var replacement = RecommendedFilters.RenderBlock(false, false, Array.Empty<RecommendedFilters.CloudSyncFolder>());
        var merged = RecommendedFilters.MergeIntoFilters(existing, replacement);
        Assert.Equal(1, CountOccurrences(merged, RecommendedFilters.CustomBlockHeader));
        Assert.Equal(1, CountOccurrences(merged, RecommendedFilters.CustomBlockFooter));
        Assert.Equal(1, CountOccurrences(merged, RecommendedFilters.AutoBlockHeader));
        Assert.DoesNotContain("stale-generated/", merged);
        var ordered = new[] { "loose-before/", "first/", "loose-middle/", "second/", "loose-after/" };
        Assert.Equal(ordered, merged.Split('\n').Select(l => l.Trim()).Where(ordered.Contains));
        Assert.Equal(merged, RecommendedFilters.MergeIntoFilters(merged, replacement));
    }

    [Fact]
    public void MergeIntoFilters_preservesRulesWithIncompleteOrNestedCustomMarkers()
    {
        var existing = RecommendedFilters.CustomBlockHeader + "\nkeep-a/\n" +
            RecommendedFilters.CustomBlockHeader + "\nkeep-b/\n" + RecommendedFilters.CustomBlockFooter +
            "\n# mention: " + RecommendedFilters.CustomBlockHeader + "\nkeep-c/";
        var merged = RecommendedFilters.MergeIntoFilters(existing, "NEW-AUTO");
        foreach (var rule in new[] { "keep-a/", "keep-b/", "keep-c/" }) Assert.Contains(rule, merged);
        Assert.Contains("# mention: " + RecommendedFilters.CustomBlockHeader, merged);
        Assert.Equal(1, merged.Split('\n').Count(l => l.Trim() == RecommendedFilters.CustomBlockHeader));
        var incomplete = RecommendedFilters.AutoBlockHeader + "\nuser-rule-without-footer/";
        Assert.Contains("user-rule-without-footer/", RecommendedFilters.MergeIntoFilters(incomplete, "NEW-AUTO"));
        var automatic = RecommendedFilters.RenderBlock(false, false, Array.Empty<RecommendedFilters.CloudSyncFolder>());
        var first = RecommendedFilters.MergeIntoFilters(incomplete, automatic);
        var second = RecommendedFilters.MergeIntoFilters(first, automatic);
        Assert.Contains("user-rule-without-footer/", second);
        Assert.Equal(first, second);
    }

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
        // The cloud-exclude line starts at the top of the drive
        // (`?:/Users/me/Dropbox/`) so only the specific cloud-sync
        // folder is excluded, not a same-named subdir elsewhere.
        Assert.Contains("?:/Users/me/Dropbox/", block.Replace("\r\n", "\n").Split('\n'));
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
        // block follows, then the essential exclusions (U151).
        Assert.StartsWith(RecommendedFilters.CustomBlockHeader, merged);
        Assert.Contains(RecommendedFilters.CustomBlockFooter, merged);
        var lines = merged.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        if (RecommendedFilters.AlwaysExcludedPatterns.Count == 0) { Assert.Equal("AUTO", lines[^1]); return; }
        Assert.Equal(RecommendedFilters.AlwaysBlockLines(), lines[^RecommendedFilters.AlwaysBlockLines().Count..]);
        Assert.Equal("AUTO", lines[^(RecommendedFilters.AlwaysBlockLines().Count + 2)]);
    }

    /// <summary>U151: the essential exclusions are a visible block at the end
    /// of the text; Apply keeps it last, with any edit the person made in it.</summary>
    [Fact]
    public void The_essential_block_stays_last_and_keeps_the_persons_edits()
    {
        if (RecommendedFilters.AlwaysExcludedPatterns.Count == 0) return;
        var edited = string.Join("\n", RecommendedFilters.AlwaysBlockLines()
            .Where(l => l != "?:/OneDriveTemp/").Append("my-extra-in-block/"));
        var text = "own/\n\n" + edited + "\n\nafter/\n";
        var merged = RecommendedFilters.MergeIntoFilters(text, RecommendedFilters.RenderBlock(false, false, Array.Empty<RecommendedFilters.CloudSyncFolder>()))
            .Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        Assert.Equal(edited.Split('\n'), merged[^edited.Split('\n').Length..]);
        Assert.Contains("own/", merged);
        Assert.Contains("after/", merged);
        Assert.Equal(1, merged.Count(l => l == RecommendedFilters.AlwaysBlockHeader));

        // A new backup's text gets the block once.
        var fresh = RecommendedFilters.WithAlwaysBlock("x/\n");
        Assert.Equal("x/\n\n" + RecommendedFilters.AlwaysBlockText() + "\n", fresh);
        Assert.Equal(fresh, RecommendedFilters.WithAlwaysBlock(fresh));
        Assert.Equal(RecommendedFilters.AlwaysBlockText() + "\n", RecommendedFilters.WithAlwaysBlock(""));
    }

    [Fact]
    public void The_always_block_is_the_approved_list()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(new[]
        {
            "?:/pagefile.sys", "?:/hiberfil.sys", "?:/swapfile.sys", "?:/DumpStack.log.tmp", "?:/System Volume Information/",
            "?:/Config.Msi/", "?:/$$WinREAgent/", "?:/$$GetCurrent/", "?:/$$SysReset/", "?:/$$Windows.~BT/", "?:/$$Windows.~WS/",
            "?:/Windows10Upgrade/", "?:/bootmgr", "?:/BOOTNXT", "?:/BOOTSECT.BAK", "?:/OneDriveTemp/",
            "?:/Users/*/AppData/Local/Microsoft/WindowsApps/",
        }, RecommendedFilters.AlwaysExcludedPatterns);
    }

    [Theory]
    [InlineData("/Windows/", "?:/Windows/")]
    [InlineData("/$RECYCLE.BIN/", "?:/$$RECYCLE.BIN/")]
    [InlineData("Users/*/AppData/**/Cache/", "?:/Users/*/AppData/**/Cache/")]
    [InlineData("InternalCache/", "?:/Users/*/AppData/**/InternalCache/")]
    [InlineData("Logs/CrashReporter/", "?:/Users/*/AppData/**/Logs/CrashReporter/")]
    [InlineData("AppData/Local/Microsoft/WindowsApps/", "?:/Users/*/AppData/Local/Microsoft/WindowsApps/")]
    [InlineData("node_modules/", "node_modules/")]
    [InlineData("ntuser.dat*", "ntuser.dat*")]
    public void Recommended_rules_written_before_U151_take_the_approved_restic_form(string before, string after)
        => Assert.Equal(after, RecommendedFilters.LegacyToRestic(before));

    /// <summary>U148, U151: a rule from the top of the drive applies to a
    /// folder on its way or inside it.</summary>
    [Theory]
    [InlineData("?:/Users/*/AppData/Local/Temp/", @"C:\", true)]
    [InlineData("?:/Users/*/AppData/Local/Temp/", @"C:\Users\me", true)]
    [InlineData("?:/Users/*/AppData/Local/Temp/", @"C:\Users\me\AppData\Local\Temp\x", false)] // it would leave out the whole backup
    [InlineData("?:/Users/*/AppData/Local/Temp/", @"C:\Users\me\Dropbox", false)]
    [InlineData("?:/Users/*/AppData/**/Cache/", @"C:\Users\me\AppData\Roaming\App", true)]
    [InlineData("?:/Windows/", @"D:\Projects", false)]
    [InlineData("?:/$$RECYCLE.BIN/", @"D:\", true)]
    [InlineData("node_modules/", @"C:\Users\me\Dropbox", true)]
    [InlineData("/home/*/.cache/", @"C:\", false)]
    public void A_drive_rule_applies_to_folders_on_its_way_or_inside_it(string rule, string source, bool applies)
        => Assert.Equal(applies, RecommendedFilters.AppliesTo(rule, new[] { source }));

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

        // The registry files hold a profile's settings: no longer recommended
        // at all (U106, 4 November 2026).
        Assert.DoesNotContain("ntuser.dat", normalised);
        Assert.DoesNotContain("UsrClass.dat", normalised);

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

    // ---- list changes (U057) ----

    /// <summary>A block saved by an earlier version: unanchored system
    /// folders (changed on 31 October 2026), the old Packages rule, a cloud
    /// folder and one rule the person typed into the block.</summary>
    private static string SavedBlock() => string.Join("\n",
        RecommendedFilters.CustomBlockHeader, "mine/", RecommendedFilters.CustomBlockFooter, "",
        RecommendedFilters.AutoBlockHeader,
        "# Essentials: always excluded; safe for any source.", "/pagefile.sys", "Config.Msi/", "/Recovery/", "System Volume Information/", "",
        "# App caches, temp files, dev junk: universally safe to skip.", "temp/", "Users/*/AppData/Local/Packages/", "my-extra/", "",
        "# Cloud-synced folders: already replicated in their providers' clouds.", @"# Dropbox at C:\Users\me\Dropbox", "/Users/me/Dropbox/", "",
        RecommendedFilters.AutoBlockFooter);

    [Fact]
    public void Changed_lists_are_offered_for_the_groups_the_backup_uses_only()
    {
        var update = RecommendedFilters.CheckForUpdates(SavedBlock());
        Assert.Contains("Config.Msi/", update.Remove);
        // Recovery is no longer recommended at all (U106): offered for removal, never added.
        Assert.Contains("/Recovery/", update.Remove);
        Assert.DoesNotContain(update.Add, p => p.Contains("Recovery"));
        Assert.Contains("Users/*/AppData/Local/Packages/", update.Remove);
        // Written before U151 from the top of the backed-up folder: offered in its restic form.
        Assert.Contains("/pagefile.sys", update.Remove);
        Assert.Contains("?:/pagefile.sys", update.Add);
        Assert.Contains("?:/Config.Msi/", update.Add);
        Assert.Contains("node_modules/", update.Add);
        // The Windows system group was never chosen; nothing from it is offered.
        Assert.DoesNotContain("/Windows/", update.Add);
        // Neither the cloud folder nor the person's own rule is an outdated recommendation.
        Assert.DoesNotContain("/Users/me/Dropbox/", update.Remove);
        Assert.DoesNotContain("my-extra/", update.Remove);
        Assert.DoesNotContain("my-extra/", update.Remove);
    }

    [Fact]
    public void Applying_an_update_keeps_own_rules_and_cloud_folders_and_settles()
    {
        var saved = SavedBlock();
        var updated = RecommendedFilters.ApplyUpdates(saved, RecommendedFilters.CheckForUpdates(saved)).Replace("\r\n", "\n");
        var lines = updated.Split('\n').Select(l => l.Trim()).ToList();
        Assert.Contains("mine/", lines);
        Assert.Contains("my-extra/", lines);
        Assert.Contains("/Users/me/Dropbox/", lines);
        Assert.Contains("?:/Config.Msi/", lines);
        Assert.DoesNotContain("Config.Msi/", lines);
        Assert.DoesNotContain("/pagefile.sys", lines);
        // Recovery, anchored or not, is gone: a manufacturer may keep recovery files there.
        Assert.DoesNotContain(lines, l => l.Contains("Recovery"));
        Assert.True(RecommendedFilters.CheckForUpdates(updated).IsEmpty);
    }

    [Fact]
    public void Ignored_changes_are_not_offered_again_and_saved_filters_never_change_by_themselves()
    {
        var saved = SavedBlock();
        var all = RecommendedFilters.CheckForUpdates(saved);
        var ignoredAll = RecommendedFilters.CheckForUpdates(saved, all.Add.Concat(all.Remove).ToList());
        Assert.True(ignoredAll.IsEmpty);
        // Checking reads only; the text stays exactly as saved.
        Assert.Equal(SavedBlock(), saved);
    }

    [Fact]
    public void A_declined_essential_addition_is_not_brought_back()
    {
        var missing = RecommendedFilters.EssentialPatterns.Last();
        var saved = RecommendedFilters.RenderBlock(RecommendedFilters.EssentialPatterns.Where(p => p != missing).ToList());
        var all = RecommendedFilters.CheckForUpdates(saved);
        Assert.Contains(missing, all.Add);

        // Declining it (U057 checkbox unticked) keeps it out.
        var declined = RecommendedFilters.ApplyUpdates(saved, new(all.Add.Where(p => p != missing).ToList(), all.Remove));
        Assert.DoesNotContain(missing, declined.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()));
        Assert.True(RecommendedFilters.CheckForUpdates(declined, new[] { missing }).IsEmpty);

        var accepted = RecommendedFilters.ApplyUpdates(saved, all);
        Assert.Contains(missing, accepted.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()));
    }

    [Fact]
    public void A_backup_without_a_recommended_block_is_never_offered_changes()
        => Assert.True(RecommendedFilters.CheckForUpdates("e:(?i)^Windows/\nfoo/").IsEmpty);

    // U105: the card says whether the ticks are already in the filters.
    [Fact]
    public void Applied_means_apply_would_change_no_rule()
    {
        var dropbox = new RecommendedFilters.CloudSyncFolder(RecommendedFilters.CloudSyncProvider.Dropbox, "Dropbox", @"C:\Users\me\Dropbox");
        var none = Array.Empty<RecommendedFilters.CloudSyncFolder>();
        var filters = RecommendedFilters.MergeIntoFilters("my/own/rule/", RecommendedFilters.RenderBlock(true, true, new[] { dropbox }));

        Assert.True(RecommendedFilters.IsApplied(filters, true, true, new[] { dropbox }));
        // A changed tick or cloud folder means Apply still has work to do.
        Assert.False(RecommendedFilters.IsApplied(filters, false, true, new[] { dropbox }));
        Assert.False(RecommendedFilters.IsApplied(filters, true, true, none));
        Assert.False(RecommendedFilters.IsApplied(RecommendedFilters.MergeIntoFilters("", RecommendedFilters.RenderBlock(true, false, none)), true, true, none));
        // No block at all, or one missing a rule of a ticked group, is not applied.
        Assert.False(RecommendedFilters.IsApplied("my/own/rule/", true, true, none));
        var partial = RecommendedFilters.ApplyUpdates(filters, new(Array.Empty<string>(), new[] { RecommendedFilters.WindowsSystemPatterns[0] }));
        Assert.False(RecommendedFilters.IsApplied(partial, true, true, new[] { dropbox }));

        var choice = RecommendedFilters.ReadChoice(filters)!;
        Assert.True(choice.WindowsSystem);
        Assert.True(choice.CachesAndJunk);
        Assert.Equal(new[] { RecommendedFilters.CloudLine(dropbox)! }, choice.CloudLines);
        Assert.Null(RecommendedFilters.ReadChoice("my/own/rule/"));
    }
}
