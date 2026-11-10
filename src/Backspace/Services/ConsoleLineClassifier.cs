using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Backspace.Services;

/// <summary>
/// Gives each console line its kind (U176). The rules follow the real lines
/// Backspace writes: its own <c>[setup]</c>, <c>[progress]</c>, <c>[recap]</c>,
/// <c>[cleanup]</c>, <c>[backspace]</c> and <c>[orchestrator]</c> lines,
/// Backrest's <c>[backrest] op=… status=…</c> transitions, its leveled
/// records (<c>[restic] 20:00:03.582Z	debug	…</c>, <c>[tasklog] … warn …</c>),
/// restic's per-file errors (<c>[restic] {"item":…}</c>) and the restore
/// wizard's lines. A log file's header and outcome blocks are recognised by
/// <see cref="ConsoleLogParser"/>, which knows where a block starts.
/// </summary>
internal static class ConsoleLineClassifier
{
    // Backrest's zap records: "[restic] 21:31:49.992Z<TAB>debug<TAB>…".
    private static readonly Regex Leveled = new(
        @"^\[[\w-]+\] \d{2}:\d{2}:\d{2}(?:\.\d+)?Z?\s+(?<level>debug|info|warn|error|dpanic|panic|fatal)\s",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // Older engine format: "2026-04-30 14:00:00 INFO …".
    private static readonly Regex Dated = new(
        @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d+)? (?<level>[A-Z]+) ",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BackrestStatus = new(
        @"^\[backrest\] op=\d+ status=STATUS_(?<status>[A-Z_]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OnlineOnly = new(
        @"\bonline-only files? skipped\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Trouble = new(
        @"\b(could not|couldn't|can't|cannot|failed|incomplete|skipped|waits?|not ready|denied|missing|unable)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Failure = new(
        @"^\[orchestrator\] (non-retriable failure|attempt \d+ failed|transient retry budget exhausted)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Retry = new(
        @"^\[orchestrator\] (retry \d|transient retry|tolerable skip|cancelled)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NotReady = new(
        @"^\[orchestrator\] «[^»]+» (isn't|is not) set up\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RestoreDone = new(
        @"^\[restore\] done: status=(?<status>\w+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GenericError = new(
        @"^(Fatal|FATAL|Error|ERROR|error)\b[: ]|\bERROR\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GenericWarning = new(
        @"^(Warning|WARNING|warning)\b[: ]|\bWARN(ING)?\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static ConsoleLineKind Classify(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ConsoleLineKind.Blank;
        if (text.StartsWith(LogSection.Prefix, StringComparison.Ordinal)) return ConsoleLineKind.Section;
        if (text.StartsWith(BackupLiveProgress.FilePrefix, StringComparison.Ordinal)) return ConsoleLineKind.File;
        if (text.StartsWith(BackupLiveProgress.Prefix, StringComparison.Ordinal)) return ConsoleLineKind.Progress;
        if (text.StartsWith(BackupLiveProgress.RecapPrefix, StringComparison.Ordinal)
            || text.StartsWith(CleanupResult.Prefix, StringComparison.Ordinal))
            return ConsoleLineKind.Summary;
        if (text.StartsWith("[setup] ", StringComparison.Ordinal)) return ConsoleLineKind.Setup;

        if (Leveled.Match(text) is { Success: true } leveled)
            return leveled.Groups["level"].Value switch
            {
                "debug" => ConsoleLineKind.Detail,
                "info" => ConsoleLineKind.Setup,
                "warn" => ConsoleLineKind.Warning,
                _ => ConsoleLineKind.Error,
            };
        if (BackrestStatus.Match(text) is { Success: true } op)
            return op.Groups["status"].Value switch
            {
                "PENDING" or "INPROGRESS" => ConsoleLineKind.Detail,
                "SUCCESS" => ConsoleLineKind.Setup,
                "WARNING" or "USER_CANCELLED" or "SYSTEM_CANCELLED" => ConsoleLineKind.Warning,
                _ => ConsoleLineKind.Error,
            };
        if (text.StartsWith("[backrest] ", StringComparison.Ordinal))
            return Trouble.IsMatch(text) ? ConsoleLineKind.Warning : ConsoleLineKind.Plain;
        // restic's own record of a file it could not read.
        if (text.StartsWith("[restic] {", StringComparison.Ordinal))
            return text.Contains("\"message_type\":\"summary\"", StringComparison.Ordinal) ? ConsoleLineKind.Setup : ConsoleLineKind.Warning;
        if (text.StartsWith("[backspace] ", StringComparison.Ordinal))
            return OnlineOnly.IsMatch(text) ? ConsoleLineKind.Summary
                : Trouble.IsMatch(text) ? ConsoleLineKind.Warning : ConsoleLineKind.Plain;
        if (text.StartsWith("[orchestrator] ", StringComparison.Ordinal))
            return text.StartsWith("[orchestrator] attempt ", StringComparison.Ordinal) && text.Contains(" starting at ", StringComparison.Ordinal)
                ? (text.StartsWith("[orchestrator] attempt 1 ", StringComparison.Ordinal) ? ConsoleLineKind.Setup : ConsoleLineKind.Warning)
                // A destination without its sign-in or password: the cell fails.
                : NotReady.IsMatch(text) ? ConsoleLineKind.Error
                : Failure.IsMatch(text) ? ConsoleLineKind.Error
                : Retry.IsMatch(text) || Trouble.IsMatch(text) ? ConsoleLineKind.Warning
                : ConsoleLineKind.Plain;
        if (text.StartsWith("[restore] ", StringComparison.Ordinal))
        {
            if (RestoreDone.Match(text) is { Success: true } done)
                return done.Groups["status"].Value switch
                {
                    "Success" => ConsoleLineKind.Summary,
                    "Failed" => ConsoleLineKind.Error,
                    _ => ConsoleLineKind.Warning,
                };
            return text.StartsWith("[restore] error:", StringComparison.Ordinal)
                || text.StartsWith("[restore] failed:", StringComparison.Ordinal)
                || text.StartsWith("[restore] aborted:", StringComparison.Ordinal)
                ? ConsoleLineKind.Error : ConsoleLineKind.Plain;
        }
        if (text.StartsWith("[error] ", StringComparison.Ordinal) || text.StartsWith("[err] ", StringComparison.Ordinal))
            return ConsoleLineKind.Error;
        if (text.StartsWith("[skip] ", StringComparison.Ordinal)) return ConsoleLineKind.Warning;
        // Backspace's own notes about the view.
        if (text.StartsWith("[could not read log file", StringComparison.Ordinal)) return ConsoleLineKind.Error;
        if (text.StartsWith("[live-tail truncated", StringComparison.Ordinal)
            || text.StartsWith("[log truncated", StringComparison.Ordinal)
            || text.StartsWith("[The log file for this run", StringComparison.Ordinal)
            || text == "…" || text == "Loading…")
            return ConsoleLineKind.Notice;

        if (Dated.Match(text) is { Success: true } dated)
            return dated.Groups["level"].Value switch
            {
                "DEBUG" or "TRACE" => ConsoleLineKind.Detail,
                "WARN" or "WARNING" => ConsoleLineKind.Warning,
                "ERROR" or "FATAL" => ConsoleLineKind.Error,
                _ => ConsoleLineKind.Plain,
            };
        if (GenericError.IsMatch(text)) return ConsoleLineKind.Error;
        if (GenericWarning.IsMatch(text)) return ConsoleLineKind.Warning;
        return ConsoleLineKind.Plain;
    }

    /// <summary>A part of a line drawn differently from the rest of it.</summary>
    internal enum SpanKind { Tag, Percent, Path }
    internal readonly record struct Span(int Start, int Length, SpanKind Kind);

    private static readonly Regex Tag = new(@"^\[[\w-]+\] ", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Percent = new(@"^\[progress\] (?<p>\d{1,3}(?:\.\d)?%)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // A Windows path: a drive or a share, up to a character no Windows file
    // name can contain (":" after the drive ends "C:\…\calls.xml: unsupported").
    private static readonly Regex WindowsPath = new(
        @"(?<![\w\\])(?:[A-Za-z]:\\|\\\\[^\\\s:""]+\\)[^""«»\t·|<>*?:\r\n]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The parts of a line drawn apart: its <c>[tag]</c> (quieter),
    /// a progress line's percentage and file paths (brighter). Warnings and
    /// errors keep one colour, so their paths are not set apart.</summary>
    internal static IReadOnlyList<Span> Spans(string text, ConsoleLineKind kind)
    {
        var spans = new List<Span>(3);
        if (kind is ConsoleLineKind.Section or ConsoleLineKind.Blank or ConsoleLineKind.Notice) return spans;
        // Backrest's own time and level ("[tasklog] 20:05:02.000Z  error") are
        // quiet like the tag: the message after them is what to read.
        var tag = Leveled.Match(text) is { Success: true } leveled ? leveled : Tag.Match(text);
        if (tag.Success) spans.Add(new Span(0, tag.Value.TrimEnd().Length, SpanKind.Tag));
        if (kind == ConsoleLineKind.Progress && Percent.Match(text) is { Success: true } pct)
            spans.Add(new Span(pct.Groups["p"].Index, pct.Groups["p"].Length, SpanKind.Percent));
        if (kind is ConsoleLineKind.Plain or ConsoleLineKind.Setup or ConsoleLineKind.Meta or ConsoleLineKind.File
            or ConsoleLineKind.Detail or ConsoleLineKind.Progress)
        {
            var from = tag.Success ? tag.Length : 0;
            foreach (Match m in WindowsPath.Matches(text, from))
            {
                var length = m.Value.TrimEnd(' ', '.', ',', ';', ')').Length;
                if (length > 3) spans.Add(new Span(m.Index, length, SpanKind.Path));
            }
        }
        return spans;
    }

    /// <summary>A line's message for the error list: without its tag and
    /// Backrest's time and level, tabs shown as separators.</summary>
    internal static string Message(string text)
    {
        var m = Leveled.Match(text);
        string message;
        if (m.Success)
        {
            // Backrest's record: logger, message, fields. "task failed: <the
            // error field>" says it; the plan's id doesn't.
            var fields = text[m.Length..].Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var error = fields.Length > 0 && JsonError.Match(fields[^1]) is { Success: true } e
                ? System.Text.RegularExpressions.Regex.Unescape(e.Groups["e"].Value) : null;
            message = error is not null && fields.Length >= 2 ? fields[^2] + ": " + error
                : string.Join(" · ", fields);
        }
        else if (OutcomeField.Match(text) is { Success: true } field) message = field.Groups["v"].Value;
        else message = Tag.Match(text) is { Success: true } tag ? text[tag.Length..] : text;
        message = message.Replace('\t', ' ').Trim();
        return message.Length > 300 ? message[..300] + "…" : message;
    }

    private static readonly Regex JsonError = new(@"""error"":\s*""(?<e>(?:[^""\\]|\\.)*)""", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // "last error    : the destination drive is full"
    private static readonly Regex OutcomeField = new(@"^[a-z][a-z ()]*?\s+: (?<v>.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
