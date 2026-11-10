using System;
using System.Globalization;

namespace Backspace.Services;

/// <summary>
/// The time each saved log line was written (U176, 10 November 2026).
/// Run log files and the background service's live transcript start each
/// engine line with the UTC time it was written, so Activity &amp; logs can
/// show it in its Timestamps column (in local time) and the file opened in
/// a text editor reads like an ordinary timestamped log:
/// <c>2026-11-10T06:00:00.569Z [setup] …</c>. Lines without a stamp (older
/// logs, restore logs) are shown as they are, with an empty time.
/// </summary>
internal static class LogLineStamp
{
    /// <summary>Length of the stamp including its trailing space.</summary>
    internal const int Length = 25;
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    internal static string Prefix(DateTime utc) =>
        utc.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture) + " ";

    internal static string Stamp(string line, DateTime utc) => Prefix(utc) + line;

    /// <summary>True when <paramref name="line"/> starts with a stamp; gives the
    /// time (UTC) and the line without it.</summary>
    internal static bool TryParse(string line, out DateTime utc, out string rest)
    {
        utc = default; rest = line;
        if (!LooksStamped(line)) return false;
        if (!DateTime.TryParseExact(line.AsSpan(0, Length - 1), Format, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc))
            return false;
        rest = line.Substring(Length);
        return true;
    }

    /// <summary>The line without its stamp, or unchanged.</summary>
    internal static string Strip(string line) => LooksStamped(line) ? line.Substring(Length) : line;

    private static bool LooksStamped(string line) =>
        line.Length >= Length && line[4] == '-' && line[7] == '-' && line[10] == 'T' && line[13] == ':'
        && line[19] == '.' && line[23] == 'Z' && line[24] == ' ' && char.IsAsciiDigit(line[0]);
}

/// <summary>
/// Where a run, a check or a cleanup starts in a log (U176): one marker line,
/// <c>[section] local_c · backup</c>, optionally followed by a tab and where it
/// runs. Activity &amp; logs draws it as a thin rule with a bold header, so
/// several runs in one view don't blur together.
/// </summary>
internal static class LogSection
{
    internal const string Prefix = "[section] ";

    internal static string Line(string backupName, string operation, string? where = null) =>
        Prefix + backupName + " · " + operation + (string.IsNullOrWhiteSpace(where) ? "" : "\t" + where);
}
