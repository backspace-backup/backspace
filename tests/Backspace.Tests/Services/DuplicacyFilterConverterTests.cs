using System;
using System.IO;
using System.Linq;
using Backspace.Models;
using Backspace.Services;
using Xunit;
using Backspace.Tests.TestHelpers;

namespace Backspace.Tests.Services;

/// <summary>
/// U151 (6 November 2026): Duplicacy filter lines are converted once into
/// plain restic rules, and restic then reads the Filters text unchanged. The
/// owner approved the converted text of his three backups line by line; the
/// conversion must produce exactly that text.
/// </summary>
public sealed class DuplicacyFilterConverterTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Services", "Fixtures", "U151", name));

    [Theory]
    [InlineData("local_c", @"C:\")]
    [InlineData("local_d", @"D:\")]
    [InlineData("local_dropbox", @"C:\Users\Erwin\Dropbox\")]
    public void The_owners_backups_convert_to_the_approved_text_byte_for_byte(string backup, string source)
    {
        PlatformTest.WindowsOnly("the approved text holds Windows' recommended rules");
        var converted = DuplicacyFilterConverter.Convert(Fixture(backup + ".txt"), new[] { source });
        // Since U165 the Essentials are the recommended block's first group
        // instead of a block of their own: the approved text in that form.
        Assert.Equal(RecommendedFilters.UpgradeEssentials(Fixture(backup + "-after.txt"), new[] { source }), converted);
    }

    [Theory]
    [InlineData("local_c", @"C:\")]
    [InlineData("local_d", @"D:\")]
    [InlineData("local_dropbox", @"C:\Users\Erwin\Dropbox\")]
    public void A_converted_text_holds_no_Duplicacy_line_and_converting_it_again_changes_nothing(string backup, string source)
    {
        var converted = Fixture(backup + "-after.txt");
        Assert.False(DuplicacyFilterConverter.NeedsConversion(converted));
        Assert.True(DuplicacyFilterConverter.NeedsConversion(Fixture(backup + ".txt")));
        var config = new AppConfig();
        config.Backups.Add(new Backup { Name = backup, SourcePaths = { source }, FiltersText = converted });
        Assert.Empty(DuplicacyFilterConverter.ConvertStored(config));
        Assert.Equal(converted, config.Backups[0].FiltersText);
    }

    [Fact]
    public void Loading_settings_converts_a_stored_Duplicacy_filter_once_and_saves_it()
    {
        PlatformTest.WindowsOnly("Windows exclusion rules (?:/...); Linux and macOS get their own");
        using var workspace = new Backspace.Tests.TestHelpers.TempWorkspace("u151");
        var writer = new ConfigStore();
        writer.Load();
        writer.Update(c =>
        {
            c.Backups.Add(new Backup { Name = "local_dropbox", SourcePaths = { @"C:\Users\Erwin\Dropbox\" }, FiltersText = Fixture("local_dropbox.txt") });
            c.Backups.Add(new Backup { Name = "plain", SourcePaths = { @"C:\Data" }, FiltersText = "*.bak\n/odd\n" });
        });
        Assert.Contains("\\ne:(?i)", File.ReadAllText(AppPaths.ConfigFile));

        // The next start converts it, as the app does when it opens.
        var store = new ConfigStore();
        store.Load();
        var expected = RecommendedFilters.UpgradeEssentials(Fixture("local_dropbox-after.txt"), new[] { @"C:\Users\Erwin\Dropbox\" });
        Assert.Equal(expected, store.Current.Backups.Single(b => b.Name == "local_dropbox").FiltersText);
        // A text without Duplicacy lines is restic's already: left alone.
        Assert.Equal("*.bak\n/odd\n", store.Current.Backups.Single(b => b.Name == "plain").FiltersText);

        // Saved: another process reads the converted text.
        // (only "# was: e:(?i)…" comments name the old lines)
        Assert.DoesNotContain("\\ne:(?i)", File.ReadAllText(AppPaths.ConfigFile));
        var again = new ConfigStore();
        again.Load();
        Assert.Equal(expected, again.Current.Backups.Single(b => b.Name == "local_dropbox").FiltersText);
    }

    [Fact]
    public void Rules_tied_to_the_backed_up_folder_start_at_the_drive_with_the_folder_s_path()
    {
        var text = "e:(?i)^Cache/\ne:(?i)/node_modules/\n";
        var converted = DuplicacyFilterConverter.Convert(text, new[] { @"C:\Users\me\Projects" });
        Assert.Contains("?:/Users/me/Projects/Cache/*", converted.Split('\n'));
        Assert.Contains("*/**/node_modules/*", converted.Split('\n'));
        // Two folders: the rooted rule once for each.
        var two = DuplicacyFilterConverter.Convert(text, new[] { @"C:\A", @"D:\B" }).Split('\n');
        Assert.Contains("?:/A/Cache/*", two);
        Assert.Contains("?:/B/Cache/*", two);
    }

    [Fact]
    public void Glob_characters_and_dollars_in_a_folder_name_are_literal()
    {
        var converted = DuplicacyFilterConverter.Convert("e:^x/\n", new[] { @"C:\My [old] $stuff" }).Split('\n');
        Assert.Contains("?:/My [[]old] $$stuff/x/*", converted);
    }

    [Fact]
    public void Linux_folders_are_written_as_full_paths()
    {
        var converted = DuplicacyFilterConverter.Convert("e:^cache/\n", new[] { "/home/me/data" }).Split('\n');
        Assert.Contains("/home/me/data/cache/*", converted);
    }

    [Fact]
    public void Duplicacy_wildcard_lines_of_an_imported_text_are_converted_and_otherwise_left_alone()
    {
        var imported = DuplicacyFilterConverter.Convert("# Imported from Duplicacy. e: lines exclude\n-*.tmp\n+keep/\n", new[] { @"C:\Work" }).Split('\n');
        Assert.Contains("# was: -*.tmp", imported);
        Assert.Contains("?:/Work/**/*.tmp", imported);
        Assert.Contains("!?:/Work/keep", imported);
        // In restic, "-x" is a file name: a text that never came from Duplicacy keeps it.
        Assert.False(DuplicacyFilterConverter.NeedsConversion("-x\n*.bak\n"));
    }

    [Fact]
    public void A_line_restic_cannot_express_is_named_rather_than_dropped_silently()
    {
        var converted = DuplicacyFilterConverter.Convert("e:(?i)\\d+\\.log$\n", new[] { @"C:\" });
        Assert.Contains("# was: e:(?i)\\d+\\.log$", converted);
        Assert.Contains("# Not converted", converted);
        Assert.Empty(FilterTranslator.ReadRules(converted).Where(r => !r.Line.StartsWith("?:/", StringComparison.Ordinal)));
    }

    [Fact]
    public void Importing_from_Duplicacy_writes_restic_rules_with_the_essentials_ticked()
    {
        PlatformTest.WindowsOnly("Windows exclusion rules (?:/...); Linux and macOS get their own");
        var text = ConfigMigrator.ImportFilters("e:(?i)^Windows/\ne:(?i)^Users/[^/]+/Dropbox/\n", new[] { @"C:\" });
        Assert.False(DuplicacyFilterConverter.NeedsConversion(text));
        var lines = text.Replace("\r\n", "\n").Split('\n');
        Assert.Contains("?:/Windows/", lines); // recognised as Backspace's recommended rule
        Assert.Contains("?:/Users/*/Dropbox/*", lines);
        Assert.Contains("# was: e:(?i)^Users/[^/]+/Dropbox/", lines);
        Assert.Contains(RecommendedFilters.EssentialsHeading, lines);
        Assert.Contains("?:/pagefile.sys", lines);
        Assert.True(RecommendedFilters.ReadChoice(text)!.Essentials);
    }
}
