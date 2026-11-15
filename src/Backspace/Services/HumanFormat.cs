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
    /// (seconds; minutes+seconds; hours+minutes; days+hours), with a space
    /// before each unit as everywhere else: "45 s", "4 min 05 s", "1 h 05 min".</summary>
    public static string Duration(TimeSpan d)
    {
        if (d.TotalSeconds < 60) return $"{d.TotalSeconds:0} s";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min {d.Seconds:00} s";
        if (d.TotalHours   < 24) return $"{(int)d.TotalHours} h {d.Minutes:00} min";
        return $"{(int)d.TotalDays} d {d.Hours:00} h";
    }

    /// <summary>
    /// "just now" / "5 min ago" / "3 h ago" / "12 d ago": how long ago
    /// something happened, for every card, tile and helper text. The units
    /// are written as in durations ("1 h 12 min"), with a space (SI style;
    /// owner, 15 November 2026: "why does it show 2d ago and 3h ago but for
    /// the duration it shows with the space"). The one place this wording
    /// lives.
    /// </summary>
    public static string Ago(DateTime utc) => Ago(utc, DateTime.UtcNow);

    /// <inheritdoc cref="Ago(DateTime)"/>
    public static string Ago(DateTime utc, DateTime nowUtc)
    {
        var d = nowUtc - utc;
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1))   return $"{(int)d.TotalMinutes} min ago";
        if (d < TimeSpan.FromDays(1))    return $"{(int)d.TotalHours} h ago";
        return $"{(int)d.TotalDays} d ago";
    }
}
