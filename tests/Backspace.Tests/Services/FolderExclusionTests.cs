using System;
using System.IO;
using System.Threading;
using Backspace.Services;
using Xunit;
using Backspace.Tests.TestHelpers;

namespace Backspace.Tests.Services;

/// <summary>
/// U178 (10 November 2026): "Exclude this folder" in What this backup added.
/// The rule written for a folder, where it goes in the Filters text, the
/// folder already excluded, an exception that keeps it, and Undo.
/// </summary>
public class FolderExclusionTests
{
    private static readonly string Header = RecommendedFilters.CustomBlockHeader;
    private static readonly string Footer = RecommendedFilters.CustomBlockFooter;

    [Fact]
    public void Rule_is_written_from_the_top_of_the_drive_for_a_drive_source()
    {
        Assert.Equal("?:/Users/", FolderExclusion.Rule(@"C:\Users"));
        Assert.Equal("?:/Users/me/AppData/Local/Temp/", FolderExclusion.Rule(@"C:\Users\me\AppData\Local\Temp\"));
        Assert.Equal("?:/Games/", FolderExclusion.Rule(@"D:\Games"));
        // A drive or share root is never offered: its rule would leave out everything.
        Assert.Null(FolderExclusion.Rule(@"C:\"));
        Assert.Null(FolderExclusion.Rule(@"\\nas\share"));
    }

    [Fact]
    public void Rule_for_a_nested_folder_excludes_it_and_what_it_holds_and_nothing_beside_it()
    {
        var folder = @"C:\Users\me\Projects\app\bin\Debug";
        var rule = FolderExclusion.Rule(folder)!;
        Assert.Equal("?:/Users/me/Projects/app/bin/Debug/", rule);
        var filters = FolderExclusion.Add("", rule);
        Assert.Equal(rule, FolderExclusion.ExcludedBy(filters, folder));
        Assert.Equal(rule, FolderExclusion.ExcludedBy(filters, folder + @"\net9.0\app.dll"));
        // Negative cases: the parent, a sibling with a longer name, the same name elsewhere.
        Assert.Null(FolderExclusion.ExcludedBy(filters, @"C:\Users\me\Projects\app\bin"));
        Assert.Null(FolderExclusion.ExcludedBy(filters, @"C:\Users\me\Projects\app\bin\Debug2"));
        Assert.Null(FolderExclusion.ExcludedBy(filters, @"C:\Users\me\Projects\other\bin\Debug"));
    }

    [Fact]
    public void Brackets_spaces_and_dollars_are_literal()
    {
        var folder = @"C:\Data\Photos [2024]\$Temp files";
        var rule = FolderExclusion.Rule(folder)!;
        Assert.Equal("?:/Data/Photos [[]2024]/$$Temp files/", rule);
        Assert.Empty(FilterTranslator.InvalidRules(rule));
        var filters = FolderExclusion.Add("", rule);
        Assert.Equal(rule, FolderExclusion.ExcludedBy(filters, folder));
        Assert.Equal(rule, FolderExclusion.ExcludedBy(filters, folder + @"\a b.jpg"));
        // "[2024]" is not a character class: "Photos 2" must survive, and "$Temp" is not a variable.
        Assert.Null(FolderExclusion.ExcludedBy(filters, @"C:\Data\Photos 2\$Temp files"));
        Assert.Null(FolderExclusion.ExcludedBy(filters, @"C:\Data\Photos [2024]\Temp files"));
        Assert.Equal("?:/Data/a[*]b[?]/", FolderExclusion.Rule(@"C:\Data\a*b?"));
    }

    [Fact]
    public void Add_puts_the_rule_at_the_end_of_the_custom_block_and_keeps_everything_else()
    {
        PlatformTest.WindowsOnly("Windows exclusion rules (?:/...); Linux and macOS get their own");
        var recommended = RecommendedFilters.RenderBlock(false, false, Array.Empty<RecommendedFilters.CloudSyncFolder>());
        var existing = RecommendedFilters.MergeIntoFilters(Header + "\nnode_modules/\n" + Footer, recommended);
        var after = FolderExclusion.Add(existing, "?:/Users/me/Videos/");
        // Every other byte is kept, line endings included (the merged text may mix them).
        var nl = existing[existing.IndexOf("node_modules/", StringComparison.Ordinal) + "node_modules/".Length] == '\r' ? "\r\n" : "\n";
        Assert.Equal(existing.Replace("node_modules/" + nl, "node_modules/" + nl + "?:/Users/me/Videos/" + nl), after);
        Assert.True(after.IndexOf("?:/Users/me/Videos/", StringComparison.Ordinal) < after.IndexOf(Footer, StringComparison.Ordinal));
        Assert.Equal(1, Count(after, Header));
        // Already a line: nothing is added twice.
        Assert.Equal(after, FolderExclusion.Add(after, "?:/Users/me/Videos/"));
        Assert.Equal(after, FolderExclusion.Add(after, "?:/users/ME/videos/"));
    }

    [Fact]
    public void Add_without_a_custom_block_puts_one_at_the_top_and_keeps_the_rest()
    {
        Assert.Equal(Header + "\n?:/x/\n" + Footer + "\n", FolderExclusion.Add("", "?:/x/"));
        var loose = "*.tmp\n!?:/keep/\n";
        var after = FolderExclusion.Add(loose, "?:/x/");
        Assert.Equal(Header + "\n?:/x/\n" + Footer + "\n\n" + loose, after);
    }

    [Fact]
    public void A_folder_already_excluded_names_the_rule_that_excludes_it()
    {
        var filters = Header + "\n?:/Users/me/Projects/\n" + Footer;
        Assert.Equal("?:/Users/me/Projects/", FolderExclusion.ExcludedBy(filters, @"C:\Users\me\Projects\app\bin"));
        Assert.Null(FolderExclusion.ExcludedBy(filters, @"C:\Users\me\Music"));
        // Recommended rules count too.
        Assert.Equal("node_modules/", FolderExclusion.ExcludedBy("node_modules/", @"C:\src\web\node_modules\react"));
    }

    [Fact]
    public void An_exception_further_down_that_keeps_the_folder_is_named()
    {
        var folder = @"C:\Users\me\Projects\app\bin";
        var filters = Header + "\n# mine\n" + Footer + "\n!?:/Users/me/Projects/\n";
        var after = FolderExclusion.Add(filters, FolderExclusion.Rule(folder)!);
        Assert.Null(FolderExclusion.ExcludedBy(after, folder));
        Assert.Equal("!?:/Users/me/Projects/", FolderExclusion.KeptBy(after, folder));
        // Without the exception, nothing keeps it.
        Assert.Null(FolderExclusion.KeptBy(FolderExclusion.Add(Header + "\n" + Footer, FolderExclusion.Rule(folder)!), folder));
        // An exception for a folder elsewhere does not count.
        Assert.Null(FolderExclusion.KeptBy(Header + "\n" + Footer + "\n!?:/Users/me/Music/", folder));
    }

    [Fact]
    public void Remove_takes_out_exactly_the_rule_added()
    {
        var recommended = RecommendedFilters.RenderBlock(true, false, Array.Empty<RecommendedFilters.CloudSyncFolder>());
        var existing = RecommendedFilters.MergeIntoFilters(Header + "\r\n*.bak\r\n" + Footer, recommended).Replace("\r\n", "\n").Replace("\n", "\r\n");
        var rule = FolderExclusion.Rule(@"C:\Users\me\Downloads")!;
        var after = FolderExclusion.Add(existing, rule);
        Assert.Contains("\r\n" + rule + "\r\n", after);
        Assert.Equal(existing, FolderExclusion.Remove(after, rule));
        // A later edit is kept: only the rule goes.
        var edited = after.Replace("*.bak", "*.bak\r\n*.old");
        Assert.Equal(existing.Replace("*.bak", "*.bak\r\n*.old"), FolderExclusion.Remove(edited, rule));
        // A rule not there changes nothing.
        Assert.Equal(existing, FolderExclusion.Remove(existing, rule));
    }

    [Fact]
    public void Measure_counts_the_files_a_folder_holds_and_reports_a_missing_folder()
    {
        var root = Directory.CreateTempSubdirectory("bs-u178-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "a", "b"));
            File.WriteAllBytes(Path.Combine(root, "x.bin"), new byte[1000]);
            File.WriteAllBytes(Path.Combine(root, "a", "b", "y.bin"), new byte[500]);
            Assert.Equal((2L, 1500L), FolderExclusion.Measure(root, TimeSpan.FromSeconds(10), CancellationToken.None));
            Assert.Null(FolderExclusion.Measure(Path.Combine(root, "missing"), TimeSpan.FromSeconds(10), CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static int Count(string text, string value)
    {
        var n = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
