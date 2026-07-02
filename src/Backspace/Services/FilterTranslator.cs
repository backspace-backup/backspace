using System;
using System.Collections.Generic;
using System.Text;

namespace Backspace.Services;

/// <summary>
/// Translates Backspace's filter text into the two glob lists Backrest's
/// plan model consumes (<c>excludes</c> case-sensitive, <c>iexcludes</c>
/// case-insensitive — mapped 1:1 to restic's <c>--exclude</c> /
/// <c>--iexclude</c> flags).
///
/// The source format IS plain restic — one glob per line, with <c>#</c>
/// comments. The same file can be handed to vanilla restic via
/// <c>--exclude-file</c> (case-sensitive) or <c>--iexclude-file</c>
/// (case-insensitive); there is intentionally no Backspace-specific
/// syntax so a hand-written restic backup running against the same
/// file behaves the same way.
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

    /// <summary>
    /// Test-friendly overload that pins the platform default explicitly.
    /// </summary>
    internal static Result Translate(string? filtersText, bool defaultsToCaseInsensitive)
    {
        var excludes = new List<string>();
        var iExcludes = new List<string>();
        if (string.IsNullOrEmpty(filtersText)) return new Result(excludes, iExcludes);

        void AddDefault(string glob)
        {
            if (glob.Length == 0) return;
            (defaultsToCaseInsensitive ? iExcludes : excludes).Add(glob);
        }

        foreach (var raw in filtersText.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            // Duplicacy regex exclude (only accepted shape that isn't a
            // bare restic glob — kept for the "Import from Duplicacy"
            // flow). Translated to a restic glob, honouring the inline
            // `(?i)` case flag.
            if (line.StartsWith("e:", StringComparison.Ordinal))
            {
                var body = line.Substring(2);
                bool caseInsensitive = false;
                if (body.StartsWith("(?i)", StringComparison.Ordinal))
                {
                    caseInsensitive = true;
                    body = body.Substring(4);
                }
                var glob = TryRegexToGlob(body);
                if (glob is null) continue;
                (caseInsensitive ? iExcludes : excludes).Add(glob);
                continue;
            }

            // Duplicacy `i:`-prefixed include patterns have no restic
            // exclude-list analog — skip silently. (Same applies to
            // `+` glob includes; those don't have a special prefix in
            // restic, so we treat a bare `+` as a literal glob.)
            if (line.StartsWith("i:", StringComparison.Ordinal)) continue;

            // Bare line — the restic-native shape.
            AddDefault(line);
        }
        return new Result(excludes, iExcludes);
    }

    /// <summary>
    /// Best-effort translation of a small Duplicacy regex subset into a
    /// restic-compatible glob pattern. Returns null for shapes we don't
    /// know how to translate so the caller can drop the line rather
    /// than emit a glob that would surprise the user.
    ///
    /// Supported shapes:
    ///   <c>^X$</c>            → <c>X</c>          (root-anchored exact)
    ///   <c>^X</c>             → <c>X*</c>         (root-anchored prefix)
    ///   <c>^X/</c>            → <c>X/**</c>       (root directory)
    ///   <c>(^|/)X/</c>        → <c>**/X/**</c>    (X directory anywhere)
    ///   <c>(^|/)X$</c>        → <c>**/X</c>       (X file anywhere)
    ///   <c>X$</c>             → <c>*X</c>         (suffix anywhere)
    ///   <c>X</c>              → <c>*X*</c>        (substring anywhere)
    /// Escapes <c>\.</c>, <c>\$</c>, <c>\\</c>, <c>\/</c>, <c>\-</c>
    /// become their literal counterparts. <c>[^/]+</c> / <c>[^/]*</c>
    /// become <c>*</c>. A trailing <c>?</c> after a literal letter is
    /// dropped (the singular form still matches the singular files).
    /// Any other regex metacharacter invalidates the translation.
    /// </summary>
    internal static string? TryRegexToGlob(string regex)
    {
        if (string.IsNullOrEmpty(regex)) return null;

        bool anywhereDir = false;   // matched leading (^|/)
        bool anchored = false;       // matched leading ^
        var body = regex;

        if (body.StartsWith("(^|/)", StringComparison.Ordinal))
        {
            anywhereDir = true;
            body = body.Substring(5);
        }
        else if (body[0] == '^')
        {
            anchored = true;
            body = body.Substring(1);
        }

        bool endAnchored = body.EndsWith("$", StringComparison.Ordinal)
                           && !body.EndsWith("\\$", StringComparison.Ordinal);
        if (endAnchored) body = body.Substring(0, body.Length - 1);

        bool trailingSlash = body.EndsWith("/", StringComparison.Ordinal);

        var literal = TryUnescape(body);
        if (literal is null) return null;

        if (anywhereDir)
        {
            if (trailingSlash) return "**/" + literal + "**";
            if (endAnchored)   return "**/" + literal;
            return "**/" + literal + "*";
        }
        if (anchored)
        {
            if (trailingSlash) return literal + "**";
            if (endAnchored)   return literal;
            return literal + "*";
        }
        // No anchor — substring match.
        if (trailingSlash) return "**/" + literal + "**";
        if (endAnchored)   return "*" + literal;
        return "*" + literal + "*";
    }

    /// <summary>
    /// Compile a restic glob pattern into the equivalent regex used by
    /// the local preview/walk paths (FilterSimulator + SavingsEstimator).
    /// Mirrors restic's own pattern semantics so the preview can't drift
    /// from what the engine actually does:
    ///
    /// • Leading <c>/</c> anchors to the source root; an unrooted
    ///   pattern matches at any path segment boundary
    ///   (<c>cache</c> matches <c>cache</c>, <c>dir/cache</c>, etc.).
    /// • Trailing <c>/</c> matches the directory itself AND everything
    ///   under it (<c>Cache/</c> matches <c>Cache</c>, <c>Cache/x</c>,
    ///   <c>sub/Cache/x</c>).
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
            if (i + 1 < glob.Length && glob[i] == '*' && glob[i + 1] == '*')
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
                case '*': sb.Append("[^/]*"); break;
                case '?': sb.Append("[^/]");  break;
                case '.': case '(': case ')': case '+': case '|':
                case '^': case '$': case '{': case '}': case '[': case ']':
                    sb.Append('\\').Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
            i++;
        }

        sb.Append(dirOnly ? "(?:/.*)?$" : "$");
        return sb.ToString();
    }

    private static string? TryUnescape(string body)
    {
        var sb = new StringBuilder(body.Length);
        for (int i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '\\')
            {
                if (i + 1 >= body.Length) return null;
                var n = body[i + 1];
                if (n == '.' || n == '$' || n == '\\' || n == '/' || n == '-')
                {
                    sb.Append(n);
                    i++;
                    continue;
                }
                return null;
            }

            // [^/]+ or [^/]* → glob `*` (single path segment wildcard).
            if (c == '[' && i + 4 < body.Length
                && body[i + 1] == '^' && body[i + 2] == '/' && body[i + 3] == ']'
                && (body[i + 4] == '+' || body[i + 4] == '*'))
            {
                sb.Append('*');
                i += 4;
                continue;
            }

            // Trailing `?` after a literal letter (e.g. `Logs?`) drops
            // to the singular form. Partial coverage > whole-line drop.
            if (c == '?' && sb.Length > 0 && char.IsLetter(sb[sb.Length - 1]))
            {
                sb.Length--;
                continue;
            }

            switch (c)
            {
                case '?': case '+': case '|': case '(': case ')':
                case '[': case ']': case '{': case '}': case '^': case '$':
                    return null;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
