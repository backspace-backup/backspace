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
    /// (seconds; minutes+seconds; hours+minutes; days+hours).</summary>
    public static string Duration(TimeSpan d)
    {
        if (d.TotalSeconds < 60) return $"{d.TotalSeconds:0}s";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m {d.Seconds:00}s";
        if (d.TotalHours   < 24) return $"{(int)d.TotalHours}h {d.Minutes:00}m";
        return $"{(int)d.TotalDays}d {d.Hours:00}h";
    }

    /// <summary>
    /// "just now" / "5m ago" / "3h ago" / "12d ago" — coarse-grained
    /// relative formatter for "when did this last happen" labels on
    /// cards, tiles, and grace-period helper text. Previously
    /// duplicated as a private static in BackupCardViewModel,
    /// BackupsViewModel, and OnboardingViewModel — three identical
    /// thresholds + format strings — until code-review flagged it.
    /// </summary>
    public static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1))   return $"{(int)d.TotalMinutes}m ago";
        if (d < TimeSpan.FromDays(1))    return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }
}
