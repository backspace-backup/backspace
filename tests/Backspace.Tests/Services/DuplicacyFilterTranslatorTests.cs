using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Imported Duplicacy filters must back up exactly what Duplicacy did (U054).
/// Each case compares the translated restic rules with Duplicacy's own
/// first-match rule (MatchPath in duplicacy_utils.go) on the same paths,
/// skipping below excluded folders as both engines do. The 31 October 2026
/// dry run on the maintainer's C: drive found the earlier translation backing
/// up 1,127,269 files where Duplicacy backed up 767,869.
/// </summary>
public sealed class DuplicacyFilterTranslatorTests
{
    // A representative file in the style of a long-lived duplicacy-util setup:
    // anchored, anywhere and substring rules, optional letters, alternatives,
    // ".*" spanning folders, and include chains that keep one folder inside
    // an excluded one.
    private const string Filters = """
        e:(?i)\.(tmp|temp)$
        e:(?i)(^|/)temp/
        e:(?i)/node_modules/
        e:(?i)\$RECYCLE\.BIN/
        e:(?i)^\$Windows\.\~BT/
        e:(?i)^Documents and Settings
        e:(?i)^Windows/
        e:(?i)^pagefile\.sys$
        e:(?i)ntuser\.dat
        e:(?i)(^|/)IconCache\.db
        i:(?i)^Program Files[^/]*/$
        i:(?i)^Program Files[^/]*/[^/]+/$
        e:(?i)^Program Files[^/]*/
        e:(?i)^Users/[^/]+/AppData/[^/]+/Caches?/
        e:(?i)^Users/[^/]+/AppData/[^/]+/.*/Caches?/
        e:(?i)^Users/[^/]+/AppData/[^/]+/.*crashlogs?/
        i:(?i)^Users/[^/]+/AppData/[^/]+/Google/Chrome/$
        i:(?i)^Users/[^/]+/AppData/[^/]+/Google/Chrome/User Data/$
        i:(?i)^Users/[^/]+/AppData/[^/]+/Google/Chrome/User Data/[^/]+/$
        i:(?i)^Users/[^/]+/AppData/[^/]+/Google/Chrome/User Data/[^/]+/Local Extension Settings/
        e:(?i)^Users/[^/]+/AppData/[^/]+/Google/Chrome/
        e:(?i)^Users/[^/]+/AppData/[^/]+/Microsoft/Windows/.*\.dat.LOG1$
        e:(?i)^Users/[^/]+/AppData/[^/]+/Microsoft/Office/.*/[^/]*Packages/
        e:(?i)^ProgramData/GOG.com/
        e:(?i)^Python[^/]*/
        e:\.*DS_Store
        e:(?i)^Users/[^/]+/Projects/cronjobs*
        """;

    private static readonly string[] Paths =
    {
        "notes.txt", "a/b/report.TMP", "a/b/report.temp", "a/b/template.txt", "Temp/x.txt", "a/temp/x.txt", "a/tempest/x.txt",
        "node_modules/x.js", "app/node_modules/x.js", "$RECYCLE.BIN/S-1/x", "Data/$Recycle.Bin/x", "$Windows.~BT/x",
        "Documents and Settings/x", "Documents and SettingsOld/x", "Windows/System32/x.dll", "Games/Worms/windows/setup.exe",
        "pagefile.sys", "Backups/pagefile.sys", "Users/me/NTUSER.DAT", "Users/me/ntuser.dat.LOG1", "Users/me/IconCache.db-wal",
        "Program Files/desktop.ini", "Program Files/App1/app.exe", "Program Files/App1/bin/lib.dll", "Program Files (x86)/App2/a.exe",
        "Users/me/AppData/Local/Cache/x", "Users/me/AppData/Local/Caches/x", "Users/me/AppData/Local/Vendor/App/Cache/x",
        "Users/me/AppData/Local/Vendor/App/Caches/x", "Users/me/AppData/Local/CacheKeeper/x", "Users/me/AppData/Roaming/App/crashlogs/c.txt",
        "Users/me/AppData/Roaming/App/sub/AppCrashlog/c.txt", "Users/me/AppData/Roaming/App/crashlogger.txt",
        "Users/me/AppData/Local/Google/Chrome/Application/chrome.exe", "Users/me/AppData/Local/Google/Chrome/User Data/Default/History",
        "Users/me/AppData/Local/Google/Chrome/User Data/Default/Local Extension Settings/ext/000003.log",
        "Users/me/AppData/Local/Google/Chrome/User Data/Profile 2/Local Extension Settings/ext/LOCK",
        "Users/me/AppData/Local/Google/Chrome/User Data/Default/Local Extension Settings/ext/Cache/x",
        "Users/me/AppData/Local/Microsoft/Windows/UsrClass.dat.LOG1", "Users/me/AppData/Local/Microsoft/Windows/a/b/x.dat-LOG1",
        "Users/me/AppData/Local/Microsoft/Windows/settings.dat", "Users/me/AppData/Local/Microsoft/Office/16.0/Wef/WebServicePackages/x",
        "Users/me/AppData/Local/Microsoft/Office/SolutionPackages/x", "ProgramData/GOG.com/x", "ProgramData/GOGXcom/x",
        "Python311/python.exe", "Tools/Python311/python.exe", "Mac/.DS_Store", "Mac/._.DS_Store", "Mac/.ds_store",
        "Users/me/Projects/cronjob.ps1", "Users/me/Projects/cronjobs/run.ps1", "Users/me/Projects/cron/x",
    };

    [Fact]
    public void Translated_rules_keep_Duplicacys_decision_for_every_path()
    {
        var untranslated = new List<string>();
        var translated = FilterTranslator.Translate(Filters, defaultsToCaseInsensitive: true, untranslated);
        Assert.Empty(untranslated);
        var rules = FilterTranslator.CompileRules(Filters);
        var reference = DuplicacyReference.Parse(Filters);
        // Restic cannot tell a file from a folder: a file lying directly in a
        // folder that Duplicacy keeps only as a folder is kept too.
        var knownRestic = new HashSet<string> { "Program Files/desktop.ini" };
        var differences = Paths.Where(p => !knownRestic.Contains(p))
            .Where(p => Included(p, path => !reference.Included(path)) != Included(p, path => FilterTranslator.Evaluate(rules, path).Excluded))
            .Select(p => $"{p}: Duplicacy {(Included(p, path => !reference.Included(path)) ? "keeps" : "skips")}")
            .ToList();
        Assert.True(differences.Count == 0, string.Join("\n", differences) + "\n\n" + string.Join("\n", translated.IExcludes));
    }

    [Theory]
    [InlineData("Users/me/AppData/Local/Google/Chrome/User Data/Default/Local Extension Settings/ext/000003.log", true)]
    [InlineData("Users/me/AppData/Local/Google/Chrome/User Data/Default/History", false)]
    [InlineData("Users/me/AppData/Local/Google/Chrome/Application/chrome.exe", false)]
    [InlineData("Program Files/App1/bin/lib.dll", false)]
    [InlineData("Tools/Python311/python.exe", true)]
    [InlineData("Games/Worms/windows/setup.exe", true)]
    public void The_regressions_found_on_the_maintainers_drives_are_fixed(string path, bool backedUp)
    {
        var rules = FilterTranslator.CompileRules(Filters);
        Assert.Equal(backedUp, Included(path, p => FilterTranslator.Evaluate(rules, p).Excluded));
    }

    [Fact]
    public void An_exclude_before_an_include_still_wins_and_after_it_does_not()
    {
        var first = FilterTranslator.CompileRules("e:(?i)\\.tmp$\ni:(?i)^keep\\.tmp$");
        Assert.True(FilterTranslator.Evaluate(first, "keep.tmp").Excluded);
        var second = FilterTranslator.CompileRules("i:(?i)^keep\\.tmp$\ne:(?i)\\.tmp$");
        Assert.False(FilterTranslator.Evaluate(second, "keep.tmp").Excluded);
        Assert.True(FilterTranslator.Evaluate(second, "other.tmp").Excluded);
    }

    [Theory]
    [InlineData(@"\d+")]
    [InlineData(@"^a{2}/")]
    [InlineData(@"(?=x)y")]
    [InlineData(@"^a+b/")]
    public void Regexes_without_an_exact_glob_are_refused(string regex)
        => Assert.Null(DuplicacyFilterTranslator.ToGlobs(regex));

    /// <summary>A path is backed up when neither it nor any folder above it
    /// is excluded: both engines skip an excluded folder entirely.</summary>
    private static bool Included(string path, Func<string, bool> excluded)
    {
        var parts = path.Split('/');
        for (var i = 1; i <= parts.Length; i++)
        {
            var candidate = string.Join('/', parts.Take(i)) + (i < parts.Length ? "/" : "");
            if (excluded(candidate)) return false;
        }
        return true;
    }

    /// <summary>Duplicacy's MatchPath: the first matching pattern decides;
    /// with no match a path is kept unless every pattern is an include.</summary>
    private sealed class DuplicacyReference
    {
        private readonly List<(bool include, Regex regex)> _patterns = new();
        private bool _allIncludes = true;

        public static DuplicacyReference Parse(string text)
        {
            var r = new DuplicacyReference();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                r._patterns.Add((line[0] == 'i', new Regex(line[2..], RegexOptions.CultureInvariant)));
                if (line[0] == 'e') r._allIncludes = false;
            }
            return r;
        }

        public bool Included(string path)
        {
            foreach (var (include, regex) in _patterns)
                if (regex.IsMatch(path)) return include;
            return !_allIncludes;
        }
    }
}
