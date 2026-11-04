using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Backspace.Services;

/// <summary>
/// Translates Backspace's filter text into the two glob lists Backrest's
/// plan model consumes (<c>excludes</c> case-sensitive, <c>iexcludes</c>
/// case-insensitive — mapped 1:1 to restic's <c>--exclude</c> /
/// <c>--iexclude</c> flags).
///
/// The source format uses restic globs, one per line with <c>#</c>
/// comments. Backspace evaluates them relative to each selected source.
/// Engine plans bind those globs to the absolute source path so an
/// ancestor named "temp" cannot exclude the selected source itself.
///
/// Case sensitivity: lines route to the platform's natural default —
/// <c>iexcludes</c> on Windows (the filesystem is case-insensitive, so
/// a case-sensitive pattern would silently miss files whose stored case
/// differs), <c>excludes</c> on Linux / macOS-on-case-sensitive volumes.
///
/// Duplicacy regex shapes (<c>e:&lt;regex&gt;</c>) are also accepted so
/// the "Import from existing Duplicacy" path keeps working without a
/// separate translation step at migration time. Each <c>e:</c> line is
/// rewritten to a best-effort restic glob — see <see cref="TryRegexToGlob"/>.
/// Imports survive; emitters always write bare globs.
/// </summary>
public static class FilterTranslator
{
    /// <summary>Result of translating a filter text — two lists matching
    /// the <c>excludes</c> / <c>iexcludes</c> fields on Backrest's plan
    /// shape one-for-one.</summary>
    public readonly record struct Result(
        IReadOnlyList<string> Excludes,
        IReadOnlyList<string> IExcludes);

    /// <summary>
    /// Translate the filter text into restic-compatible exclude lists,
    /// using the platform's natural case-sensitivity default.
    /// </summary>
    public static Result Translate(string? filtersText) =>
        Translate(filtersText, defaultsToCaseInsensitive: Platform.PlatformInfo.IsWindows);

    /// <summary>The filter text that applies to one source: rules for the top
    /// of a drive (System Volume Information, /Windows/, /Intel/…) only when
    /// the source is a whole drive, so they never match a person's own folder
    /// of the same name at the top of a backed-up folder (U106).</summary>
    internal static string? ScopeToSource(string? filtersText, string sourcePath)
    {
        if (filtersText is null || TreeWalk.IsDriveRoot(sourcePath)) return filtersText;
        return string.Join("\n", filtersText.Replace("\r\n", "\n").Split('\n').Where(l => !RecommendedFilters.IsDriveTopPattern(l)));
    }

    /// <summary>Bind source-relative exclusions to the paths restic walks.</summary>
    internal static Result TranslateForSource(string? filtersText, string sourcePath)
    {
        var translated = Translate(ScopeToSource(filtersText, sourcePath));
        var root = Path.GetFullPath(sourcePath).Replace('\\', '/').TrimEnd('/');
        // Escape only the literal path prefix; user globs retain their syntax.
        root = root.Replace("[", "[[]").Replace("*", "[*]").Replace("?", "[?]");
        string Bind(string pattern)
        {
            var negated = pattern.StartsWith('!');
            var glob = (negated ? pattern[1..] : pattern).Replace('\\', '/');
            return (negated ? "!" : "") + root +
                   (glob.StartsWith('/') ? glob : "/**/" + glob);
        }
        return new Result(translated.Excludes.Select(Bind).ToArray(),
            translated.IExcludes.Select(Bind).ToArray());
    }

    /// <param name="Required">Text every path the rule matches contains, or
    /// null. Most paths lack it, and looking for it is far cheaper than the
    /// pattern: two thirds of the time to count C:\ with local_c's 270 imported
    /// rules went into the patterns (U123, 4 November 2026).</param>
    internal readonly record struct CompiledRule(string Glob, Regex Pattern, bool Negated, bool CaseInsensitive, string? Required = null);

    /// <summary>The longest run of plain characters in <paramref name="glob"/>,
    /// without the slashes at its ends (an anchor or a folder mark may make
    /// those optional), when it is at least two ASCII characters; else null.
    /// Every path the glob's pattern matches contains it.</summary>
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

    internal static List<CompiledRule> CompileRules(string? text, bool compiled = true)
    {
        var translated = Translate(text);
        var rules = new List<CompiledRule>();
        void Add(string glob, bool insensitive)
        {
            var negated = glob.StartsWith('!');
            var options = (compiled ? RegexOptions.Compiled : RegexOptions.None) | RegexOptions.CultureInvariant;
            if (insensitive) options |= RegexOptions.IgnoreCase;
            Regex pattern;
            try { pattern = new Regex(GlobToRegex(negated ? glob[1..] : glob), options); }
            catch (ArgumentException) { pattern = new Regex("(?!)"); }
            rules.Add(new(glob, pattern, negated, insensitive, RequiredText(negated ? glob[1..] : glob)));
        }
        foreach (var glob in translated.Excludes) Add(glob, false);
        foreach (var glob in translated.IExcludes) Add(glob, true);
        return rules;
    }

    /// <summary>
    /// Exceptions (<c>!</c> rules) that can never bring anything back, each with
    /// why (U127, 4 November 2026). The engine decides on a folder before going
    /// into it and never enters an excluded one, so <c>!cache/keep/</c> under
    /// <c>cache/</c> keeps nothing: <c>cache/*</c> would. Each exception is tried
    /// on a sample path (a wildcard stands for any name) whose folders are
    /// checked with all the rules, exceptions included.
    /// </summary>
    internal static IReadOnlyList<string> DeadExceptions(string? filtersText)
    {
        var rules = CompileRules(filtersText, compiled: false);
        var found = new List<string>();
        foreach (var exception in rules.Where(r => r.Negated))
        {
            var parts = SamplePath(exception.Glob[1..]);
            for (var depth = 1; depth < parts.Count; depth++)
            {
                var folder = string.Join("/", parts.Take(depth));
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

    internal static (bool Excluded, string? Rule) Evaluate(IReadOnlyList<CompiledRule> rules, string relativePath)
    {
        // Walkers identify directories with a trailing separator for display;
        // restic matches the directory's own name before deciding to descend.
        relativePath = relativePath.TrimEnd('/');
        string? sensitive = null, insensitive = null;
        foreach (var rule in rules)
        {
            if (rule.Required is { } text
                && relativePath.IndexOf(text, rule.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) < 0) continue;
            if (!rule.Pattern.IsMatch(relativePath)) continue;
            // Ordered exceptions apply separately to --exclude and --iexclude.
            if (rule.CaseInsensitive) insensitive = rule.Negated ? null : rule.Glob;
            else sensitive = rule.Negated ? null : rule.Glob;
        }
        return (sensitive is not null || insensitive is not null, sensitive ?? insensitive);
    }

    /// <summary>
    /// Test-friendly overload that pins the platform default explicitly.
    /// </summary>
    internal static Result Translate(string? filtersText, bool defaultsToCaseInsensitive)
        => Translate(filtersText, defaultsToCaseInsensitive, null);

    /// <param name="untranslated">Receives imported Duplicacy lines that have
    /// no exact restic equivalent; they are left out rather than guessed.</param>
    internal static Result Translate(string? filtersText, bool defaultsToCaseInsensitive, ICollection<string>? untranslated)
    {
        var excludes = new List<string>();
        var iExcludes = new List<string>();
        if (string.IsNullOrEmpty(filtersText)) return new Result(excludes, iExcludes);

        void AddDefault(string glob)
        {
            if (glob.Length == 0) return;
            (defaultsToCaseInsensitive ? iExcludes : excludes).Add(glob);
        }

        // Imported Duplicacy lines (e: exclude, i: include) keep Duplicacy's
        // first-match meaning within each uninterrupted group of them; see
        // DuplicacyFilterTranslator. Comments and blank lines do not split a group.
        var run = new List<DuplicacyFilterTranslator.Line>();
        void Flush()
        {
            if (run.Count == 0) return;
            foreach (var rule in DuplicacyFilterTranslator.TranslateRun(run, untranslated))
                (rule.CaseInsensitive ? iExcludes : excludes).Add((rule.Negated ? "!" : "") + rule.Glob);
            run.Clear();
        }

        foreach (var raw in filtersText.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (DuplicacyFilterTranslator.IsDuplicacyLine(line)) { run.Add(DuplicacyFilterTranslator.Parse(line)); continue; }
            Flush();
            AddDefault(line);
        }
        Flush();
        return new Result(excludes, iExcludes);
    }

    /// <summary>First restic glob for one Duplicacy regex, or null when it has
    /// no exact equivalent. Kept for callers that show a single pattern.</summary>
    internal static string? TryRegexToGlob(string regex)
        => DuplicacyFilterTranslator.ToGlobs(regex) is { Count: > 0 } globs ? globs[0].Glob : null;

    /// <summary>
    /// Compile a restic glob pattern into the equivalent regex used by
    /// the local preview/walk paths (FilterSimulator + SavingsEstimator).
    /// Mirrors restic's own pattern semantics so the preview can't drift
    /// from what the engine actually does:
    ///
    /// • Leading <c>/</c> anchors to the source root; an unrooted
    ///   pattern matches at any path segment boundary
    ///   (<c>cache</c> matches <c>cache</c>, <c>dir/cache</c>, etc.).
    /// • A pattern also matches everything below what it matches, as in
    ///   restic, where <c>!keep/in</c> re-includes <c>keep/in/deep/k.txt</c>.
    ///   A trailing <c>/</c> changes nothing: restic's <c>*Cache/</c> also
    ///   excludes a file named <c>ModuleAnalysisCache</c> (both checked with
    ///   restic 0.19.1 on 31 October 2026).
    /// • <c>*</c> matches any number of characters except <c>/</c>.
    /// • <c>**</c> matches zero or more path components — including the
    ///   empty case, so <c>Documents/**/*.txt</c> matches
    ///   <c>Documents/notes.txt</c> as well as <c>Documents/sub/notes.txt</c>.
    /// • <c>?</c> matches a single character except <c>/</c>.
    /// • Backslashes are normalised to forward slashes so a Windows user
    ///   typing <c>Documents\Letters\*.pdf</c> still matches restic's
    ///   forward-slash-internal paths.
    /// </summary>
    internal static string GlobToRegex(string glob)
    {
        if (string.IsNullOrEmpty(glob)) return "(?!)";

        // Restic stores paths with forward slashes regardless of host OS.
        glob = glob.Replace('\\', '/');

        bool rooted = glob[0] == '/';
        if (rooted) glob = glob.Substring(1);
        bool dirOnly = glob.EndsWith("/", StringComparison.Ordinal);
        if (dirOnly) glob = glob.Substring(0, glob.Length - 1);

        var sb = new StringBuilder();
        sb.Append(rooted ? "^" : "(?:^|/)");

        int i = 0;
        while (i < glob.Length)
        {
            // `**` runs — handle them with awareness of the surrounding
            // slashes so the zero-component case (restic-canonical) works.
            if (i + 1 < glob.Length && glob[i] == '*' && glob[i + 1] == '*'
                && (i == 0 || glob[i - 1] == '/')
                && (i + 2 == glob.Length || glob[i + 2] == '/'))
            {
                bool followedBySlash = i + 2 < glob.Length && glob[i + 2] == '/';
                bool atEnd = i + 2 == glob.Length;
                bool precededBySlashInEmitted = sb.Length > 0 && sb[sb.Length - 1] == '/';

                if (followedBySlash)
                {
                    // `**/X` shape. Make the whole `**/` optional so the
                    // zero-intermediate-component case matches too.
                    if (precededBySlashInEmitted)
                    {
                        // `/**/X` → undo the trailing `/`, emit `(?:/.*)?/`
                        sb.Length--;
                        sb.Append("(?:/.*)?/");
                    }
                    else
                    {
                        // `**/X` at the start of the body (after the
                        // segment-boundary prefix) → optional any-prefix.
                        sb.Append("(?:.*/)?");
                    }
                    i += 3;
                    continue;
                }
                if (atEnd)
                {
                    // `**` at end of glob → match everything (including
                    // the zero-suffix case so `Cache/**` matches `Cache`).
                    if (precededBySlashInEmitted)
                    {
                        sb.Length--;
                        sb.Append("(?:/.*)?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                    i += 2;
                    continue;
                }
                // Standalone `**X` (no surrounding slashes) — greedy any.
                sb.Append(".*");
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
