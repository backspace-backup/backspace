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
        // Custom block (empty placeholder) comes first; recommended block
        // last: nothing follows it since U165.
        Assert.StartsWith(RecommendedFilters.CustomBlockHeader, merged);
        Assert.Contains(RecommendedFilters.CustomBlockFooter, merged);
        var lines = merged.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        Assert.Equal("AUTO", lines[^1]);
    }

    /// <summary>U165: the Essentials are the first recommended group, ticked by
    /// default; they hold only what no restore can use.</summary>
    [Fact]
    public void The_essentials_are_the_approved_list()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(new[]
        {
            "?:/pagefile.sys", "?:/hiberfil.sys", "?:/swapfile.sys", "?:/DumpStack.log.tmp", "?:/System Volume Information/",
            "?:/$$RECYCLE.BIN/", "?:/Config.Msi/", "?:/$$WinREAgent/", "?:/$$GetCurrent/", "?:/$$SysReset/", "?:/$$Windows.~BT/",
            "?:/$$Windows.~WS/", "?:/Windows10Upgrade/", "?:/Windows.old/", "?:/PerfLogs/", "?:/bootmgr", "?:/BOOTNXT",
            "?:/BOOTSECT.BAK", "?:/OneDriveTemp/", "?:/Users/*/AppData/Local/Microsoft/WindowsApps/",
        }, RecommendedFilters.EssentialPatterns);
        // No rule is in two groups.
        var all = RecommendedFilters.EssentialPatterns.Concat(RecommendedFilters.WindowsSystemPatterns).Concat(RecommendedFilters.CachesAndJunkPatterns).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Unticking_the_essentials_removes_their_rules()
    {
        if (!OperatingSystem.IsWindows()) return;
        var none = Array.Empty<RecommendedFilters.CloudSyncFolder>();
        var with = RecommendedFilters.MergeIntoFilters("own/", RecommendedFilters.RenderBlock(true, true, none, new[] { @"C:\" }));
        Assert.True(RecommendedFilters.ReadChoice(with)!.Essentials);
        Assert.True(FilterTranslator.Evaluate(FilterTranslator.CompileRules(with), "C:/pagefile.sys").Excluded);

        var without = RecommendedFilters.MergeIntoFilters(with, RecommendedFilters.RenderBlock(true, true, none, new[] { @"C:\" }, includeEssentials: false));
        var lines = without.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToList();
        Assert.Empty(lines.Intersect(RecommendedFilters.EssentialPatterns));
        Assert.DoesNotContain(RecommendedFilters.EssentialsHeading, lines);
        Assert.False(RecommendedFilters.ReadChoice(without)!.Essentials);
        var rules = FilterTranslator.CompileRules(without);
        Assert.False(FilterTranslator.Evaluate(rules, "C:/pagefile.sys").Excluded);
        Assert.False(FilterTranslator.Evaluate(rules, "C:/$RECYCLE.BIN/S-1-5/x.txt").Excluded);
        // The other groups and the person's own rule stay.
        Assert.True(FilterTranslator.Evaluate(rules, "C:/Windows/notepad.exe").Excluded);
        Assert.Contains("own/", lines);
        Assert.True(RecommendedFilters.IsApplied(without, true, true, none, new[] { @"C:\" }, includeEssentials: false));
        Assert.False(RecommendedFilters.IsApplied(without, true, true, none, new[] { @"C:\" }));
        // An unticked group offers no suggestions.
        Assert.True(RecommendedFilters.CheckForUpdates(without, sources: new[] { @"C:\" }).IsEmpty);
    }

    /// <summary>U165: each group sorted, rules from the top of the drive first,
    /// then by path ignoring case, $$ read as $.</summary>
    [Fact]
    public void Each_group_lists_rules_from_the_top_of_the_drive_first_then_by_path()
    {
        if (!OperatingSystem.IsWindows()) return;
        var block = RecommendedFilters.RenderBlock(true, true, Array.Empty<RecommendedFilters.CloudSyncFolder>());
        var groups = block.Replace("\r\n", "\n").Split("\n\n")
            .Select(g => g.Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).ToList())
            .Where(g => g.Count > 1).ToList();
        Assert.Equal(3, groups.Count);
        foreach (var rules in groups)
        {
            var anchored = rules.TakeWhile(r => r.StartsWith("?:/", StringComparison.Ordinal)).ToList();
            Assert.DoesNotContain(rules.Skip(anchored.Count), r => r.StartsWith("?:/", StringComparison.Ordinal));
            var key = (string r) => r.Replace("?:", "").Replace("$$", "$").ToLowerInvariant();
            Assert.Equal(anchored.OrderBy(key, StringComparer.Ordinal), anchored);
            Assert.Equal(rules.Skip(anchored.Count).OrderBy(key, StringComparer.Ordinal), rules.Skip(anchored.Count));
        }
        // $RECYCLE.BIN sorts as "$recycle.bin", before "bootmgr".
        Assert.Equal("?:/$$GetCurrent/", groups[0][0]);
        Assert.True(groups[0].IndexOf("?:/$$RECYCLE.BIN/") < groups[0].IndexOf("?:/bootmgr"));
        var caches = groups[2];
        Assert.Equal(caches.IndexOf("?:/Users/*/AppData/**/Cache/") + 1, caches.IndexOf("?:/Users/*/AppData/**/Caches/"));
        Assert.Equal("*.crdownload", caches[caches.FindIndex(r => !r.StartsWith("?:/", StringComparison.Ordinal))]);
    }

    /// <summary>U165: a hidden suggestion stays hidden when Apply rebuilds the
    /// block: a hidden addition isn't written, a kept rule is written again.</summary>
    [Fact]
    public void Apply_never_brings_back_a_hidden_suggestion()
    {
        if (!OperatingSystem.IsWindows()) return;
        var none = Array.Empty<RecommendedFilters.CloudSyncFolder>();
        var sources = new[] { @"C:\" };
        const string hiddenAdd = "node_modules/";
        const string keptRetired = "Users/*/AppData/Local/Packages/";
        var block = RecommendedFilters.RenderBlock(true, true, none, sources, hidden: new[] { hiddenAdd, keptRetired }, kept: new[] { keptRetired });
        var lines = block.Replace("\r\n", "\n").Split('\n').ToList();
        Assert.DoesNotContain(hiddenAdd, lines);
        Assert.Contains(keptRetired, lines);
        Assert.Contains(RecommendedFilters.OwnHeading, lines);
        // Without the hidden list, Apply writes it (the falsifiable side).
        Assert.Contains(hiddenAdd, RecommendedFilters.RenderBlock(true, true, none, sources).Replace("\r\n", "\n").Split('\n'));
        // The hidden ones count as done: nothing left to apply, nothing suggested.
        var text = RecommendedFilters.MergeIntoFilters("", block);
        Assert.True(RecommendedFilters.IsApplied(text, true, true, none, sources, hidden: new[] { hiddenAdd, keptRetired }));
        Assert.True(RecommendedFilters.CheckForUpdates(text, new[] { hiddenAdd, keptRetired }, sources).IsEmpty);
        // Unhidden, both are suggested again.
        var update = RecommendedFilters.CheckForUpdates(text, sources: sources);
        Assert.Contains(hiddenAdd, update.Add);
        Assert.Contains(keptRetired, update.Remove);
    }

    [Fact]
    public void The_rules_shown_under_a_group_are_those_Apply_writes_here()
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = new[] { @"C:\Users\me\Documents" };
        Assert.Empty(RecommendedFilters.RulesFor(RecommendedFilters.Group.Essentials, folder));
        Assert.Equal(RecommendedFilters.EssentialPatterns.Count, RecommendedFilters.RulesFor(RecommendedFilters.Group.Essentials, new[] { @"C:\" }).Count);
        Assert.Equal(new[] { "?:/Users/*/AppData/Local/Microsoft/WindowsApps/" },
            RecommendedFilters.RulesFor(RecommendedFilters.Group.Essentials, new[] { @"C:\Users\me" }));
        Assert.DoesNotContain("node_modules/", RecommendedFilters.RulesFor(RecommendedFilters.Group.CachesAndJunk, folder, new[] { "node_modules/" }));
        Assert.Contains("node_modules/", RecommendedFilters.RulesFor(RecommendedFilters.Group.CachesAndJunk, folder));
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
        RecommendedFilters.EssentialsHeading, "/pagefile.sys", "Config.Msi/", "/Recovery/", "System Volume Information/", "",
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

    // ---- U165 follow-up: a tick writes its group at once, nothing else ----

    [Fact]
    public void Ticking_a_group_changes_only_that_group_and_keeps_everything_else()
    {
        var none = Array.Empty<RecommendedFilters.CloudSyncFolder>();
        var sources = new[] { @"C:\" };
        var start = RecommendedFilters.MergeIntoFilters("my/own/rule/",
            RecommendedFilters.RenderBlock(false, true, none, sources)).Replace("\r\n", "\n");
        // A pending suggestion (a caches rule missing) and a rule written into the block by hand.
        var missing = RecommendedFilters.GroupRules(start, RecommendedFilters.Group.CachesAndJunk)[0];
        var text = RecommendedFilters.ApplyUpdates(start, new(new[] { "handmade/" }, new[] { missing }));
        Assert.DoesNotContain(missing, text.Split('\n'));

        var ticked = RecommendedFilters.WithGroup(text, RecommendedFilters.Group.WindowsSystem, true, sources);
        var choice = RecommendedFilters.ReadChoice(ticked)!;
        Assert.True(choice.WindowsSystem);
        Assert.True(choice.CachesAndJunk);
        Assert.Equal(RecommendedFilters.RulesFor(RecommendedFilters.Group.WindowsSystem, sources),
            RecommendedFilters.GroupRules(ticked, RecommendedFilters.Group.WindowsSystem));
        // The caches group is left as it was: its pending suggestion stays pending.
        Assert.DoesNotContain(missing, ticked.Split('\n'));
        Assert.Contains(missing, RecommendedFilters.CheckForUpdates(ticked, sources: sources).Add);
        Assert.Contains("handmade/", ticked.Split('\n'));
        Assert.StartsWith(RecommendedFilters.CustomBlockHeader, ticked);
        Assert.Contains("my/own/rule/", ticked.Split('\n'));

        // Unticking removes every rule of the group and its heading; ticking
        // again gives back the same text.
        var unticked = RecommendedFilters.WithGroup(ticked, RecommendedFilters.Group.WindowsSystem, false, sources);
        Assert.False(RecommendedFilters.ReadChoice(unticked)!.WindowsSystem);
        Assert.Empty(RecommendedFilters.GroupRules(unticked, RecommendedFilters.Group.WindowsSystem));
        Assert.Equal(ticked, RecommendedFilters.WithGroup(unticked, RecommendedFilters.Group.WindowsSystem, true, sources));
        // Unticking a group that isn't there changes nothing.
        Assert.Equal(unticked, RecommendedFilters.WithGroup(unticked, RecommendedFilters.Group.WindowsSystem, false, sources));
    }

    [Fact]
    public void Ticking_a_group_leaves_hidden_rules_out_and_drops_a_copied_block()
    {
        var hidden = RecommendedFilters.RulesFor(RecommendedFilters.Group.CachesAndJunk, null)[0];
        var copied = RecommendedFilters.AutoBlockHeader + "\ncopied/\n" + RecommendedFilters.AutoBlockFooter;
        var text = "mine/\n\n" + copied + "\nafter/\n" + copied;
        var result = RecommendedFilters.WithGroup(text, RecommendedFilters.Group.CachesAndJunk, true, null, new[] { hidden });
        var lines = result.Split('\n');
        Assert.DoesNotContain(hidden, lines);
        Assert.Equal(1, lines.Count(l => l.Trim() == RecommendedFilters.AutoBlockHeader));
        Assert.Equal(1, lines.Count(l => l.Trim() == "copied/"));
        // Rules before and after the block keep their places (order decides in restic).
        Assert.Equal(new[] { "mine/", "after/" }, lines.Where(l => l is "mine/" or "after/"));
        Assert.True(Array.IndexOf(lines, "after/") > Array.IndexOf(lines, RecommendedFilters.AutoBlockFooter));
    }

    [Fact]
    public void A_text_without_a_block_gets_one_below_its_rules_only_when_ticking()
    {
        Assert.Equal("mine/\n", RecommendedFilters.WithGroup("mine/\n", RecommendedFilters.Group.Essentials, false, null));
        var result = RecommendedFilters.WithGroup("mine/\n", RecommendedFilters.Group.Essentials, true, new[] { @"C:\Users\me\Documents" });
        Assert.StartsWith("mine/\n\n" + RecommendedFilters.AutoBlockHeader, result);
        // No Essentials rule fits a documents folder: the group is still ticked, under its note.
        Assert.True(RecommendedFilters.ReadChoice(result)!.Essentials);
        Assert.Contains(RecommendedFilters.NoneApplyNote, result);
        Assert.Empty(RecommendedFilters.GroupRules(result, RecommendedFilters.Group.Essentials));
    }

    [Fact]
    public void Cloud_folders_follow_their_ticks()
    {
        var dropbox = new RecommendedFilters.CloudSyncFolder(RecommendedFilters.CloudSyncProvider.Dropbox, "Dropbox", @"C:\Users\me\Dropbox");
        var none = Array.Empty<RecommendedFilters.CloudSyncFolder>();
        var text = RecommendedFilters.MergeIntoFilters("", RecommendedFilters.RenderBlock(false, true, none)).Replace("\r\n", "\n");
        var with = RecommendedFilters.WithCloudFolders(text, new[] { dropbox });
        Assert.Equal(new[] { RecommendedFilters.CloudLine(dropbox)! }, RecommendedFilters.ReadChoice(with)!.CloudLines);
        Assert.True(RecommendedFilters.ReadChoice(with)!.CachesAndJunk);
        var without = RecommendedFilters.WithCloudFolders(with, none);
        Assert.Empty(RecommendedFilters.ReadChoice(without)!.CloudLines);
        Assert.Equal(RecommendedFilters.GroupRules(text, RecommendedFilters.Group.CachesAndJunk),
            RecommendedFilters.GroupRules(without, RecommendedFilters.Group.CachesAndJunk));
    }
}
