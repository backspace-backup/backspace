using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Backspace.Services;

/// <summary>
/// Translates imported Duplicacy filter lines (<c>e:</c> exclude and
/// <c>i:</c> include regexes) into restic patterns with the same meaning
/// (U054, 31 October 2026). The earlier best-effort translation lost root
/// anchors, so <c>^Python[^/]*/</c> excluded every folder named Python*
/// anywhere (4.6 GB of the maintainer's D: drive), ignored include lines
/// and dropped alternatives such as <c>\.(tmp|temp)$</c>.
///
/// Duplicacy applies the first matching rule, restic the last one; restic's
/// <c>!pattern</c> re-includes. A run of consecutive excludes or includes
/// commutes internally, so reversing the order of the runs reproduces
/// Duplicacy's decision for every path. Both engines skip an excluded
/// folder entirely, which keeps the traversal identical too.
///
/// Restic patterns match a path and everything below it. An include that
/// Duplicacy anchors to a folder itself (<c>Chrome/$</c>, used to reach a
/// subfolder that a later rule excludes around it) therefore becomes
/// <c>!Chrome</c> followed by <c>Chrome/*</c>, deeper includes after
/// shallower ones. Restic patterns cannot tell files from folders (checked
/// with restic 0.19.1): a folder exclude <c>X/</c> becomes <c>X/*</c>, which
/// keeps the folder empty and leaves files named X alone, as Duplicacy does.
/// Two differences remain: files lying directly in a folder kept only to
/// reach a subfolder are kept, and a folder whose name matches a file-only
/// rule such as <c>\.tmp$</c> is skipped with its contents.
/// </summary>
internal static class DuplicacyFilterTranslator
{
    internal readonly record struct Rule(string Glob, bool Negated, bool CaseInsensitive);
    internal readonly record struct Translation(string Glob, bool ExactDirectory);

    /// <summary>One Duplicacy line of a contiguous run of filter lines.</summary>
    internal sealed record Line(bool Include, bool CaseInsensitive, string Regex, string Source);

    internal static bool IsDuplicacyLine(string line) =>
        line.StartsWith("e:", StringComparison.Ordinal) || line.StartsWith("i:", StringComparison.Ordinal);

    internal static Line Parse(string line)
    {
        var body = line[2..];
        var insensitive = body.StartsWith("(?i)", StringComparison.Ordinal);
        if (insensitive) body = body[4..];
        return new(line[0] == 'i', insensitive, body, line);
    }

    /// <summary>Restic rules for consecutive Duplicacy lines, in restic order.
    /// Lines that cannot be translated exactly are reported and skipped.</summary>
    internal static List<Rule> TranslateRun(IReadOnlyList<Line> lines, ICollection<string>? untranslated = null)
    {
        var translated = lines.Select(l => (line: l, globs: ToGlobs(l.Regex))).ToList();
        foreach (var (line, globs) in translated)
            if (globs is null) untranslated?.Add(line.Source);

        var runs = new List<List<(Line line, IReadOnlyList<Translation> globs)>>();
        foreach (var (line, globs) in translated)
        {
            if (globs is null) continue;
            if (runs.Count == 0 || runs[^1][0].line.Include != line.Include) runs.Add(new());
            runs[^1].Add((line, globs));
        }

        var rules = new List<Rule>();
        for (var r = runs.Count - 1; r >= 0; r--)
        {
            var run = runs[r];
            if (!run[0].line.Include)
            {
                // A folder rule excludes the folder's contents, not the folder:
                // restic's "Cache/" would also exclude a file named
                // "ModuleAnalysisCache", which Duplicacy keeps.
                foreach (var (line, globs) in run)
                    foreach (var g in globs) rules.Add(new(g.Glob.EndsWith('/') ? g.Glob + "*" : g.Glob, false, line.CaseInsensitive));
                continue;
            }
            // Later excludes (earlier in restic order, already emitted) decide
            // whether a folder-only include needs its contents excluded again.
            var later = runs.Skip(r + 1).SelectMany(x => x).Where(x => !x.line.Include).Select(x => x.line).ToList();
            var includes = run.SelectMany(x => x.globs.Select(g => (x.line, g)))
                .OrderBy(x => x.g.Glob.Count(c => c == '/')).ToList();
            foreach (var (line, g) in includes)
            {
                var glob = g.Glob.TrimEnd('/');
                rules.Add(new(glob, true, line.CaseInsensitive));
                if (g.ExactDirectory && later.Any(e => CoversChildren(e, glob)))
                    rules.Add(new(glob + "/*", false, line.CaseInsensitive));
            }
        }
        return rules;
    }

    /// <summary>Whether a later Duplicacy exclude would exclude the children of
    /// a folder that an include keeps on its own. Tested on a concrete child
    /// path with each wildcard replaced by a sample name.</summary>
    private static bool CoversChildren(Line exclude, string folderGlob)
    {
        var sample = folderGlob.TrimStart('/').Replace("**", "x").Replace('*', 'x').Replace('?', 'x');
        if (sample.Contains('[')) return true; // Conservative: keep the folder-only meaning.
        try
        {
            var options = RegexOptions.CultureInvariant | (exclude.CaseInsensitive ? RegexOptions.IgnoreCase : 0);
            return Regex.IsMatch(sample + "/child/", exclude.Regex, options) || Regex.IsMatch(sample + "/child", exclude.Regex, options);
        }
        catch (ArgumentException) { return true; }
    }

    // ---- regex → glob ----

    private enum Kind { Literal, Segment, AnyChar, Deep, Class }
    private readonly record struct Token(Kind Kind, string Text = "");

    private sealed class Unsupported : Exception { }

    /// <summary>Exact restic globs for one Duplicacy regex (without its
    /// <c>e:</c>/<c>i:</c> prefix or <c>(?i)</c> flag), or null when the regex
    /// uses something these globs cannot express.</summary>
    internal static IReadOnlyList<Translation>? ToGlobs(string regex)
    {
        if (string.IsNullOrEmpty(regex)) return null;
        var body = regex;
        bool rooted = false, segmentStart = false;
        if (body.StartsWith("(^|/)", StringComparison.Ordinal)) { segmentStart = true; body = body[5..]; }
        else if (body[0] == '^') { rooted = true; body = body[1..]; }
        var exact = body.EndsWith('$') && !EndsEscaped(body);
        if (exact) body = body[..^1];
        try
        {
            var parser = new Parser(body, unanchoredStart: !rooted && !segmentStart, prefixEnd: !exact);
            var variants = parser.Parse();
            var result = new List<Translation>();
            foreach (var tokens in variants.SelectMany(ExpandDeep))
            {
                var t = tokens.ToList();
                var mode = rooted ? "rooted" : segmentStart ? "segment" : "anywhere";
                if (mode == "anywhere" && t.Count > 0 && t[0] is { Kind: Kind.Literal, Text: "/" }) { t.RemoveAt(0); mode = "below"; }
                if (t.Count == 0) return null;
                var glob = Render(t, mode, exact);
                if (glob is null) return null;
                var exactDirectory = exact && t[^1] is { Kind: Kind.Literal, Text: "/" };
                if (!result.Any(x => x.Glob == glob)) result.Add(new(glob, exactDirectory));
            }
            return result.Count is > 0 and <= 16 ? result : null;
        }
        catch (Unsupported) { return null; }
    }

    private static bool EndsEscaped(string s)
    {
        var slashes = 0;
        for (var i = s.Length - 2; i >= 0 && s[i] == '\\'; i--) slashes++;
        return slashes % 2 == 1;
    }

    /// <summary><c>.*</c> inside a name may stay within it or cross folders.</summary>
    private static IEnumerable<List<Token>> ExpandDeep(List<Token> tokens)
    {
        var index = -1;
        for (var i = 0; i < tokens.Count; i++)
            if (tokens[i] is { Kind: Kind.Deep, Text: "" } && i > 0 && tokens[i - 1] is not { Kind: Kind.Literal, Text: "/" } && i < tokens.Count - 1)
            { index = i; break; }
        if (index < 0) { yield return tokens; yield break; }
        var within = new List<Token>(tokens) { [index] = new(Kind.Segment) };
        var across = new List<Token>(tokens);
        across.RemoveAt(index);
        across.InsertRange(index, new[] { new Token(Kind.Segment), new Token(Kind.Literal, "/"), new Token(Kind.Deep, "**"), new Token(Kind.Literal, "/"), new Token(Kind.Segment) });
        foreach (var v in ExpandDeep(within)) yield return v;
        foreach (var v in ExpandDeep(across)) yield return v;
    }

    private static string? Render(List<Token> tokens, string mode, bool exact)
    {
        var sb = new StringBuilder();
        if (mode == "rooted") sb.Append('/');
        else if (mode == "anywhere") sb.Append('*'); // the match may start inside a name
        else if (mode == "below") sb.Append("*/**/"); // "/X": X below some folder, never at the top
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var atBoundary = (i == 0 && mode is not "anywhere") || (i > 0 && tokens[i - 1] is { Kind: Kind.Literal, Text: "/" });
            var nextIsSlash = i + 1 < tokens.Count && tokens[i + 1] is { Kind: Kind.Literal, Text: "/" };
            switch (t.Kind)
            {
                case Kind.Literal:
                    foreach (var c in t.Text) sb.Append(c is '*' or '?' or '[' ? $"[{c}]" : c.ToString());
                    break;
                case Kind.Segment: sb.Append('*'); break;
                case Kind.AnyChar: sb.Append('?'); break;
                case Kind.Class: sb.Append('[').Append(t.Text).Append(']'); break;
                case Kind.Deep:
                    if (t.Text == "**") sb.Append("**"); // any number of folders, from ExpandDeep
                    else if (i == tokens.Count - 1) sb.Append(atBoundary ? "**" : "*");
                    else if (atBoundary && nextIsSlash) sb.Append("*/**"); // at least one folder
                    else if (atBoundary) sb.Append("**/*");
                    else return null;
                    break;
            }
        }
        var last = tokens[^1];
        if (!exact && last is not { Kind: Kind.Literal, Text: "/" } && last.Kind != Kind.Deep) sb.Append('*');
        var glob = sb.ToString().Replace("***", "**");
        return glob is "/" or "*" or "**" or "/**" ? null : glob;
    }

    private sealed class Parser(string text, bool unanchoredStart, bool prefixEnd)
    {
        private int _pos;

        public List<List<Token>> Parse()
        {
            var result = ParseAlternatives();
            if (_pos != text.Length) throw new Unsupported();
            return result;
        }

        private List<List<Token>> ParseAlternatives()
        {
            var all = new List<List<Token>>(ParseSequence());
            while (_pos < text.Length && text[_pos] == '|') { _pos++; all.AddRange(ParseSequence()); }
            return all;
        }

        private List<List<Token>> ParseSequence()
        {
            var variants = new List<List<Token>> { new() };
            while (_pos < text.Length && text[_pos] is not ('|' or ')'))
            {
                var atStart = _pos == 0;
                var atom = ParseAtom();
                List<List<Token>> choices;
                if (_pos < text.Length && text[_pos] == '?') { _pos++; choices = new(atom) { new() }; }
                else if (_pos < text.Length && text[_pos] is '*' or '+')
                {
                    var quantifier = text[_pos++];
                    var single = atom.Count == 1 && atom[0].Count == 1 ? atom[0][0] : (Token?)null;
                    if (single is { Kind: Kind.AnyChar } && quantifier == '*') choices = new() { new() { new(Kind.Deep) } };
                    else if (single is { Kind: Kind.Segment }) choices = new() { new() { single.Value } };
                    // A repeated character at the open start or open end of the
                    // pattern adds nothing: the match may begin or end anywhere.
                    else if ((atStart && unanchoredStart && quantifier == '*') || (_pos == text.Length && prefixEnd))
                        choices = quantifier == '+' ? atom : new() { new() };
                    else throw new Unsupported();
                }
                else choices = atom;
                if (variants.Count * choices.Count > 16) throw new Unsupported();
                variants = variants.SelectMany(v => choices.Select(c => v.Concat(c).ToList())).ToList();
            }
            return variants;
        }

        private List<List<Token>> ParseAtom()
        {
            var c = text[_pos];
            switch (c)
            {
                case '(':
                    _pos++;
                    if (_pos < text.Length && text[_pos] == '?') throw new Unsupported(); // flags, lookarounds
                    var inner = ParseAlternatives();
                    if (_pos >= text.Length || text[_pos] != ')') throw new Unsupported();
                    _pos++;
                    return inner;
                case '[':
                    var end = text.IndexOf(']', _pos + 1);
                    if (end < 0) throw new Unsupported();
                    var cls = text[(_pos + 1)..end];
                    _pos = end + 1;
                    if (cls == "^/") return new() { new() { new(Kind.Segment) } }; // with + or *; alone it is one character
                    if (cls.Length == 0 || cls.Contains('/') || cls.Contains('\\') || cls.Contains('[')) throw new Unsupported();
                    return new() { new() { new(Kind.Class, cls) } };
                case '\\':
                    if (_pos + 1 >= text.Length) throw new Unsupported();
                    var escaped = text[_pos + 1];
                    if (char.IsLetterOrDigit(escaped)) throw new Unsupported(); // \d, \w, \s, \b...
                    _pos += 2;
                    return new() { new() { new(Kind.Literal, escaped.ToString()) } };
                case '.':
                    _pos++;
                    return new() { new() { new(Kind.AnyChar) } };
                case '^': case '$': case '{': case '}': case ')': case '|': case '*': case '+': case '?':
                    throw new Unsupported();
                default:
                    _pos++;
                    return new() { new() { new(Kind.Literal, c.ToString()) } };
            }
        }
    }
}
