using System;
using System.Collections.Generic;

namespace Backspace.Services;

/// <summary>
/// Turns raw log text into console lines (U176): strips each line's stamp
/// (<see cref="LogLineStamp"/>) into its time, classifies it, recognises a
/// run log file's header block ("--- Backspace cell log ---", drawn as the
/// run's section header with its fields dim below) and outcome block
/// ("--- cell outcome ---", its status coloured), and makes a section header
/// of each <c>[section]</c> marker. Feed lines in order; the parser keeps the
/// little state a block needs, so a live stream can be fed chunk by chunk.
/// </summary>
internal sealed class ConsoleLogParser
{
    internal const string HeaderMarker = "--- Backspace cell log ---";
    internal const string OutcomeMarker = "--- cell outcome ---";

    private enum Block { None, Header, Outcome }
    private Block _block;
    private string _outcomeStatus = "";
    private ConsoleLine? _lastSection;
    // Whether anything but setup has been shown since the last section, so a
    // marker right after a file's header block names that section instead
    // of starting another.
    private bool _sectionHasContent;

    /// <summary>Parse a whole text (a saved run's log). <paramref name="fallbackTime"/>
    /// is used for lines without a stamp; null leaves their time empty.</summary>
    internal static List<ConsoleLine> ParseText(string text, DateTime? fallbackTime = null)
    {
        var parser = new ConsoleLogParser();
        var lines = new List<ConsoleLine>(Math.Max(16, text.Length / 80));
        int i = 0, n = text.Length;
        while (i < n)
        {
            var start = i;
            while (i < n && text[i] != '\n') i++;
            var end = i > start && text[i - 1] == '\r' ? i - 1 : i;
            if (parser.Feed(text.Substring(start, end - start), fallbackTime) is { } line) lines.Add(line);
            i++;
        }
        return lines;
    }

    /// <summary>One raw line; null when it adds nothing to show (the header
    /// marker, or a section marker that named the section before it).</summary>
    internal ConsoleLine? Feed(string raw, DateTime? fallbackTime)
    {
        DateTime? time = fallbackTime;
        var text = raw;
        if (LogLineStamp.TryParse(raw, out var utc, out var rest)) { time = utc.ToLocalTime(); text = rest; }

        if (text == HeaderMarker)
        {
            _block = Block.Header;
            _sectionHasContent = false;
            return _lastSection = new ConsoleLine("backup", ConsoleLineKind.Section, time) { FromHeader = true };
        }
        if (text == OutcomeMarker)
        {
            _block = Block.Outcome;
            _outcomeStatus = "";
            return new ConsoleLine(text, ConsoleLineKind.Meta, time);
        }
        if (_block != Block.None)
        {
            if (string.IsNullOrWhiteSpace(text)) { _block = Block.None; return new ConsoleLine("", ConsoleLineKind.Blank, time); }
            return BlockLine(text, time);
        }

        var kind = ConsoleLineClassifier.Classify(text);
        if (kind == ConsoleLineKind.Section)
        {
            var title = text[LogSection.Prefix.Length..];
            string? where = null;
            var tab = title.IndexOf('\t');
            if (tab >= 0) { where = title[(tab + 1)..].Trim(); title = title[..tab]; }
            if (_lastSection is { FromHeader: true } header && !_sectionHasContent)
            {
                header.Text = title; header.Subtitle = where; header.FromHeader = false;
                header.Time ??= time;
                return null;
            }
            _sectionHasContent = false;
            return _lastSection = new ConsoleLine(title, ConsoleLineKind.Section, time) { Subtitle = where };
        }
        if (kind is not (ConsoleLineKind.Setup or ConsoleLineKind.Meta or ConsoleLineKind.Blank or ConsoleLineKind.Detail))
            _sectionHasContent = true;
        return new ConsoleLine(text, kind, time);
    }

    private ConsoleLine BlockLine(string text, DateTime? time)
    {
        var colon = text.IndexOf(" : ", StringComparison.Ordinal);
        var key = colon > 0 ? text[..colon].Trim() : "";
        var value = colon > 0 ? text[(colon + 3)..].Trim() : "";
        if (_block == Block.Header)
        {
            if (_lastSection is { FromHeader: true } section)
            {
                if (key == "backup" && value.Length > 0) section.Text = value + " · backup";
                else if (key == "destination" && value.Length > 0) section.Subtitle = value;
                else if (key == "started (UTC)" && DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                             System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var started))
                    section.Time = started.ToLocalTime();
            }
            return new ConsoleLine(text, ConsoleLineKind.Meta, time);
        }
        // Outcome block: the status and what it says take its colour.
        if (key == "status") _outcomeStatus = value;
        var kind = key switch
        {
            "status" or "summary" => _outcomeStatus switch
            {
                "Failed" => ConsoleLineKind.Error,
                "Warning" or "Skipped" => ConsoleLineKind.Warning,
                "Success" => key == "status" ? ConsoleLineKind.Summary : ConsoleLineKind.Meta,
                _ => ConsoleLineKind.Meta,
            },
            "last error" => ConsoleLineKind.Error,
            _ => ConsoleLineKind.Meta,
        };
        if (key == "ended (UTC)" && DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var ended))
            time ??= ended.ToLocalTime();
        return new ConsoleLine(text, kind, time) { ListAsError = key is not ("status" or "summary") };
    }
}
