using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Backspace.Services;

/// <summary>
/// Turns the progress Backrest records on a running backup operation
/// (restic's status: percent, bytes and files done of the totals found so far)
/// into one readable line every few seconds (U112, 3 November 2026). Before,
/// only the operation's state was read, so a backup showed no progress at all
/// while it uploaded at full speed.
/// </summary>
internal sealed class BackupLiveProgress
{
    internal const string Prefix = "[progress] ";
    internal static TimeSpan Interval = TimeSpan.FromSeconds(5);

    internal sealed record Sample(double Percent, long TotalBytes, long BytesDone, long TotalFiles, long FilesDone);

    private DateTime _lastEmitted = DateTime.MinValue;
    private (DateTime At, long Bytes)? _rateFrom;
    private (DateTime At, long Bytes)? _firstMoving;
    private Sample? _last;

    /// <summary>The line to show now, or null when it is too soon or nothing changed.</summary>
    internal string? Next(JsonElement? operationBackup, DateTime nowUtc)
    {
        if (Parse(operationBackup) is not { } sample) return null;
        if (_last == sample || nowUtc - _lastEmitted < Interval) return null;
        double? rate = null;
        if (_rateFrom is { } from && nowUtc > from.At && sample.BytesDone >= from.Bytes)
            rate = (sample.BytesDone - from.Bytes) / (nowUtc - from.At).TotalSeconds;
        _rateFrom = (nowUtc, sample.BytesDone);
        _lastEmitted = nowUtc;
        _last = sample;
        // Time left from the average speed since data started moving, as
        // Duplicacy showed (U115). Totals still grow while restic scans, so
        // it is an estimate and says so.
        if (sample.BytesDone > 0) _firstMoving ??= (nowUtc, sample.BytesDone);
        TimeSpan? left = null;
        if (_firstMoving is { } first && nowUtc - first.At >= TimeSpan.FromSeconds(10) && sample.BytesDone > first.Bytes
            && sample.TotalBytes > sample.BytesDone)
        {
            var average = (sample.BytesDone - first.Bytes) / (nowUtc - first.At).TotalSeconds;
            left = TimeSpan.FromSeconds((sample.TotalBytes - sample.BytesDone) / average);
        }
        return Format(sample, rate, left);
    }

    internal static Sample? Parse(JsonElement? operationBackup)
    {
        if (operationBackup is not { ValueKind: JsonValueKind.Object } backup
            || !backup.TryGetProperty("lastStatus", out var last) || last.ValueKind != JsonValueKind.Object
            || !last.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Object) return null;
        return new(Number(status, "percentDone") * 100, (long)Number(status, "totalBytes"), (long)Number(status, "bytesDone"),
            (long)Number(status, "totalFiles"), (long)Number(status, "filesDone"));
    }

    // Protocol buffers write 64-bit numbers as strings in JSON.
    private static double Number(JsonElement element, string name)
        => !element.TryGetProperty(name, out var value) ? 0
            : value.ValueKind == JsonValueKind.Number ? value.GetDouble()
            : value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    internal static string Format(Sample sample, double? bytesPerSecond, TimeSpan? left = null)
    {
        var percent = Math.Clamp(sample.Percent, 0, 100);
        var parts = new System.Collections.Generic.List<string>
        {
            percent.ToString("0.0", CultureInfo.InvariantCulture) + "%",
            sample.BytesDone == 0 && sample.FilesDone == 0 ? "Scanning" : "Backing up",
            $"{Bytes(sample.BytesDone)} of {Bytes(sample.TotalBytes)}",
            $"{sample.FilesDone.ToString("N0", CultureInfo.InvariantCulture)} of {sample.TotalFiles.ToString("N0", CultureInfo.InvariantCulture)} files",
        };
        if (bytesPerSecond is > 0 and var rate) parts.Add(Bytes((long)rate) + "/s");
        if (left is { } remaining && remaining < TimeSpan.FromDays(30)) parts.Add(TimeLeft(remaining));
        return Prefix + string.Join(" · ", parts);
    }

    internal static string TimeLeft(TimeSpan left) =>
        left < TimeSpan.FromMinutes(1) ? "under a minute left"
        : left < TimeSpan.FromHours(1) ? $"about {Math.Ceiling(left.TotalMinutes):0} min left"
        : $"about {(int)left.TotalHours} h {left.Minutes:00} min left";

    internal static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes; var unit = 0;
        while (value >= 1000 && unit < units.Length - 1) { value /= 1000; unit++; }
        return (unit == 0 ? value.ToString("0", CultureInfo.InvariantCulture) : value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture)) + " " + units[unit];
    }

    internal const string RecapPrefix = "[recap] ";

    /// <summary>The closing summary of a finished backup, like the totals
    /// Duplicacy wrote at the end of its log (U112), or null when Backrest
    /// kept no summary (a failed or cancelled run).</summary>
    internal static string? Recap(JsonElement? operationBackup)
    {
        if (operationBackup is not { ValueKind: JsonValueKind.Object } backup
            || !backup.TryGetProperty("lastStatus", out var last) || last.ValueKind != JsonValueKind.Object
            || !last.TryGetProperty("summary", out var s) || s.ValueKind != JsonValueKind.Object) return null;
        string Count(string name) => ((long)Number(s, name)).ToString("N0", CultureInfo.InvariantCulture);
        var duration = TimeSpan.FromSeconds(Number(s, "totalDuration"));
        return RecapPrefix + string.Join(" · ",
            $"Backup complete: {Count("totalFilesProcessed")} files ({Bytes((long)Number(s, "totalBytesProcessed"))}) checked",
            $"{Count("filesNew")} new, {Count("filesChanged")} changed, {Count("filesUnmodified")} unchanged",
            $"{Bytes((long)Number(s, "dataAdded"))} added to the storage",
            duration.TotalHours >= 1 ? $"took {(int)duration.TotalHours}h {duration.Minutes:00}m" : $"took {duration.Minutes}m {duration.Seconds:00}s")
            // The snapshot it made, like Duplicacy's revision number (U115).
            + (s.TryGetProperty("snapshotId", out var id) && id.GetString() is { Length: >= 8 } snapshot ? $" · snapshot {snapshot[..8]}" : "");
    }

    internal const string FilePrefix = "[file] ";

    /// <summary>One line per file a backup added, changed or removed, from
    /// <c>restic diff</c> (U115). Other output
    /// (the totals restic prints at the end) is left out.</summary>
    internal static string? FileLine(string diffLine)
    {
        // restic prints "%-5s%v": the change, padded to five, then the path.
        if (diffLine.Length < 7 || diffLine[5] != '/') return null;
        var change = diffLine[..5].TrimEnd();
        if (change.Length == 0 || change.Any(c => c is not ('+' or '-' or 'M' or 'T' or 'U' or '?'))) return null;
        var path = diffLine[5..].TrimEnd();
        if (path.EndsWith('/')) return null; // folders: the files in them are listed
        // restic writes paths as /C/Users/…; show them as Windows paths.
        if (OperatingSystem.IsWindows() && path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == '/')
            path = path[1] + ":" + path[2..].Replace('/', '\\');
        var action = change.Contains('+') ? "added" : change.Contains('-') ? "removed" : change.Contains('M') ? "changed"
            : change.Contains('T') ? "type changed" : "details changed";
        return $"{FilePrefix}{action}: {path}";
    }

    /// <summary>What the card shows for a progress line: the text after its percentage.</summary>
    internal static string? Detail(string line)
    {
        if (!line.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var separator = line.IndexOf(" · ", StringComparison.Ordinal);
        return separator < 0 ? null : line[(separator + 3)..];
    }
}
