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
    // Shapes taken from local_c's imported rules and Backspace's recommendations.
    private const string Rules = """
        e:(?i)/node_modules/
        e:(?i)^Users/[^/]+/AppData/[^/]+/Google/Chrome/
        i:(?i)^Program Files[^/]*/$
        i:(?i)^Program Files[^/]*/[^/]+/$
        e:(?i)^Program Files[^/]*/
        e:(?i)\$RECYCLE\.BIN/
        e:(?i)(^|/)IconCache\.db
        e:(?i)^Users/[^/]+/Dropbox/
        e:\.*DS_Store
        /pagefile.sys
        OneDriveTemp/
        Users/*/AppData/**/Code Cache/
        __pycache__/
        *.tmp
        UsrClass.dat*
        cache/
        !cache/keep/
        Users/*/.nuget/packages/
        [Tt]emp?/
        **/Logs/CrashReporter/
        """;

    private static readonly string[] Paths =
    [
        "Users", "Users/me", "Users/me/AppData", "Users/me/AppData/Local/Google/Chrome", "Users/me/AppData/Local/Google/Chrome/x.db",
        "Users/ME/APPDATA/local/GOOGLE/chrome/User Data/x", "Users/me/code/app/node_modules", "Users/me/code/app/node_modules/lib.js",
        "Users/me/code/app/NODE_MODULES/x", "Program Files", "Program Files/App", "Program Files/App/app.exe", "Program Files (x86)/A/b/c.dll",
        "$RECYCLE.BIN", "$Recycle.Bin/S-1/x", "Users/me/AppData/Local/IconCache.db", "iconcache.db", "Users/me/Dropbox", "Users/me/Dropbox/a.mp4",
        "Users/me/Dropbox.txt", "a/.DS_Store", "a/DS_Store", "pagefile.sys", "x/pagefile.sys", "OneDriveTemp", "Users/me/OneDriveTemp/f",
        "Users/me/AppData/Roaming/Code/Code Cache/js/x", "Users/me/AppData/Roaming/Code/Code Cache", "src/__pycache__/m.pyc", "a/b.tmp", "a/b.TMP",
        "Users/me/AppData/Local/Microsoft/Windows/UsrClass.dat.LOG1", "app/cache/x", "app/cache/keep/y", "app/Cache/x", "Users/me/.nuget/packages/a/b",
        "Temp1/x", "temp2", "TempAB/x", "x/Logs/CrashReporter/r.txt", "Logs/CrashReporter", "Users/me/Documents/taxes.pdf", "", "a",
    ];

    [Fact]
    public void Skipping_rules_whose_text_is_missing_never_changes_a_decision()
    {
        var fast = FilterTranslator.CompileRules(Rules);
        Assert.Contains(fast, r => r.Required is not null);
        var slow = fast.Select(r => r with { Required = null }).ToList();
        foreach (var path in Paths)
            foreach (var form in new[] { path, path + "/" })
                Assert.Equal(FilterTranslator.Evaluate(slow, form), FilterTranslator.Evaluate(fast, form));
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
