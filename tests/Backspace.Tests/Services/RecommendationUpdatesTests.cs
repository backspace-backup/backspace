using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U165 (9 November 2026): the Essentials are the recommended block's first
/// group instead of a block every text ended with; texts written before keep
/// excluding the same files. Updated recommendations are applied by
/// themselves only for backups set up in Easy mode; the others show "New
/// filter suggestions" on their card.
/// </summary>
public sealed class RecommendationUpdatesTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Services", "Fixtures", "U151", name));

    private static readonly RecommendedFilters.CloudSyncFolder[] NoCloud = Array.Empty<RecommendedFilters.CloudSyncFolder>();

    // ---- old texts convert to the same excluded set ----

    /// <summary>Paths under <paramref name="source"/> that each rule of the
    /// texts would match or nearly match: the rule written out with its
    /// wildcards filled in, a file inside it, a sibling, the same name deeper
    /// down; plus ordinary files. Restic's own matching (FilterTranslator)
    /// then decides each one under both texts.</summary>
    private static List<string> Probes(string source, params string[] texts)
    {
        var root = source.Replace('\\', '/').TrimEnd('/');
        var drive = root[..2];
        var paths = new List<string> { root + "/Documents/report.docx", root + "/x/y/z.txt", root + "/Users/me/Desktop/a.txt" };
        foreach (var rule in texts.SelectMany(t => FilterTranslator.ReadRules(t)).Select(r => r.Line.TrimStart('!')).Distinct())
        {
            var body = Regex.Replace(rule.Replace("$$", "$"), @"\[(.)\]", "$1").Replace("**", "a/b").Replace("*", "zz").Replace("?:", drive);
            var bases = body.StartsWith(drive, StringComparison.OrdinalIgnoreCase)
                ? new[] { body } : new[] { root + "/" + body, root + "/deep/er/" + body };
            foreach (var b in bases)
            {
                var p = b.TrimEnd('/');
                paths.AddRange(new[] { p, p + "/", p + "/inside.bin", p + "/sub/inside.bin", p + "-sibling/file", p + ".bak" });
                if (p.Contains('/')) paths.Add(root + "/copy/" + p[(p.IndexOf('/') + 1)..]);
            }
        }
        return paths.Where(p => p.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
    }

    private static List<string> Differences(string before, string after, string source)
    {
        var a = FilterTranslator.CompileRules(before);
        var b = FilterTranslator.CompileRules(after);
        return Probes(source, before, after)
            .Where(p => FilterTranslator.Evaluate(a, p).Excluded != FilterTranslator.Evaluate(b, p).Excluded).ToList();
    }

    /// <summary>The owner's three saved texts (approved in U151, each ending
    /// with the always-applied block) exclude exactly what they did.</summary>
    [Theory]
    [InlineData("local_c", @"C:\")]
    [InlineData("local_d", @"D:\")]
    [InlineData("local_dropbox", @"C:\Users\Erwin\Dropbox\")]
    public void The_owners_saved_texts_keep_excluding_the_same_files(string backup, string source)
    {
        if (!OperatingSystem.IsWindows()) return;
        var before = Fixture(backup + "-after.txt");
        Assert.True(RecommendedFilters.HasLegacyEssentials(before));
        var after = RecommendedFilters.UpgradeEssentials(before, new[] { source });

        var probes = Probes(source, before, after);
        Assert.True(probes.Count > 50, $"only {probes.Count} probes");
        Assert.Empty(Differences(before, after, source));

        // Today's form: no separate block, the Essentials ticked and first.
        var lines = after.Split('\n');
        Assert.DoesNotContain(RecommendedFilters.LegacyAlwaysBlockHeader, lines);
        Assert.DoesNotContain(RecommendedFilters.LegacyEssentialsHeading, lines);
        Assert.False(RecommendedFilters.HasLegacyEssentials(after));
        Assert.True(RecommendedFilters.ReadChoice(after)!.Essentials);
        Assert.Equal(after, RecommendedFilters.UpgradeEssentials(after, new[] { source }));
        // Nothing to suggest because of the conversion itself.
        var update = RecommendedFilters.CheckForUpdates(after, sources: new[] { source });
        Assert.DoesNotContain(update.Add.Concat(update.Remove), p => RecommendedFilters.EssentialPatterns.Contains(p));
        // The rest of the text is untouched.
        var own = before[..before.IndexOf(RecommendedFilters.LegacyAlwaysBlockHeader, StringComparison.Ordinal)];
        var firstEssentials = own.IndexOf(RecommendedFilters.LegacyEssentialsHeading, StringComparison.Ordinal);
        Assert.StartsWith(firstEssentials < 0 ? own.TrimEnd() : own[..firstEssentials], after);
    }

    [Fact]
    public void The_probes_catch_a_conversion_that_loses_a_rule()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Falsifiable: dropping the app aliases or the page file is noticed.
        var before = Fixture("local_c-after.txt");
        var after = RecommendedFilters.UpgradeEssentials(before, new[] { @"C:\" });
        var broken = after.Replace("?:/Users/*/AppData/Local/Microsoft/WindowsApps/\n", "").Replace("?:/pagefile.sys\n", "");
        var differences = Differences(before, broken, @"C:\");
        Assert.Contains("C:/pagefile.sys", differences);
        Assert.Contains(differences, p => p.Contains("WindowsApps", StringComparison.Ordinal));
    }

    [Fact]
    public void A_text_with_only_the_old_block_gets_a_block_with_the_essentials_alone()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Easy mode with "Skip junk" unticked wrote the person's rules and the always-applied block.
        var always = Fixture("local_c-after.txt");
        always = always[always.IndexOf(RecommendedFilters.LegacyAlwaysBlockHeader, StringComparison.Ordinal)..];
        var before = "my-own/\n\n" + always;
        foreach (var source in new[] { @"C:\", @"C:\Users\me", @"D:\Photos" })
        {
            var after = RecommendedFilters.UpgradeEssentials(before, new[] { source });
            Assert.Empty(Differences(before, after, source));
            var choice = RecommendedFilters.ReadChoice(after)!;
            Assert.True(choice.Essentials);
            Assert.False(choice.WindowsSystem);
            Assert.False(choice.CachesAndJunk);
            Assert.StartsWith("my-own/\n\n" + RecommendedFilters.AutoBlockHeader, after);
        }
        // A whole drive then gets today's three new Essentials offered, nothing more.
        var drive = RecommendedFilters.UpgradeEssentials(before, new[] { @"C:\" });
        var update = RecommendedFilters.CheckForUpdates(drive, sources: new[] { @"C:\" });
        Assert.Equal(new[] { "?:/$$RECYCLE.BIN/", "?:/PerfLogs/", "?:/Windows.old/" }, update.Add.OrderBy(p => p));
        Assert.Empty(update.Remove);
        // A folder where none of them can match keeps the group ticked under a note.
        var folder = RecommendedFilters.UpgradeEssentials(before, new[] { @"D:\Photos" });
        Assert.Contains(RecommendedFilters.NoneApplyNote, folder.Split('\n'));
    }

    [Fact]
    public void Loading_settings_converts_old_texts_once_and_saves_them()
    {
        using var workspace = new TempWorkspace("u165-load");
        var writer = new ConfigStore();
        writer.Load();
        writer.Update(c => c.Backups.Add(new Backup { Name = "local_c", SourcePaths = { @"C:\" }, FiltersText = Fixture("local_c-after.txt") }));
        var store = new ConfigStore();
        store.Load();
        var text = store.Current.Backups.Single().FiltersText;
        Assert.Equal(RecommendedFilters.UpgradeEssentials(Fixture("local_c-after.txt"), new[] { @"C:\" }), text);
        Assert.DoesNotContain(RecommendedFilters.LegacyAlwaysBlockHeader, File.ReadAllText(AppPaths.ConfigFile));
        Assert.Empty(RecommendedFilters.UpdateStored(store.Current));
    }

    // ---- updated recommendations: automatic or offered ----

    /// <summary>A whole-drive backup whose block lacks one rule of each ticked
    /// group, as a Backspace update adding those rules would leave it.</summary>
    private static Backup DriveBackupMissing(params string[] missing)
    {
        var block = RecommendedFilters.RenderBlock(true, true, NoCloud, new[] { @"C:\" });
        var text = RecommendedFilters.MergeIntoFilters("own/", block);
        foreach (var rule in missing) text = text.Replace("\n" + rule + "\n", "\n").Replace("\r\n" + rule + "\r\n", "\r\n");
        return new Backup { Name = "drive", SourcePaths = { @"C:\" }, FiltersText = text.Replace("\r\n", "\n") };
    }

    [Fact]
    public void Automatic_updates_add_the_new_rules_of_ticked_groups_except_hidden_ones()
    {
        if (!OperatingSystem.IsWindows()) return;
        var config = new AppConfig();
        var auto = DriveBackupMissing("node_modules/", "?:/Windows.old/", "?:/MSOCache/");
        auto.Name = "easy";
        auto.AutoApplyRecommendations = true;
        auto.IgnoredRecommendationChanges.Add("?:/MSOCache/");
        var offered = DriveBackupMissing("node_modules/");
        offered.Name = "advanced";
        config.Backups.Add(auto);
        config.Backups.Add(offered);
        var offeredBefore = offered.FiltersText;

        Assert.Equal(new[] { "easy" }, RecommendedFilters.UpdateStored(config));
        var lines = auto.FiltersText.Split('\n');
        Assert.Contains("node_modules/", lines);
        Assert.Contains("?:/Windows.old/", lines);
        Assert.DoesNotContain("?:/MSOCache/", lines); // hidden: never re-applied
        Assert.Contains("own/", lines);
        Assert.False(RecommendedFilters.HasSuggestions(auto));
        // Again: nothing more to do.
        Assert.Empty(RecommendedFilters.UpdateStored(config));

        // Not automatic: the saved text never changes by itself; the card offers it.
        Assert.Equal(offeredBefore, offered.FiltersText);
        Assert.True(RecommendedFilters.HasSuggestions(offered));
    }

    [Fact]
    public void The_card_line_shows_only_for_a_ticked_changed_group_without_automatic_updates()
    {
        if (!OperatingSystem.IsWindows()) return;
        var changed = DriveBackupMissing("node_modules/");
        Assert.True(RecommendedFilters.HasSuggestions(changed));

        // Same text, automatic updates on: no line.
        Assert.False(RecommendedFilters.HasSuggestions(new Backup { SourcePaths = { @"C:\" }, FiltersText = changed.FiltersText, AutoApplyRecommendations = true }));
        // The change is hidden: no line.
        Assert.False(RecommendedFilters.HasSuggestions(new Backup { SourcePaths = { @"C:\" }, FiltersText = changed.FiltersText, IgnoredRecommendationChanges = { "node_modules/" } }));
        // The changed group is unticked: no line.
        var unticked = RecommendedFilters.MergeIntoFilters("own/", RecommendedFilters.RenderBlock(true, false, NoCloud, new[] { @"C:\" }));
        Assert.False(RecommendedFilters.HasSuggestions(new Backup { SourcePaths = { @"C:\" }, FiltersText = unticked }));
        // A complete block, or rules without one: no line.
        Assert.False(RecommendedFilters.HasSuggestions(new Backup { SourcePaths = { @"C:\" },
            FiltersText = RecommendedFilters.MergeIntoFilters("", RecommendedFilters.RenderBlock(true, true, NoCloud, new[] { @"C:\" })) }));
        Assert.False(RecommendedFilters.HasSuggestions(new Backup { SourcePaths = { @"C:\" }, FiltersText = "own/\n" }));
    }

    // ---- Easy and Advanced ----

    [Fact]
    public void Easy_mode_writes_a_block_once_and_keeps_rules_and_hidden_choices_on_later_saves()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fresh = new Backup { SourcePaths = { @"C:\" } };
        var first = OnboardingViewModel.EasyFilters(fresh, skipJunk: true);
        Assert.True(RecommendedFilters.ReadChoice(first)!.Essentials);
        Assert.Contains("?:/pagefile.sys", first.Split('\n'));

        // Saved again in Easy mode: the person's rule and a hidden addition survive.
        var edited = new Backup { SourcePaths = { @"C:\" }, FiltersText = RecommendedFilters.MergeIntoFilters("own/", first).Replace("\r\n", "\n").Replace("node_modules/\n", ""),
            IgnoredRecommendationChanges = { "node_modules/" }, AutoApplyRecommendations = true };
        var again = OnboardingViewModel.EasyFilters(edited, skipJunk: true);
        Assert.Contains("own/", again.Split('\n'));
        Assert.DoesNotContain("node_modules/", again.Split('\n'));
        // Unticking "Skip junk for me" removes the whole block, Essentials too.
        var none = OnboardingViewModel.EasyFilters(edited, skipJunk: false);
        Assert.Null(RecommendedFilters.ReadChoice(none));
        Assert.Contains("own/", none.Split('\n'));
        Assert.False(FilterTranslator.Evaluate(FilterTranslator.CompileRules(none), "C:/pagefile.sys").Excluded);
    }

    // ---- measured effect of each suggestion ----

    [Fact]
    public async Task Each_suggestion_is_measured_against_todays_rules()
    {
        using var workspace = new TempWorkspace("u165-measure", vault: false);
        var root = Path.Combine(workspace.Root, "src");
        void Write(string relative, int size)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[size]);
        }
        Write("app/node_modules/a.js", 1000);
        Write("app/node_modules/deep/b.js", 2000);
        Write("app/src/main.js", 50);
        Write("cache/__pycache__/x.pyc", 300);
        Write("old/kept.txt", 70);
        Write("docs/report.docx", 10);

        // Today's rules already leave out old/ and __pycache__/.
        var text = "old/\n__pycache__/\n";
        var effects = await new SavingsEstimator().MeasureChangesAsync(new[] { root }, text, new[]
        {
            ("node_modules/", true),   // new: two files
            ("*.pyc", true),           // matches, but already left out: changes nothing
            ("*.psd", true),           // matches nothing
            ("old/", false),           // removed: one file comes back
        }, CancellationToken.None);
        Assert.False(effects.Truncated);
        Assert.Equal(new SuggestionEffect(2, 3000), effects.Changes[0]);
        Assert.Equal(new SuggestionEffect(0, 0), effects.Changes[1]);
        Assert.Equal(new SuggestionEffect(0, 0), effects.Changes[2]);
        Assert.Equal(new SuggestionEffect(1, 70), effects.Changes[3]);

        var add = new RecommendationChangeChoice("node_modules/");
        add.ShowEffect(effects.Changes[0], false);
        Assert.Equal("Leaves out 2 files (2.9 KB) of this backup.", add.EffectLabel);
        Assert.Equal("App caches and temporary files", add.GroupLabel);
        var nothing = new RecommendationChangeChoice("*.psd");
        nothing.ShowEffect(effects.Changes[2], false);
        Assert.Equal("Matches nothing here today.", nothing.EffectLabel);
        var removal = new RecommendationChangeChoice("old/", isAddition: false);
        removal.ShowEffect(effects.Changes[3], false);
        Assert.Equal("Removing it backs up 1 file (70 B) of this backup.", removal.EffectLabel);
        Assert.Equal("No longer recommended", removal.GroupLabel);
    }
}
