using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Backspace.Services;

/// <summary>
/// Reads a backup's Filters text the way restic reads an exclude file, so
/// Count, Simulate and the size estimates decide exactly as the backup does
/// (U151, 6 November 2026).
///
/// The Filters text IS the exclude file: the engine hands it to restic
/// unchanged with <c>--iexclude-file</c> on Windows (names ignore case) and
/// <c>--exclude-file</c> on Linux and macOS. That flag is the only thing
/// Backspace decides. Here, as in restic:
///
/// • Each line is trimmed; blank lines and lines starting with <c>#</c> are
///   skipped. <c>$NAME</c> and <c>${NAME}</c> are environment variables and
///   <c>$$</c> is a literal <c>$</c> (Go's <c>os.Expand</c>).
/// • A rule is matched against the full path, with forward slashes:
///   <c>C:/Users/me/x</c> on Windows, <c>/home/me/x</c> elsewhere. A rule
///   starting with <c>/</c> starts at the top of the file system (so it
///   matches nothing on Windows, where paths start with the drive); any other
///   rule may start at any folder. <c>?:/</c> is the top of any drive.
/// • <c>*</c> and <c>?</c> stay within one name, <c>**</c> is any number of
///   folders, and a rule also matches everything below what it matches. A
///   trailing <c>/</c> changes nothing: restic cleans it off.
/// • The last matching rule decides; a rule starting with <c>!</c> keeps what
///   an earlier rule excluded.
///
/// Known difference: restic tries a first <c>**</c> on at most as many
/// folders as the rest of the rule leaves room for, counting a second
/// <c>**</c> as one folder (docs/GOTCHAS.md). Here each <c>**</c> is any
/// number of folders, so a rule with two <c>**</c> can match a path one
/// folder shorter than restic would.
/// </summary>
public static class FilterTranslator
{
    /// <summary>True when restic gets the rules with <c>--iexclude-file</c>.</summary>
    internal static bool IgnoresCase => Platform.PlatformInfo.IsWindows;

    /// <summary>The restic flag that reads the Filters text.</summary>
    internal static string ExcludeFileFlag => IgnoresCase ? "--iexclude-file" : "--exclude-file";

    /// <param name="Glob">The rule as written in the Filters text, shown as
    /// the rule that decided.</param>
    /// <param name="Required">Text every path the rule matches contains, or
    /// null. Most paths lack it, and looking for it is far cheaper than the
    /// pattern: two thirds of the time to count C:\ with local_c's 270 rules
    /// went into the patterns (U123, 4 November 2026).</param>
    /// <param name="Rooted">The rule starts with <c>/</c>: only a path that
    /// starts with <c>/</c> can match it.</param>
    internal readonly record struct CompiledRule(string Glob, Regex Pattern, bool Negated, bool CaseInsensitive, string? Required = null, bool Rooted = false);

    /// <summary>The rules restic reads from <paramref name="text"/>, in order:
    /// each with the line as written and the pattern after <c>$</c> expansion.</summary>
    internal static List<(string Line, string Pattern)> ReadRules(string? text, Func<string, string?>? getenv = null)
    {
        var rules = new List<(string, string)>();
        if (string.IsNullOrEmpty(text)) return rules;
        getenv ??= Environment.GetEnvironmentVariable;
        // Restic drops a byte-order mark at the start of the file.
        foreach (var raw in text.TrimStart('﻿').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var pattern = Expand(line, getenv);
            if (pattern.Length == 0) continue; // restic ignores empty patterns
            rules.Add((line, pattern));
        }
        return rules;
    }

    /// <summary>Go's <c>os.Expand</c> as restic calls it on each line of an
    /// exclude file: <c>$$</c> is <c>$</c>, <c>$NAME</c> and <c>${NAME}</c>
    /// are environment variables (empty when unset).</summary>
    internal static string Expand(string s, Func<string, string?> getenv)
    {
        StringBuilder? buf = null;
        var i = 0;
        for (var j = 0; j < s.Length; j++)
        {
            if (s[j] != '$' || j + 1 >= s.Length) continue;
            buf ??= new StringBuilder(s.Length * 2);
            buf.Append(s, i, j - i);
            var (name, w) = ShellName(s, j + 1);
            if (name.Length == 0 && w > 0) { /* invalid syntax: Go drops it */ }
            else if (name.Length == 0) buf.Append('$');
            else buf.Append(name == "$" ? "$" : getenv(name) ?? "");
            j += w;
            i = j + 1;
        }
        return buf is null ? s : buf.Append(s, i, s.Length - i).ToString();
    }

    private static bool IsShellSpecial(char c) => c is '*' or '#' or '$' or '@' or '!' or '?' or '-' or (>= '0' and <= '9');
    private static bool IsAlphaNum(char c) => c == '_' || c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static (string Name, int Width) ShellName(string s, int start)
    {
        if (s[start] == '{')
        {
            if (s.Length - start > 2 && IsShellSpecial(s[start + 1]) && s[start + 2] == '}') return (s.Substring(start + 1, 1), 3);
            for (var i = start + 1; i < s.Length; i++)
            {
                if (s[i] != '}') continue;
                if (i == start + 1) return ("", 2);
                return (s[(start + 1)..i], i - start + 1);
            }
            return ("", 1);
        }
        if (IsShellSpecial(s[start])) return (s.Substring(start, 1), 1);
        var end = start;
        while (end < s.Length && IsAlphaNum(s[end])) end++;
        return (s[start..end], end - start);
    }

    /// <summary>A path as restic matches it: forward slashes, with its drive
    /// on Windows (<c>C:/Users/me</c>).</summary>
    internal static string ResticPath(string fullPath) => fullPath.Replace('\\', '/');

    /// <param name="Required">See <see cref="CompiledRule.Required"/>.</param>
    internal static string? RequiredText(string glob)
    {
        glob = glob.Replace('\\', '/');
        string best = "";
        var run = new StringBuilder();
        void End()
        {
            var text = run.ToString().Trim('/');
            if (text.Length > best.Length) best = text;
            run.Clear();
        }
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c is '*' or '?') { End(); continue; }
            if (c == '[')
            {
                End();
                var close = glob.IndexOf(']', i + 1);
                if (close < 0) return null;
                i = close;
                continue;
            }
            run.Append(c);
        }
        End();
        return best.Length >= 2 && best.All(char.IsAscii) ? best : null;
    }

    /// <summary>Time limit for the imported Duplicacy regexes, which run only
    /// while a rule is converted.</summary>
    internal static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>The rules of a Filters text, compiled, as restic gets them on
    /// this system.</summary>
    internal static List<CompiledRule> CompileRules(string? text) => CompileRules(text, IgnoresCase);

    internal static List<CompiledRule> CompileRules(string? text, bool ignoreCase)
    {
        var rules = new List<CompiledRule>();
        foreach (var (line, pattern) in ReadRules(text))
        {
            var negated = pattern[0] == '!';
            var glob = negated ? pattern[1..] : pattern;
            // A pathological rule ("*a*a*a*…b") must not hang the walk on one
            // long name (U152). The non-backtracking engine takes time in
            // proportion to the name, whatever the rule. It is also faster
            // here than a time limit (300,000 paths against local_c's 273
            // rules: 4.1 s, against 5.8 s with a time limit; 6 November 2026).
            var options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
            if (ignoreCase) options |= RegexOptions.IgnoreCase;
            Regex regex;
            // Restic refuses the whole file for an invalid rule; the editor
            // says which (InvalidRules). Here it simply matches nothing.
            try { regex = new Regex(GlobToRegex(glob), options); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { regex = new Regex(@"[^\s\S]"); }
            rules.Add(new(line, regex, negated, ignoreCase, RequiredText(glob), IsRooted(glob)));
        }
        return rules;
    }

    private static bool IsRooted(string glob) => glob.Length > 0 && (glob[0] == '/' || (glob[0] == '\\' && Platform.PlatformInfo.IsWindows));

    /// <summary>Lines restic refuses as patterns (an unclosed <c>[</c>, for
    /// example). Restic then refuses the whole file, so the backup fails.</summary>
    internal static IReadOnlyList<string> InvalidRules(string? text)
    {
        var invalid = new List<string>();
        foreach (var (line, pattern) in ReadRules(text))
        {
            var glob = pattern[0] == '!' ? pattern[1..] : pattern;
            // Parsing is enough (the editor checks on every keystroke); the
            // non-backtracking engine accepts the same patterns.
            try { _ = new Regex(GlobToRegex(glob), RegexOptions.CultureInvariant); }
            catch (ArgumentException) { invalid.Add(line); }
        }
        return invalid;
    }

    /// <summary>
    /// Exceptions (<c>!</c> rules) that can never bring anything back, each with
    /// why (U127, 4 November 2026). Restic decides on a folder before going
    /// into it and never enters an excluded one, so <c>!?:/cache/keep/</c>
    /// under <c>?:/cache/</c> keeps nothing: <c>?:/cache/*</c> would. Each
    /// exception is tried on a sample path (a wildcard stands for any name)
    /// whose folders are checked with all the rules, exceptions included.
    /// </summary>
    internal static IReadOnlyList<string> DeadExceptions(string? filtersText)
    {
        var rules = CompileRules(filtersText);
        var found = new List<string>();
        foreach (var exception in rules.Where(r => r.Negated))
        {
            var glob = exception.Glob[1..];
            var parts = SamplePath(Expand(glob, _ => ""));
            var start = IsRooted(glob) ? "/" : "";
            for (var depth = 1; depth < parts.Count; depth++)
            {
                var folder = start + string.Join("/", parts.Take(depth));
                if (Evaluate(rules, folder + "/") is not { Excluded: true, Rule: { } by }) continue;
                found.Add($"{exception.Glob} can never apply: the backup never looks inside {folder}, which {by} excludes. To keep it, exclude what {folder} contains ({by.TrimEnd('/')}/*) rather than the folder itself.");
                break;
            }
        }
        return found;
    }

    // A path the glob matches, with "x" for any name and nothing for "**".
    private static List<string> SamplePath(string glob)
    {
        var parts = new List<string>();
        foreach (var segment in glob.Replace('\\', '/').Trim('/').Split('/'))
        {
            if (segment == "**") continue;
            var name = new StringBuilder();
            for (var i = 0; i < segment.Length; i++)
            {
                var c = segment[i];
                if (c is '*' or '?') { name.Append('x'); continue; }
                if (c == '[')
                {
                    var close = segment.IndexOf(']', i + 1);
                    var first = close > i + 1 ? segment[i + 1] : 'x';
                    name.Append(first is '^' or '!' ? 'x' : first);
                    i = close < 0 ? segment.Length : close;
                    continue;
                }
                name.Append(c);
            }
            if (name.Length > 0) parts.Add(name.ToString());
        }
        return parts;
    }

    /// <summary>Whether the rules exclude <paramref name="path"/>, a full path
    /// as restic sees it (<see cref="ResticPath"/>), and the rule that decided.
    /// A trailing <c>/</c> (a folder, for display) is ignored.</summary>
    internal static (bool Excluded, string? Rule) Evaluate(IReadOnlyList<CompiledRule> rules, string path)
    {
        path = path.TrimEnd('/');
        // Restic splits "/home/me" into "/", "home", "me": a rule starting with
        // "/" must start at that top, and any other rule starts at a name.
        var absolute = path.StartsWith('/');
        var body = absolute ? path[1..] : path;
        string? decided = null;
        foreach (var rule in rules)
        {
            if (rule.Rooted && !absolute) continue;
            if (rule.Required is { } text
                && body.IndexOf(text, rule.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) < 0) continue;
            if (!rule.Pattern.IsMatch(body)) continue;
            decided = rule.Negated ? null : rule.Glob;
        }
        return (decided is not null, decided);
    }

    /// <summary>
    /// Compile a restic pattern (without its <c>!</c>) into the regex used by
    /// Count, Simulate and the size estimates, matched against a full path
    /// without its leading <c>/</c> (<see cref="Evaluate"/>):
    ///
    /// • Like restic (<c>filepath.Clean</c>), doubled slashes, <c>.</c> names
    ///   and a trailing <c>/</c> are dropped. A leading <c>/</c> ties the rule
    ///   to the top; otherwise it may start at any name.
    /// • A rule matches a run of whole names and everything below it.
    /// • <c>*</c> matches any characters except <c>/</c>, <c>?</c> one of them,
    ///   <c>[…]</c> one character of a class (<c>[^…]</c> negated).
    /// • A whole-name <c>**</c> matches zero or more names.
    /// • Backslashes are slashes, as in restic on Windows.
    /// </summary>
    internal static string GlobToRegex(string glob)
    {
        // Never matches, as restic's "." (an empty or "." rule); no lookaround,
        // which the non-backtracking engine refuses.
        const string never = @"[^\s\S]";
        if (string.IsNullOrEmpty(glob)) return never;

        glob = glob.Replace('\\', '/');
        var rooted = glob[0] == '/';
        glob = string.Join("/", glob.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != "."));
        if (glob.Length == 0) return rooted ? "^.*$" : never;

        var sb = new StringBuilder();
        sb.Append(rooted ? "^" : "(?:^|/)");

        int i = 0;
        while (i < glob.Length)
        {
            // A whole-name "**": zero or more names.
            if (i + 1 < glob.Length && glob[i] == '*' && glob[i + 1] == '*'
                && (i == 0 || glob[i - 1] == '/')
                && (i + 2 == glob.Length || glob[i + 2] == '/'))
            {
                bool followedBySlash = i + 2 < glob.Length;
                bool precededBySlash = sb.Length > 0 && sb[^1] == '/';
                if (followedBySlash)
                {
                    if (precededBySlash) { sb.Length--; sb.Append("(?:/.*)?/"); } // "a/**/b"
                    else sb.Append("(?:.*/)?");                                    // "**/b"
                    i += 3;
                    continue;
                }
                if (precededBySlash) { sb.Length--; sb.Append("(?:/.*)?"); }     // "a/**"
                else sb.Append(".*");                                              // "**"
                i += 2;
                continue;
            }

            var c = glob[i];
            switch (c)
            {
                case '[':
                    var end = glob.IndexOf(']', i + 1);
                    if (end < 0 || end == i + 1) throw new ArgumentException("Unclosed or empty character class.");
                    var body = glob.Substring(i + 1, end - i - 1);
                    if (body == "^" || body.Contains('/')) throw new ArgumentException("Invalid character class.");
                    sb.Append('[');
                    for (var j = 0; j < body.Length; j++)
                    {
                        if (body[j] == '[' || (body[j] == '^' && j != 0)) sb.Append('\\');
                        sb.Append(body[j]);
                    }
                    sb.Append(']');
                    i = end;
                    break;
                case '*': sb.Append("[^/]*"); break;
                case '?': sb.Append("[^/]");  break;
                case '.': case '(': case ')': case '+': case '|':
                case '^': case '$': case '{': case '}': case ']':
                    sb.Append('\\').Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
            i++;
        }

        sb.Append("(?:/.*)?$");
        return sb.ToString();
    }
}
