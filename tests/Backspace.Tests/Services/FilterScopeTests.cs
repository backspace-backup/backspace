using System;
using System.IO;
using System.Linq;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U106 (4 November 2026): "Are you 100% confident about all the other
/// filtering rules?" Rules meant for the top of a drive must never match a
/// person's own folder of the same name deeper down, and rules that can catch
/// someone's own work are no longer recommended. Since U151 (6 November 2026)
/// such rules say so themselves (?:/Intel/), and restic gets the text as is.
/// </summary>
public sealed class FilterScopeTests
{
    [Fact]
    public void The_engine_hands_restic_the_filters_text_unchanged_as_an_exclude_file()
    {
        using var workspace = new Backspace.Tests.TestHelpers.TempWorkspace("u151-plan");
        var filters = "?:/Intel/\r\n/build/\n# note\n*.TMP\n?:/$$Recycle.Bin/\n";
        var backup = new Backup { FiltersText = filters };
        var trailing = ResticBackrestEngine.BackupTrailing(backup, "plan-a", null);
        // No rules as arguments: they would mean something else ($ is literal there).
        Assert.DoesNotContain(trailing, t => t.Contains("Intel", StringComparison.Ordinal) || t.Contains("build", StringComparison.Ordinal));
        var file = ResticBackrestEngine.ExcludeFilePath("plan-a");
        var at = trailing.IndexOf(FilterTranslator.ExcludeFileFlag);
        Assert.True(at >= 0, string.Join(" ", trailing));
        // One argument, unquoted: restic gets it as it is.
        Assert.Equal(file, trailing[at + 1]);
        Assert.StartsWith(EngineWorkPaths.Root, file, StringComparison.OrdinalIgnoreCase);

        ResticBackrestEngine.WriteExcludeFile("plan-a", filters);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(filters), File.ReadAllBytes(file));
        // Rewritten when the text changes.
        ResticBackrestEngine.WriteExcludeFile("plan-a", "*.bak");
        Assert.Equal("*.bak", File.ReadAllText(file));
    }

    [Fact]
    public void Drive_top_rules_match_the_top_of_a_drive_and_not_a_folder_of_the_same_name_below()
    {
        var rules = FilterTranslator.CompileRules("?:/Intel/\n?:/Windows/\n?:/Windows.old/", ignoreCase: true);
        Assert.True(FilterTranslator.Evaluate(rules, "D:/Intel/x").Excluded);
        Assert.True(FilterTranslator.Evaluate(rules, "C:/Windows/System32/x").Excluded);
        Assert.False(FilterTranslator.Evaluate(rules, "D:/Projects/Intel/x").Excluded);
        Assert.False(FilterTranslator.Evaluate(rules, "D:/Projects/Windows/x").Excluded);
    }

    [Fact]
    public void Rules_that_can_catch_a_persons_own_work_are_not_recommended()
    {
        var recommended = RecommendedFilters.EssentialPatterns.Concat(RecommendedFilters.WindowsSystemPatterns)
            .Concat(RecommendedFilters.CachesAndJunkPatterns).ToList();
        foreach (var risky in new[] { "temp/", "tmp/", "*.dmp", "*.dump", "Users/*/AppData/*/Log/",
                     "Users/*/AppData/*/Logs/", "Users/*/AppData/Local/Packages/*/LocalCache/", "Users/*/MicrosoftEdgeBackups/",
                     "ntuser.dat*", "UsrClass.dat*", "/Recovery/", "Recovery/", "OneDriveTemp/",
                     "?:/Users/*/AppData/*/Log/", "?:/Recovery/", "?:/Users/*/MicrosoftEdgeBackups/" })
        {
            Assert.DoesNotContain(risky, recommended);
            // Earlier backups that have them, as written then or as converted, are offered their removal.
            Assert.Contains(risky, RecommendedFilters.RetiredPatterns);
        }
        if (!OperatingSystem.IsWindows()) return;
        Assert.Contains("?:/Users/*/AppData/Local/Temp/", recommended);
        // Offered again at any depth (U151).
        Assert.Contains("*.tmp", recommended);
        Assert.Contains("*.temp", recommended);
        Assert.DoesNotContain("*.tmp", RecommendedFilters.RetiredPatterns);
    }
}
