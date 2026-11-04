using System;
using System.Linq;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U106 (4 November 2026): "Are you 100% confident about all the other
/// filtering rules?" Rules meant for the top of a drive must never match a
/// person's own folder of the same name at the top of a backed-up folder,
/// and rules that can catch someone's own work are no longer recommended.
/// </summary>
public sealed class FilterScopeTests
{
    private static string[] Rules(string filters, string source)
    {
        var plan = ResticBackrestEngine.BuildPlanFromBackup("p", "r", new Backup { FiltersText = filters }, source);
        return plan.Excludes.Concat(plan.IExcludes).ToArray();
    }

    [Fact]
    public void Drive_top_rules_apply_to_a_whole_drive_but_not_to_a_folder()
    {
        if (!OperatingSystem.IsWindows()) return;
        var filters = "/Intel/\n/Windows.old/\n/Windows/\n/build/";
        var drive = Rules(filters, @"D:\");
        Assert.Contains("D:/Intel/", drive);
        Assert.Contains("D:/Windows.old/", drive);
        Assert.Contains("D:/System Volume Information/", drive); // always excluded on a drive

        // D:\Projects\Intel or D:\Projects\Windows are the person's own folders.
        var folder = Rules(filters, @"D:\Projects");
        Assert.DoesNotContain(folder, r => r.Contains("/Intel/") || r.Contains("/Windows/") || r.Contains("/Windows.old") || r.Contains("System Volume Information"));
        // A rule the person wrote is theirs, wherever the source is.
        Assert.Contains("D:/Projects/build/", folder);
    }

    [Fact]
    public void The_preview_and_savings_follow_the_same_scope()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal("/build/", FilterTranslator.ScopeToSource("/Intel/\n/build/", @"D:\Projects"));
        Assert.Equal("/Intel/\n/build/", FilterTranslator.ScopeToSource("/Intel/\n/build/", @"D:\"));
    }

    [Fact]
    public void Rules_that_can_catch_a_persons_own_work_are_not_recommended()
    {
        var recommended = RecommendedFilters.EssentialPatterns.Concat(RecommendedFilters.WindowsSystemPatterns)
            .Concat(RecommendedFilters.CachesAndJunkPatterns).Concat(RecommendedFilters.AlwaysExcludedPatterns).ToList();
        foreach (var risky in new[] { "temp/", "tmp/", "*.tmp", "*.temp", "*.dmp", "*.dump", "Users/*/AppData/*/Log/",
                     "Users/*/AppData/*/Logs/", "Users/*/AppData/Local/Packages/*/LocalCache/", "Users/*/MicrosoftEdgeBackups/",
                     "ntuser.dat*", "UsrClass.dat*", "/Recovery/", "Recovery/", "OneDriveTemp/" })
        {
            Assert.DoesNotContain(risky, recommended);
            // Earlier backups that have them are offered their removal.
            Assert.Contains(risky, RecommendedFilters.RetiredPatterns);
        }
        Assert.Contains("Users/*/AppData/Local/Temp/", recommended);
    }
}
