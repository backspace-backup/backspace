using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Backspace.Services;

/// <summary>
/// Converts Duplicacy filter lines into plain restic rules, once (U151,
/// 6 November 2026). The converted text replaces the backup's Filters and is
/// saved; from then on restic reads it unchanged, so the same file works with
/// restic alone. Runs when settings load, for each backup whose Filters still
/// hold Duplicacy lines, and when a Duplicacy setup is imported. Converted
/// text holds none, so a second run changes nothing.
///
/// <list type="bullet">
/// <item>Duplicacy lets the first matching line decide, restic the last, so
/// each uninterrupted group of <c>e:</c>/<c>i:</c> (and <c>-</c>/<c>+</c>)
/// lines is written in restic's order (<see cref="DuplicacyFilterTranslator"/>),
/// under a <c># was:</c> comment naming the line each rule came from.</item>
/// <item>A Duplicacy rule tied to the top of the backed-up folder (<c>^X</c>,
/// or a Backspace rule starting with <c>/</c>) becomes a rule from the top of
/// the drive: <c>?:/Users/Erwin/Dropbox/X</c> for C:\Users\Erwin\Dropbox
/// (<c>?:</c> matches any drive: only a drive name holds ':'). On Linux and
/// macOS the folder's full path is used.</item>
/// <item>Rules of Backspace's recommended block move to the top of the drive
/// (<c>/Windows/</c> → <c>?:/Windows/</c>, <c>Users/*/…</c> → <c>?:/Users/*/…</c>,
/// four generic cache names under the profiles' AppData); the others still
/// match at any depth.</item>
/// <item>A literal <c>$</c> is written <c>$$</c>: restic expands <c>$NAME</c> in
/// an exclude file.</item>
/// <item>The essential exclusions are written as a visible block at the end.</item>
/// <item>Comments outside the recommended block are dropped: they described
/// the Duplicacy lines.</item>
/// </list>
/// </summary>
internal static class DuplicacyFilterConverter
{
    internal const string RunHeader1 = "# Your rules, converted from Duplicacy. restic lets the last matching rule decide,";
    internal const string RunHeader2 = "# so a rule starting with ! comes after the rules it overrides.";

    /// <summary>The first line Backspace wrote above imported Duplicacy filters
    /// before U151: a text with it may also hold <c>+</c>/<c>-</c> lines.</summary>
    internal const string LegacyImportNote = "# Imported from Duplicacy.";

    private static bool IsRegexLine(string line) => DuplicacyFilterTranslator.IsDuplicacyLine(line);
    private static bool IsWildcardLine(string line) => line.Length > 1 && line[0] is '+' or '-';

    /// <summary>True when <paramref name="text"/> still holds Duplicacy lines.</summary>
    internal static bool NeedsConversion(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var lines = text.Split('\n').Select(l => l.Trim()).ToList();
        return lines.Any(IsRegexLine)
            || (lines.Any(l => l.StartsWith(LegacyImportNote, StringComparison.Ordinal)) && lines.Any(IsWildcardLine));
    }

    /// <summary>Converts every backup whose Filters hold Duplicacy lines.
    /// Returns the names of the backups changed.</summary>
    internal static IReadOnlyList<string> ConvertStored(Models.AppConfig config)
    {
        var changed = new List<string>();
        foreach (var backup in config.Backups)
        {
            if (!NeedsConversion(backup.FiltersText)) continue;
            backup.FiltersText = Convert(backup.FiltersText, backup.SourcePaths);
            changed.Add(backup.Name);
        }
        return changed;
    }

    /// <summary>The restic text for <paramref name="text"/>, a backup's
    /// Filters as Backspace kept them before U151, for a backup of
    /// <paramref name="sources"/>.</summary>
    /// <param name="wildcardLines">Whether lines starting with <c>+</c> or
    /// <c>-</c> are Duplicacy's wildcard rules. Null: only in text Backspace
    /// imported from Duplicacy. In restic, such a line is a file name.</param>
    internal static string Convert(string? text, IReadOnlyCollection<string>? sources, bool? wildcardLines = null)
    {
        var lines = (text ?? "").Split('\n').Select(l => l.Trim()).ToList();
        wildcardLines ??= lines.Any(l => l.StartsWith(LegacyImportNote, StringComparison.Ordinal));
        var prefixes = Prefixes(sources);
        var windows = WindowsStyle(sources);

        var recStart = lines.FindIndex(l => l == RecommendedFilters.AutoBlockHeader);
        var recEnd = recStart < 0 ? -1 : lines.FindIndex(recStart + 1, l => l == RecommendedFilters.AutoBlockFooter);
        if (recEnd < 0) recStart = -1;
        var always = RecommendedFilters.FindAlwaysBlock(lines);

        var file = new List<string>();
        var recommended = new List<string>();
        var run = new List<DuplicacyFilterTranslator.Line>();

        void Flush()
        {
            if (run.Count == 0) return;
            file.AddRange(ConvertRun(run, prefixes));
            file.Add("");
            run.Clear();
        }

        for (var k = 0; k < lines.Count; k++)
        {
            var line = lines[k];
            if (always is { } a && k >= a.Start && k <= a.End) continue;
            var inRecommended = recStart >= 0 && k >= recStart && k <= recEnd;
            if (line.Length == 0 || line[0] == '#')
            {
                if (inRecommended) recommended.Add(line);
                continue;
            }
            if (IsRegexLine(line)) { run.Add(DuplicacyFilterTranslator.Parse(line)); continue; }
            if (wildcardLines == true && IsWildcardLine(line)) { run.Add(ParseWildcard(line)); continue; }
            Flush();
            if (inRecommended) recommended.Add(RecommendedFilters.LegacyToRestic(line, windows));
            else file.AddRange(Anchor(line, prefixes));
        }
        Flush();

        var output = new List<string>(file);
        output.AddRange(recommended);
        if (RecommendedFilters.AlwaysExcludedPatterns.Count > 0)
        {
            if (output.Count > 0) output.Add("");
            output.AddRange(RecommendedFilters.AlwaysBlockLines());
        }
        return string.Join("\n", output) + "\n";
    }

    /// <summary>One group of Duplicacy lines in restic's order, each rule
    /// under a <c># was:</c> comment naming its line.</summary>
    private static List<string> ConvertRun(List<DuplicacyFilterTranslator.Line> run, IReadOnlyList<string> prefixes)
    {
        var untranslated = new List<string>();
        var rules = DuplicacyFilterTranslator.TranslateRun(run, untranslated);
        // The rules each line stands for on its own; a rule restic needs to
        // keep a folder empty around an include belongs to the include.
        var own = run.Select(l => (Line: l, Globs: Own(l))).ToList();
        var output = new List<string> { RunHeader1, RunHeader2 };
        DuplicacyFilterTranslator.Line? last = null;
        foreach (var rule in rules)
        {
            var text = (rule.Negated ? "!" : "") + rule.Glob;
            var owner = own.FirstOrDefault(o => o.Globs.Contains(text)).Line
                ?? own.FirstOrDefault(o => o.Line.Include && o.Globs.Any(g => text == g[1..] + "/*")).Line;
            if (owner is not null && !ReferenceEquals(owner, last)) { output.Add("# was: " + owner.Source); last = owner; }
            output.AddRange(Anchor(text, prefixes));
        }
        foreach (var line in untranslated)
        {
            output.Add("# was: " + line);
            output.Add("# Not converted: no restic rule means the same, so this line is left out.");
        }
        return output;
    }

    private static List<string> Own(DuplicacyFilterTranslator.Line line)
    {
        var globs = DuplicacyFilterTranslator.ToGlobs(line.Regex);
        if (globs is null) return new();
        return line.Include
            ? globs.Select(g => "!" + g.Glob.TrimEnd('/')).ToList()
            : globs.Select(g => g.Glob.EndsWith('/') ? g.Glob + "*" : g.Glob).ToList();
    }

    /// <summary>Duplicacy's <c>+</c>/<c>-</c> wildcard lines match the whole
    /// path from the top of the backed-up folder; <c>*</c> may cross folders.</summary>
    private static DuplicacyFilterTranslator.Line ParseWildcard(string line)
    {
        var regex = new StringBuilder("^");
        foreach (var c in line[1..])
        {
            if (c == '*') regex.Append(".*");
            else if (c == '?') regex.Append('.');
            else if (c is '(' or ')' or '[' or ']' or '\\' or '.' or '^' or '$' or '{' or '}' or '|' or '+') regex.Append('\\').Append(c);
            else regex.Append(c);
        }
        regex.Append('$');
        return new(line[0] == '+', false, regex.ToString(), line);
    }

    /// <summary>A rule tied to the top of the backed-up folder (<c>/X</c>)
    /// written from the top of the drive, once for each folder; any other
    /// rule unchanged. A literal <c>$</c> becomes <c>$$</c>.</summary>
    internal static IEnumerable<string> Anchor(string glob, IReadOnlyList<string> prefixes)
    {
        var negated = glob.StartsWith('!');
        var body = negated ? glob[1..] : glob;
        if (!body.StartsWith('/')) return new[] { Dollars(glob) };
        return prefixes.Select(p => Dollars((negated ? "!" : "") + p + body)).Distinct(StringComparer.Ordinal);
    }

    private static string Dollars(string rule) => rule.Replace("$", "$$");

    /// <summary>Where the backed-up folders start, as a restic rule writes it:
    /// <c>?:</c> for C:\, <c>?:/Users/Erwin/Dropbox</c> for that folder,
    /// <c>/home/me</c> on Linux. Glob characters in the names are escaped.</summary>
    internal static IReadOnlyList<string> Prefixes(IReadOnlyCollection<string>? sources)
    {
        var result = new List<string>();
        foreach (var source in sources ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            var prefix = Prefix(source.Trim());
            if (!result.Contains(prefix, StringComparer.OrdinalIgnoreCase)) result.Add(prefix);
        }
        if (result.Count == 0) result.Add(Platform.PlatformInfo.IsWindows ? "?:" : "");
        return result;
    }

    private static string Prefix(string source)
    {
        var path = source.Replace('\\', '/').TrimEnd('/');
        if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
            return "?:" + EscapeNames(path[2..]);
        if (path.StartsWith("//", StringComparison.Ordinal))
            return "//" + EscapeNames(path[2..]);
        return EscapeNames(path);
    }

    /// <summary>Folder names as literal text in a restic rule.</summary>
    internal static string EscapeNames(string path)
    {
        var sb = new StringBuilder(path.Length);
        foreach (var c in path)
            sb.Append(c is '[' or '*' or '?' ? $"[{c}]" : c.ToString());
        return sb.ToString();
    }

    private static bool WindowsStyle(IReadOnlyCollection<string>? sources)
    {
        var real = sources?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (real is null || real.Count == 0) return Platform.PlatformInfo.IsWindows;
        return real.Any(s => s.Trim().Length >= 2 && s.Trim()[1] == ':' || s.StartsWith(@"\\", StringComparison.Ordinal));
    }
}
