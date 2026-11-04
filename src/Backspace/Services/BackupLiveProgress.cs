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
///
/// Restic counts the files while it backs them up, so its totals are only
/// what it has found so far until they stop growing, and its bytes done
/// include files that need nothing sent. A first backup of C:\ showed 91%
/// and "under a minute left" with most of the drive still uncounted, at
/// "622 MB/s" to Google Drive (U124, 4 November 2026). Until the count is
/// done, the percentage is of the size expected (the last backup's, or the
/// estimate) or none at all, and no time left is guessed; the speed is of
/// the data actually sent to the storage.
/// </summary>
internal sealed class BackupLiveProgress
{
    internal const string Prefix = "[progress] ";
    internal static TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>How long restic's totals must stay unchanged before they
    /// count as final. Its progress is recorded every second or so.</summary>
    internal static TimeSpan CountSettles = TimeSpan.FromSeconds(30);

    internal sealed record Sample(double Percent, long TotalBytes, long BytesDone, long TotalFiles, long FilesDone);

    private readonly Func<long> _expectedBytes;
    private readonly Func<long?>? _sentBytes;
    private DateTime _lastEmitted = DateTime.MinValue;
    private (DateTime At, long Bytes)? _sentFrom;
    // Bytes done over the last minutes, for the time left.
    private readonly System.Collections.Generic.Queue<(DateTime At, long Bytes)> _recent = new();
    private (long Files, long Bytes)? _totals;

    /// <summary>How far back the pace for the time left is measured.</summary>
    internal static TimeSpan PaceWindow = TimeSpan.FromMinutes(5);
    private DateTime _totalsSince;
    private Sample? _last;

    /// <param name="expectedBytes">The size this backup is expected to read
    /// (the last backup's, or the estimate), or 0 when unknown.</param>
    /// <param name="sentBytes">How much the engine has stored so far, or null
    /// when it can't tell (measured from the engine's writes on Windows).</param>
    internal BackupLiveProgress(long expectedBytes = 0, Func<long?>? sentBytes = null)
        : this(() => expectedBytes, sentBytes) { }

    /// <param name="expectedBytes">The size expected so far: it can become
    /// known while the backup runs (the engine's own count of the source).</param>
    internal BackupLiveProgress(Func<long> expectedBytes, Func<long?>? sentBytes)
    {
        _expectedBytes = expectedBytes;
        _sentBytes = sentBytes;
    }

    /// <summary>The line to show now, or null when it is too soon or nothing changed.</summary>
    internal string? Next(JsonElement? operationBackup, DateTime nowUtc)
    {
        if (Parse(operationBackup) is not { } sample) return null;
        if (_totals != (sample.TotalFiles, sample.TotalBytes))
            (_totals, _totalsSince) = ((sample.TotalFiles, sample.TotalBytes), nowUtc);
        var counting = nowUtc - _totalsSince < CountSettles;
        if (_last == sample || nowUtc - _lastEmitted < Interval) return null;
        _lastEmitted = nowUtc;
        _last = sample;

        long? sent = null;
        double? rate = null;
        try { sent = _sentBytes?.Invoke(); } catch (Exception) { }
        if (sent is { } stored)
        {
            if (_sentFrom is { } from && nowUtc > from.At && stored >= from.Bytes)
                rate = (stored - from.Bytes) / (nowUtc - from.At).TotalSeconds;
            _sentFrom = (nowUtc, stored);
        }

        // Time left from the pace of the last few minutes, once the total is
        // known (U115). Not the average since the start: files already stored
        // are read at hundreds of MB/s, new data at the upload's pace, and
        // local_c said "about 23 min left" with 89 GB to upload at about
        // 5 MB/s (4 November 2026).
        if (sample.BytesDone > 0) _recent.Enqueue((nowUtc, sample.BytesDone));
        // Keep one sample at least a window old as the starting point.
        while (_recent.Count > 2 && nowUtc - _recent.ElementAt(1).At >= PaceWindow) _recent.Dequeue();
        TimeSpan? left = null;
        if (!counting && _recent.Count >= 2 && _recent.Peek() is var first && nowUtc - first.At >= TimeSpan.FromSeconds(10)
            && sample.BytesDone > first.Bytes && sample.TotalBytes > sample.BytesDone)
        {
            var pace = (sample.BytesDone - first.Bytes) / (nowUtc - first.At).TotalSeconds;
            left = TimeSpan.FromSeconds((sample.TotalBytes - sample.BytesDone) / pace);
        }
        long expected = 0;
        try { expected = _expectedBytes(); } catch (Exception) { }
        return Format(sample, counting, expected, sent, rate, left);
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

    internal static string Format(Sample sample, bool counting, long expectedBytes, long? sentBytes, double? sentPerSecond, TimeSpan? left = null)
    {
        static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
        // While counting, a share of what was found so far would be too
        // optimistic: use the size expected (or what was found, if more),
        // or show no percentage yet.
        var expected = counting && expectedBytes > 0;
        if (expected) expectedBytes = Math.Max(expectedBytes, sample.TotalBytes);
        var percent = !counting ? sample.Percent : expected ? 100.0 * sample.BytesDone / expectedBytes : 0;
        var parts = new System.Collections.Generic.List<string>
        {
            Math.Clamp(percent, 0, 100).ToString("0.0", CultureInfo.InvariantCulture) + "%",
            sample.BytesDone == 0 && sample.FilesDone == 0 ? "Scanning" : "Backing up",
        };
        if (!counting)
        {
            parts.Add($"{Bytes(sample.BytesDone)} of {Bytes(sample.TotalBytes)}");
            parts.Add($"{Count(sample.FilesDone)} of {Count(sample.TotalFiles)} files");
        }
        else
        {
            parts.Add(expected ? $"{Bytes(sample.BytesDone)} of about {Bytes(expectedBytes)}" : $"{Bytes(sample.BytesDone)} done");
            parts.Add($"{Count(sample.FilesDone)} files done, {Count(sample.TotalFiles)} found so far (still counting)");
        }
        if (sentBytes is { } sent)
            parts.Add(sentPerSecond is > 0 and var rate ? $"{Bytes(sent)} sent at {Bytes((long)rate)}/s" : $"{Bytes(sent)} sent");
        if (left is { } remaining && remaining < TimeSpan.FromDays(30)) parts.Add(TimeLeft(remaining));
        return Prefix + string.Join(" · ", parts);
    }

    internal static string TimeLeft(TimeSpan left) =>
        left < TimeSpan.FromMinutes(1) ? "under a minute left"
        : left < TimeSpan.FromHours(1) ? $"about {Math.Ceiling(left.TotalMinutes):0} min left"
        : $"about {(int)left.TotalHours} h {left.Minutes:00} min left";

    // In units of 1,024, like the sizes on the backup card and in Windows:
    // the card said "about 177.69 GB" for the 190.8 billion bytes the
    // progress line called "190 GB" (U123).
    internal static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes; var unit = 0;
        while (value >= 1000 && unit < units.Length - 1) { value /= 1024; unit++; }
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

    private static readonly System.Text.RegularExpressions.Regex CheckedRx = new(
        @"^\[recap\] Backup complete: [\d,]+ files \((\d+(?:\.\d)?) (B|KB|MB|GB|TB)\) checked", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The size a finished backup read, from its recap line, to the
    /// precision the line shows (three figures), or null for any other line.</summary>
    internal static long? CheckedBytes(string line)
    {
        if (!line.StartsWith(RecapPrefix, StringComparison.Ordinal) || CheckedRx.Match(line) is not { Success: true } m) return null;
        var scale = Array.IndexOf(new[] { "B", "KB", "MB", "GB", "TB" }, m.Groups[2].Value);
        return (long)(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * Math.Pow(1024, scale));
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

    /// <summary>A file of a first backup, from <c>restic ls --json</c>: every
    /// file is new. Folders and the snapshot header are left out.</summary>
    internal static string? ListedFileLine(string json)
    {
        if (!json.StartsWith('{')) return null;
        try
        {
            using var node = JsonDocument.Parse(json);
            var root = node.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "file"
                || !root.TryGetProperty("path", out var path) || path.GetString() is not { Length: > 0 } value) return null;
            return FileLine("+    " + value);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>What the card shows for a progress line: the text after its percentage.</summary>
    internal static string? Detail(string line)
    {
        if (!line.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var separator = line.IndexOf(" · ", StringComparison.Ordinal);
        return separator < 0 ? null : line[(separator + 3)..];
    }
}
