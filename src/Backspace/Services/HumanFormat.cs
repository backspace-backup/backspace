using System;

namespace Backspace.Services;

/// <summary>
/// Engine-agnostic formatting helpers for byte counts and durations.
/// Surface text rendered by the GUI (Activity log, tile summaries,
/// notifications) goes through these so the same backup statistics
/// look identical no matter which IBackupEngine produced them.
///
/// Lives outside any concrete engine so callers can keep depending
/// on the same formatters across engine swaps.
/// </summary>
public static class HumanFormat
{
    /// <summary>Format a byte count using base-1024 binary prefixes
    /// (B / KB / MB / GB / TB). Matches what the restic and Restic
    /// CLIs print so users see consistent numbers across run logs and
    /// the GUI's run summaries.</summary>
    public static string Size(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }

    /// <summary>Format a duration as the most natural pair of units
    /// (seconds; minutes+seconds; hours+minutes; days+hours), compact and
    /// without padding zeros, as everywhere in Backspace (owner, 15 November
    /// 2026: "1d ago (no space), 1m ago ... about 10m left", and "don't show
    /// 07 in time periods"): "45s", "4m 5s", "1h 5m", "2d 3h"; a zero second
    /// part is left out ("4m", "1h").</summary>
    public static string Duration(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalSeconds < 60) return $"{(int)Math.Round(d.TotalSeconds)}s";
        if (d.TotalMinutes < 60) return Pair((int)d.TotalMinutes, "m", d.Seconds, "s");
        if (d.TotalHours   < 24) return Pair((int)d.TotalHours, "h", d.Minutes, "m");
        return Pair((int)d.TotalDays, "d", d.Hours, "h");
    }

    /// <summary>A rough duration, for a time that keeps moving (a run so
    /// far): "45s", "12m", "1h 5m".</summary>
    public static string Rough(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalSeconds < 60) return $"{(int)d.TotalSeconds}s";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m";
        return Duration(TimeSpan.FromMinutes(Math.Floor(d.TotalMinutes)));
    }

    private static string Pair(int big, string bigUnit, int small, string smallUnit)
        => small == 0 ? $"{big}{bigUnit}" : $"{big}{bigUnit} {small}{smallUnit}";

    /// <summary>
    /// "just now" / "5m ago" / "3h ago" / "12d ago": how long ago something
    /// happened, for every card, tile and helper text, in the same compact
    /// units as durations (owner, 15 November 2026). The one place this
    /// wording lives.
    /// </summary>
    public static string Ago(DateTime utc) => Ago(utc, DateTime.UtcNow);

    /// <inheritdoc cref="Ago(DateTime)"/>
    public static string Ago(DateTime utc, DateTime nowUtc)
    {
        var d = nowUtc - utc;
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1))   return $"{(int)d.TotalMinutes}m ago";
        if (d < TimeSpan.FromDays(1))    return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }
}
