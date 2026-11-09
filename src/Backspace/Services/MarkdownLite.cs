using System;
using System.Collections.Generic;
using System.Text;

namespace Backspace.Services;

/// <summary>
/// The small part of Markdown that release notes use (U169): headings
/// (<c>#</c> to <c>######</c>), bullet lists (<c>-</c>, <c>*</c>, <c>+</c>),
/// paragraphs, <c>**bold**</c>, <c>`code`</c> and <c>[text](url)</c> links.
/// Anything else is shown as written. No HTML is interpreted and no image is
/// loaded: release notes come from GitHub and are displayed as text.
/// </summary>
public static class MarkdownLite
{
    public enum BlockKind { Heading, Bullet, Paragraph }
    public enum SpanKind { Text, Bold, Code, Link }

    /// <param name="Url">For a link: its address, only when it is https;
    /// otherwise null and the link is shown as plain text.</param>
    public sealed record Span(SpanKind Kind, string Text, string? Url = null);

    /// <param name="Level">Heading level (1 to 6); 0 for other blocks.</param>
    public sealed record Block(BlockKind Kind, int Level, IReadOnlyList<Span> Spans);

    public static IReadOnlyList<Block> Parse(string? markdown)
    {
        var blocks = new List<Block>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;
        var paragraph = new StringBuilder();
        void EndParagraph()
        {
            if (paragraph.Length == 0) return;
            blocks.Add(new(BlockKind.Paragraph, 0, Inline(paragraph.ToString())));
            paragraph.Clear();
        }

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0) { EndParagraph(); continue; }
            if (HeadingLevel(trimmed) is var level and > 0)
            {
                EndParagraph();
                blocks.Add(new(BlockKind.Heading, level, Inline(trimmed[level..].Trim().TrimEnd('#').TrimEnd())));
                continue;
            }
            if (trimmed.Length >= 2 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' ')
            {
                EndParagraph();
                blocks.Add(new(BlockKind.Bullet, 0, Inline(trimmed[2..].Trim())));
                continue;
            }
            // An indented line right after a bullet continues it.
            if (paragraph.Length == 0 && line.Length > trimmed.Length && blocks.Count > 0 && blocks[^1].Kind == BlockKind.Bullet)
            {
                var previous = blocks[^1];
                var merged = new List<Span>(previous.Spans) { new(SpanKind.Text, " ") };
                merged.AddRange(Inline(trimmed));
                blocks[^1] = previous with { Spans = merged };
                continue;
            }
            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(trimmed);
        }
        EndParagraph();
        return blocks;
    }

    private static int HeadingLevel(string line)
    {
        var level = 0;
        while (level < line.Length && line[level] == '#') level++;
        return level is >= 1 and <= 6 && level < line.Length && line[level] == ' ' ? level : 0;
    }

    /// <summary>Bold, code and links within one block. An unclosed marker is
    /// kept as written.</summary>
    public static IReadOnlyList<Span> Inline(string text)
    {
        var spans = new List<Span>();
        var plain = new StringBuilder();
        void Flush()
        {
            if (plain.Length == 0) return;
            spans.Add(new(SpanKind.Text, plain.ToString()));
            plain.Clear();
        }

        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '`' && text.IndexOf('`', i + 1) is var close and > 0)
            {
                Flush();
                spans.Add(new(SpanKind.Code, text[(i + 1)..close]));
                i = close + 1;
                continue;
            }
            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*' && text.IndexOf("**", i + 2, StringComparison.Ordinal) is var end and > 0
                && end > i + 2)
            {
                Flush();
                // Code inside bold is rare in notes: shown as bold text.
                foreach (var inner in Inline(text[(i + 2)..end]))
                    spans.Add(inner.Kind == SpanKind.Text ? inner with { Kind = SpanKind.Bold } : inner);
                i = end + 2;
                continue;
            }
            if (text[i] == '[' && Link(text, i) is { } link)
            {
                Flush();
                spans.Add(new(SpanKind.Link, link.Label, SafeUrl(link.Url)));
                i = link.End;
                continue;
            }
            plain.Append(text[i]);
            i++;
        }
        Flush();
        return spans;
    }

    private static (string Label, string Url, int End)? Link(string text, int start)
    {
        var closeLabel = text.IndexOf(']', start + 1);
        if (closeLabel < 0 || closeLabel + 1 >= text.Length || text[closeLabel + 1] != '(') return null;
        // Parentheses inside the address are balanced, as GitHub does.
        int closeUrl = -1, depth = 0;
        for (var i = closeLabel + 2; i < text.Length && closeUrl < 0; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && depth-- == 0) closeUrl = i;
        }
        if (closeUrl < 0) return null;
        var label = text[(start + 1)..closeLabel];
        var url = text[(closeLabel + 2)..closeUrl].Trim();
        return label.Length == 0 || url.Length == 0 ? null : (label, url, closeUrl + 1);
    }

    /// <summary>Only an absolute https address opens; anything else (a
    /// relative path, file:, javascript:, http:) is shown but not opened.</summary>
    public static string? SafeUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(uri.Host)
            ? uri.AbsoluteUri : null;
}
