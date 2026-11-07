using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Imported Duplicacy filters must back up exactly what Duplicacy did (U054).
/// Each case compares the converted restic rules (U151) with Duplicacy's own
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
        // "^Users/" is the top of the backup's folder only, as in Duplicacy: an old PC's copied profiles deeper down are kept.
        "Backups/OldPC/Users/bob/AppData/Local/Cache/x", "Backups/OldPC/Users/bob/AppData/Local/Google/Chrome/User Data/Default/History",
    };

    /// <summary>The converted text (U151), as restic reads it on a backup of C:\.</summary>
    private static string Converted => DuplicacyFilterConverter.Convert(Filters, new[] { @"C:\" });

    private static List<FilterTranslator.CompiledRule> Rules(string text) => FilterTranslator.CompileRules(text, ignoreCase: true);

    private static bool ExcludedOnC(List<FilterTranslator.CompiledRule> rules, string relative)
        => FilterTranslator.Evaluate(rules, "C:/" + relative).Excluded;

    [Fact]
    public void Converted_rules_keep_Duplicacys_decision_for_every_path()
    {
        var converted = Converted;
        Assert.DoesNotContain("# Not converted", converted);
        var rules = Rules(converted);
        var reference = DuplicacyReference.Parse(Filters);
        // Restic cannot tell a file from a folder: a file lying directly in a
        // folder that Duplicacy keeps only as a folder is kept too. And one
        // list ignores case or not: on Windows the e:\.*DS_Store line, which
        // has no (?i), ignores case too; and "/node_modules/" (below some
        // folder) becomes */**/node_modules/*, where the drive counts as that
        // folder (both approved 6 November 2026).
        var knownRestic = new HashSet<string> { "Program Files/desktop.ini", "Mac/.ds_store", "node_modules/x.js" };
        var differences = Paths.Where(p => !knownRestic.Contains(p))
            .Where(p => Included(p, path => !reference.Included(path)) != Included(p, path => ExcludedOnC(rules, path)))
            .Select(p => $"{p}: Duplicacy {(Included(p, path => !reference.Included(path)) ? "keeps" : "skips")}")
            .ToList();
        Assert.True(differences.Count == 0, string.Join("\n", differences) + "\n\n" + converted);
    }

    /// <summary>A backup of a folder: "^X" is X at the top of that folder,
    /// written from the top of the drive with the folder's path.</summary>
    [Fact]
    public void Converted_rules_tied_to_a_backed_up_folder_keep_Duplicacys_decision()
    {
        var anchored = string.Join("\n", Filters.Split('\n').Select(l => l.Trim())
            .Where(l => l.Contains('^') && !l.Contains("(^|/)", StringComparison.Ordinal)));
        var rules = Rules(DuplicacyFilterConverter.Convert(anchored, new[] { @"C:\Users\me\Dropbox" }));
        var reference = DuplicacyReference.Parse(anchored);
        var differences = Paths.Where(p => p != "Program Files/desktop.ini")
            .Where(p => Included(p, path => !reference.Included(path))
                != Included(p, path => FilterTranslator.Evaluate(rules, "C:/Users/me/Dropbox/" + path).Excluded))
            .ToList();
        Assert.Empty(differences);
        // The same names elsewhere on the drive are not this backup's.
        Assert.False(FilterTranslator.Evaluate(rules, "C:/Windows/System32/x.dll").Excluded);
    }

    [Theory]
    [InlineData("Users/me/AppData/Local/Google/Chrome/User Data/Default/Local Extension Settings/ext/000003.log", true)]
    [InlineData("Users/me/AppData/Local/Google/Chrome/User Data/Default/History", false)]
    [InlineData("Users/me/AppData/Local/Google/Chrome/Application/chrome.exe", false)]
    [InlineData("Program Files/App1/bin/lib.dll", false)]
    [InlineData("Tools/Python311/python.exe", true)]
    [InlineData("Games/Worms/windows/setup.exe", true)]
    [InlineData("Users/me/AppData/Local/Cache/x", false)]
    [InlineData("Backups/OldPC/Users/bob/AppData/Local/Cache/x", true)]
    public void The_regressions_found_on_the_maintainers_drives_are_fixed(string path, bool backedUp)
    {
        var rules = Rules(Converted);
        Assert.Equal(backedUp, Included(path, p => ExcludedOnC(rules, p)));
    }

    /// <summary>U151: a Duplicacy rule "^Users/…" becomes a rule from the top
    /// of the drive, as Duplicacy meant it for a backup of C:\.</summary>
    [Fact]
    public void Anchored_duplicacy_rules_bind_to_the_top_of_the_drive()
    {
        var text = DuplicacyFilterConverter.Convert("e:(?i)^Users/[^/]+/AppData/Local/Temp/", new[] { @"C:\" });
        Assert.Contains("?:/Users/*/AppData/Local/Temp/*", text.Split('\n'));
        var rules = Rules(text);
        Assert.True(FilterTranslator.Evaluate(rules, "C:/Users/me/AppData/Local/Temp/x").Excluded);
        Assert.False(FilterTranslator.Evaluate(rules, "C:/Backups/OldPC/Users/bob/AppData/Local/Temp/x").Excluded);
    }

    [Fact]
    public void An_exclude_before_an_include_still_wins_and_after_it_does_not()
    {
        var first = Rules(DuplicacyFilterConverter.Convert("e:(?i)\\.tmp$\ni:(?i)^keep\\.tmp$", new[] { @"C:\" }));
        Assert.True(FilterTranslator.Evaluate(first, "C:/keep.tmp").Excluded);
        var second = Rules(DuplicacyFilterConverter.Convert("i:(?i)^keep\\.tmp$\ne:(?i)\\.tmp$", new[] { @"C:\" }));
        Assert.False(FilterTranslator.Evaluate(second, "C:/keep.tmp").Excluded);
        Assert.True(FilterTranslator.Evaluate(second, "C:/other.tmp").Excluded);
    }

    /// <summary>U152: an imported rule built to explode cannot hang backups.</summary>
    [Fact]
    public void Pathological_rules_finish_quickly()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var lots = "e:x" + string.Concat(Enumerable.Repeat("a.*", 30)) + "b";
        Assert.Null(DuplicacyFilterTranslator.ToGlobs(lots[2..]));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"translation took {watch.Elapsed}");

        var rules = FilterTranslator.CompileRules("*a*a*a*a*a*a*a*a*a*a*a*a*b");
        watch.Restart();
        var result = FilterTranslator.Evaluate(rules, new string('a', 5000));
        Assert.False(result.Excluded);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"matching took {watch.Elapsed}");
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
