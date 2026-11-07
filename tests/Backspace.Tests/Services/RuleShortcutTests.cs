using System.Collections.Generic;
using System.Linq;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U123 (4 November 2026): two thirds of the time to count local_c's size went
/// into testing every path against its 270 imported rules. A rule is now tried
/// only on paths containing the plain text it requires. These tests check the
/// shortcut never changes a decision.
/// </summary>
public sealed class RuleShortcutTests
{
    // Shapes taken from local_c's converted rules and Backspace's recommendations (U151).
    private const string Rules = """
        */**/node_modules/*
        ?:/Users/*/AppData/*/Google/Chrome/*
        !?:/Program Files*
        ?:/Program Files*/*
        !?:/Program Files*/*
        ?:/Program Files*/*/*
        *$$RECYCLE.BIN/*
        IconCache.db*
        ?:/Users/*/Dropbox/*
        *DS_Store*
        ?:/pagefile.sys
        OneDriveTemp/
        ?:/Users/*/AppData/**/Code Cache/
        __pycache__/
        *.tmp
        UsrClass.dat*
        cache/
        !cache/keep/
        ?:/Users/*/.nuget/packages/
        [Tt]emp?/
        **/Logs/CrashReporter/
        /home/*/.cache/
        """;

    private static readonly string[] Paths =
    [
        "C:/Users", "C:/Users/me", "C:/Users/me/AppData", "C:/Users/me/AppData/Local/Google/Chrome", "C:/Users/me/AppData/Local/Google/Chrome/x.db",
        "C:/Users/ME/APPDATA/local/GOOGLE/chrome/User Data/x", "C:/Users/me/code/app/node_modules", "C:/Users/me/code/app/node_modules/lib.js",
        "C:/Users/me/code/app/NODE_MODULES/x", "C:/Program Files", "C:/Program Files/App", "C:/Program Files/App/app.exe", "C:/Program Files (x86)/A/b/c.dll",
        "C:/$RECYCLE.BIN", "C:/$Recycle.Bin/S-1/x", "C:/Users/me/AppData/Local/IconCache.db", "C:/iconcache.db", "C:/Users/me/Dropbox", "C:/Users/me/Dropbox/a.mp4",
        "C:/Users/me/Dropbox.txt", "C:/a/.DS_Store", "C:/a/DS_Store", "C:/pagefile.sys", "C:/x/pagefile.sys", "C:/OneDriveTemp", "C:/Users/me/OneDriveTemp/f",
        "C:/Users/me/AppData/Roaming/Code/Code Cache/js/x", "C:/Users/me/AppData/Roaming/Code/Code Cache", "C:/src/__pycache__/m.pyc", "C:/a/b.tmp", "C:/a/b.TMP",
        "C:/Users/me/AppData/Local/Microsoft/Windows/UsrClass.dat.LOG1", "C:/app/cache/x", "C:/app/cache/keep/y", "C:/app/Cache/x", "C:/Users/me/.nuget/packages/a/b",
        "C:/Temp1/x", "C:/temp2", "C:/TempAB/x", "C:/x/Logs/CrashReporter/r.txt", "C:/Logs/CrashReporter", "C:/Users/me/Documents/taxes.pdf", "C:/", "C:/a", "/home/me/.cache/x", "/data/home/me/.cache",
    ];

    [Fact]
    public void Skipping_rules_whose_text_is_missing_never_changes_a_decision()
    {
        foreach (var ignoreCase in new[] { true, false })
        {
            var fast = FilterTranslator.CompileRules(Rules, ignoreCase);
            Assert.Contains(fast, r => r.Required is not null);
            var slow = fast.Select(r => r with { Required = null }).ToList();
            foreach (var path in Paths)
                foreach (var form in new[] { path, path + "/" })
                    Assert.Equal(FilterTranslator.Evaluate(slow, form), FilterTranslator.Evaluate(fast, form));
            // The decisions themselves, so the comparison above is not of two no-ops.
            Assert.True(FilterTranslator.Evaluate(fast, "C:/Users/me/Dropbox/a.mp4").Excluded);
            Assert.False(FilterTranslator.Evaluate(fast, "C:/Users/me/Dropbox.txt").Excluded);
            Assert.True(FilterTranslator.Evaluate(fast, "C:/$Recycle.Bin/S-1/x").Excluded == ignoreCase);
            Assert.False(FilterTranslator.Evaluate(fast, "C:/Program Files/App").Excluded);
            Assert.True(FilterTranslator.Evaluate(fast, "C:/Program Files/App/app.exe").Excluded);
            Assert.True(FilterTranslator.Evaluate(fast, "/home/me/.cache/x").Excluded);
        }
    }

    [Theory]
    [InlineData("/Users/*/AppData/*/Google/Chrome/*", "Google/Chrome")]
    [InlineData("*/**/AppData/Local/Packages/*", "AppData/Local/Packages")]
    [InlineData("OneDriveTemp/", "OneDriveTemp")]
    [InlineData("*.tmp", ".tmp")]
    [InlineData("[Tt]emp?/", "emp")]
    [InlineData("*", null)]
    [InlineData("a*", null)] // too short to be worth it
    [InlineData("Ünïcode/*", null)] // case rules beyond ASCII are left to the pattern
    public void The_required_text_is_the_longest_plain_run(string glob, string? expected)
        => Assert.Equal(expected, FilterTranslator.RequiredText(glob));
}
