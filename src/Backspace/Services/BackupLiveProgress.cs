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

    internal sealed record Sample(double Percent, long TotalBytes, long BytesDone, long TotalFiles, long FilesDone, string? CurrentFiles = null);

    private readonly Func<long> _expectedBytes;
    private readonly Func<long?>? _sentBytes;
    private DateTime _lastEmitted = DateTime.MinValue;
    // Bytes sent over the last half minute, for the speed: over 5 seconds it
    // jumped between a few bytes and megabytes as restic sent its data in
    // packs (U133).
    private readonly System.Collections.Generic.Queue<(DateTime At, long Bytes)> _sent = new();
    internal static TimeSpan SpeedWindow = TimeSpan.FromSeconds(30);
    // Whether new data is being sent at all (U133, 4 November 2026): restic
    // reports what it adds only at the end, and the engine's writes include
    // its own bookkeeping (folder listings, index, lock) while it reads files
    // already stored. That bookkeeping comes in occasional bursts: on
    // local_c's resumed run, the amount sent grew in under a third of the
    // five-second steps while restic read stored files, by a few KB or a
    // burst of up to 875 KB. An upload, however slow, grows it at every step. So new data is being sent when the amount grew
    // by at least ContinuousBytes in nearly every step of the last minute
    // (an upload of 1 KB/s or more), or by FlowBytes in two minutes (a fast
    // destination, where packs of 16 MiB go out in bursts).
    private readonly System.Collections.Generic.Queue<(DateTime At, long Bytes)> _flow = new();
    internal static TimeSpan FlowWindow = TimeSpan.FromMinutes(2);
    internal const long FlowBytes = 4L << 20;
    internal const long ContinuousBytes = 4 << 10;
    internal static TimeSpan ContinuousWindow = TimeSpan.FromMinutes(1);
    // Bytes done over the last minutes, for the time left.
    private readonly System.Collections.Generic.Queue<(DateTime At, long Bytes)> _recent = new();
    private (long Files, long Bytes)? _totals;

    /// <summary>How far back the pace for the time left is measured.</summary>
    internal static TimeSpan PaceWindow = TimeSpan.FromMinutes(5);
    /// <summary>The recent pace that can only make the time left longer (U140).</summary>
    internal static TimeSpan RecentPaceWindow = TimeSpan.FromMinutes(1);
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
    internal BackupLiveProgress(Func<long> expectedBytes, Func<long?>? sentBytes, ChangePlanner? changes = null)
        : this(expectedBytes, sentBytes, changes is null ? null : () => changes.Plan, changes is null ? null : () => changes.PreviousPace) { }

    /// <param name="plan">What this backup will read (U144), once known.</param>
    /// <param name="previousPace">The pace the last backup measured, if any.</param>
    internal BackupLiveProgress(Func<long> expectedBytes, Func<long?>? sentBytes, Func<ChangePlan?>? plan, Func<(double FilesPerSecond, double BytesPerSecond)?>? previousPace)
    {
        _expectedBytes = expectedBytes;
        _sentBytes = sentBytes;
        _plan = plan;
        _previousPace = previousPace;
    }

    // ---- U144: progress of the work, once the changes are known ----
    private readonly Func<ChangePlan?>? _plan;
    private readonly Func<(double FilesPerSecond, double BytesPerSecond)?>? _previousPace;
    private ChangePlan? _activePlan;
    private DateTime _planSince;
    /// <summary>Bytes of changes that cost as much time as checking one
    /// unchanged file, fixed for the run so the percentage only grows.</summary>
    private double _bytesPerFile;
    private double _filesPerSecond, _bytesPerSecond;
    private bool _measuredFiles, _measuredBytes;
    private (DateTime At, long Unchanged, long Changed)? _lastWork;
    /// <summary>First guesses until the run measures its own: a walk of C:
    /// checks about 1,500 unchanged files a second; 5 MB/s is a common upload.</summary>
    internal const double DefaultFilesPerSecond = 1_500, DefaultBytesPerSecond = 5_000_000;
    /// <summary>How much a new five-second measure moves the pace.</summary>
    internal const double PaceWeight = 0.25;

    /// <summary>The pace this run measured, for the next run's first guess.</summary>
    internal (double FilesPerSecond, double BytesPerSecond)? MeasuredPace
        => _measuredFiles || _measuredBytes ? (_filesPerSecond, _bytesPerSecond) : null;

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
            _sent.Enqueue((nowUtc, stored));
            while (_sent.Count > 2 && nowUtc - _sent.ElementAt(1).At >= SpeedWindow) _sent.Dequeue();
            _flow.Enqueue((nowUtc, stored));
            while (_flow.Count > 2 && nowUtc - _flow.ElementAt(1).At >= FlowWindow) _flow.Dequeue();
            var flowing = stored - _flow.Peek().Bytes >= FlowBytes || Continuous(nowUtc);
            if (flowing && _sent.Peek() is var from && nowUtc > from.At && stored > from.Bytes)
                rate = (stored - from.Bytes) / (nowUtc - from.At).TotalSeconds;
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
            // The slower of that and the last minute's pace (U140, 5 November
            // 2026): after minutes of unchanged files read at hundreds of MB/s,
            // local_c reached a large changed file and said "about 2 min left"
            // while sending it at 3.8 MB/s with 18 GB to go. Restic can't say
            // which of the remaining files are new, so the estimate follows the
            // slowdown at once and recovers when the pace does.
            if (_recent.LastOrDefault(r => nowUtc - r.At >= RecentPaceWindow) is { At: var at } recent && at > first.At
                && sample.BytesDone >= recent.Bytes)
                pace = Math.Min(pace, Math.Max(1, sample.BytesDone - recent.Bytes) / (nowUtc - at).TotalSeconds);
            left = TimeSpan.FromSeconds((sample.TotalBytes - sample.BytesDone) / pace);
        }
        long expected = 0;
        try { expected = _expectedBytes(); } catch (Exception) { }
        if (PlanLine(sample, counting, nowUtc, sent, rate) is { } planned) return planned;
        return Format(sample, counting, expected, sent, rate, left);
    }

    /// <summary>The line once the changes are known (U144): the percentage and
    /// the time left are of the work (unchanged files to check, changed bytes
    /// to read and send), not of the source's size.</summary>
    private string? PlanLine(Sample sample, bool counting, DateTime nowUtc, long? sent, double? rate)
    {
        if (_activePlan is null)
        {
            ChangePlan? plan = null;
            try { plan = _plan?.Invoke(); } catch (Exception) { }
            if (plan is null) return null;
            (double FilesPerSecond, double BytesPerSecond)? previous = null;
            try { previous = _previousPace?.Invoke(); } catch (Exception) { }
            (_filesPerSecond, _bytesPerSecond) = previous ?? (DefaultFilesPerSecond, DefaultBytesPerSecond);
            _bytesPerFile = _bytesPerSecond / _filesPerSecond;
            (_activePlan, _planSince) = (plan, nowUtc);
        }
        var p = _activePlan;
        var (unchanged, changed) = p.Done(sample.CurrentFiles, sample.FilesDone, sample.BytesDone);
        var changedTotal = Math.Max(p.ChangedBytes, changed);
        MeasurePace(nowUtc, unchanged, changed);
        var total = p.UnchangedFiles * _bytesPerFile + changedTotal;
        var percent = total <= 0 ? 100 : 100 * (unchanged * _bytesPerFile + changed) / total;
        TimeSpan? left = nowUtc - _planSince >= TimeSpan.FromSeconds(20)
            ? TimeSpan.FromSeconds((p.UnchangedFiles - unchanged) / _filesPerSecond + (changedTotal - changed) / _bytesPerSecond)
            : null;
        static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
        var parts = new System.Collections.Generic.List<string>
        {
            Math.Clamp(percent, 0, 100).ToString("0.0", CultureInfo.InvariantCulture) + "%",
            "Backing up",
            "changes " + Of(changed, changedTotal, estimated: true),
            counting
                ? $"{Count(sample.FilesDone)} / ~{Count(p.Files)} files, ~{Bytes(p.TotalBytes)}"
                : $"{Count(sample.FilesDone)} / {Count(sample.TotalFiles)} files, {Bytes(sample.TotalBytes)}",
        };
        if (sent is { } s)
            parts.Add(rate is > 0 and var r ? $"{Bytes(s)} sent at {Bytes(Math.Max(1, (long)r))}/s" : $"{Bytes(s)} sent");
        if (left is { } l && l < TimeSpan.FromDays(30)) parts.Add(TimeLeft(l));
        return Prefix + string.Join(" · ", parts);
    }

    /// <summary>Shares each step's time between checking and reading changes in
    /// proportion to the current pace, and moves each pace towards what the
    /// step showed. A slowdown therefore shows within a few steps (U140).</summary>
    private void MeasurePace(DateTime nowUtc, long unchanged, long changed)
    {
        if (_lastWork is not { } last) { _lastWork = (nowUtc, unchanged, changed); return; }
        var seconds = (nowUtc - last.At).TotalSeconds;
        var du = Math.Max(0, unchanged - last.Unchanged);
        var dc = Math.Max(0, changed - last.Changed);
        _lastWork = (nowUtc, unchanged, changed);
        if (seconds <= 0 || du + dc == 0) return;
        var tu = du / _filesPerSecond;
        var tc = dc / _bytesPerSecond;
        if (du > 0)
        {
            var share = seconds * tu / (tu + tc);
            _filesPerSecond = Math.Max(1, (1 - PaceWeight) * _filesPerSecond + PaceWeight * du / share);
            _measuredFiles = true;
        }
        if (dc > 0)
        {
            var share = seconds * tc / (tu + tc);
            _bytesPerSecond = Math.Max(1, (1 - PaceWeight) * _bytesPerSecond + PaceWeight * dc / share);
            _measuredBytes = true;
        }
    }

    /// <summary>Whether the amount sent grew by at least <see cref="ContinuousBytes"/>
    /// in all but one of the steps of the last <see cref="ContinuousWindow"/>
    /// (at least six steps): an upload going on, however slow.</summary>
    private bool Continuous(DateTime nowUtc)
    {
        var recent = _flow.Where(s => nowUtc - s.At <= ContinuousWindow + TimeSpan.FromSeconds(1)).ToArray();
        if (recent.Length < 7) return false;
        var steps = recent.Zip(recent.Skip(1), (a, b) => b.Bytes - a.Bytes).ToArray();
        return steps.Count(d => d >= ContinuousBytes) >= steps.Length - 1;
    }

    internal static Sample? Parse(JsonElement? operationBackup)
    {
        if (operationBackup is not { ValueKind: JsonValueKind.Object } backup
            || !backup.TryGetProperty("lastStatus", out var last) || last.ValueKind != JsonValueKind.Object
            || !last.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Object) return null;
        // Every file restic is reading, one per line: a string keeps the
        // sample comparable, so an unchanged status is not shown again.
        string? current = null;
        if (status.TryGetProperty("currentFile", out var files) && files.ValueKind == JsonValueKind.Array)
            current = string.Join('\n', files.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.String).Select(f => f.GetString()));
        return new(Number(status, "percentDone") * 100, (long)Number(status, "totalBytes"), (long)Number(status, "bytesDone"),
            (long)Number(status, "totalFiles"), (long)Number(status, "filesDone"), current);
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
        // Compact, so it fits on the card (U133, 4 November 2026): "30 / ~178 GB"
        // and "88,211 / 109,320+ files" while restic is still counting. "~"
        // marks Backspace's own count of the source, not yet restic's; "+"
        // a file total that is still growing.
        if (!counting)
        {
            parts.Add(Of(sample.BytesDone, sample.TotalBytes, estimated: false));
            parts.Add($"{Count(sample.FilesDone)} / {Count(sample.TotalFiles)} files");
        }
        else
        {
            parts.Add(expected ? Of(sample.BytesDone, expectedBytes, estimated: true) : Bytes(sample.BytesDone));
            parts.Add($"{Count(sample.FilesDone)} / {Count(sample.TotalFiles)}+ files");
        }
        // A speed only while new data is being sent (see FlowBytes), however
        // slow: restic's own bookkeeping alone is no upload.
        if (sentBytes is { } sent)
            parts.Add(sentPerSecond is > 0 and var rate ? $"{Bytes(sent)} sent at {Bytes(Math.Max(1, (long)rate))}/s" : $"{Bytes(sent)} sent");
        if (left is { } remaining && remaining < TimeSpan.FromDays(30)) parts.Add(TimeLeft(remaining));
        return Prefix + string.Join(" · ", parts);
    }

    /// <summary>The size restic counted for this run ("190 GB"), once its count
    /// is complete: no "~" (Backspace's estimate) and no "+" (files still being
    /// counted). Null otherwise (U141).</summary>
    internal static string? CountedTotal(string detail)
    {
        var m = System.Text.RegularExpressions.Regex.Match(detail ?? "", @"· [^·/]+ / (?<total>[0-9][^·]*?) · [0-9,]+ / [0-9,]+ files");
        if (m.Success) return m.Groups["total"].Value;
        // With the changes known (U144): "504,701 / 601,922 files, 190 GB".
        m = System.Text.RegularExpressions.Regex.Match(detail ?? "", @"· [0-9,]+ / [0-9,]+ files, (?<total>[0-9][^·]*?)( ·|$)");
        return m.Success ? m.Groups["total"].Value.Trim() : null;
    }

    /// <summary>"30 / 178 GB", or "880 MB / 178 GB" when the units differ.</summary>
    internal static string Of(long done, long total, bool estimated)
    {
        var (a, b) = (Bytes(done), Bytes(total));
        var unit = b[(b.LastIndexOf(' ') + 1)..];
        var head = a.EndsWith(" " + unit, StringComparison.Ordinal) ? a[..^(unit.Length + 1)] : a;
        return $"{head} / {(estimated ? "~" : "")}{b}";
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

    /// <summary>New and changed files and the data added, from restic's
    /// summary of a finished backup (U142), or null without one.</summary>
    internal static (int New, int Changed, long Added)? Stats(JsonElement? operationBackup)
    {
        if (operationBackup is not { ValueKind: JsonValueKind.Object } backup
            || !backup.TryGetProperty("lastStatus", out var last) || last.ValueKind != JsonValueKind.Object
            || !last.TryGetProperty("summary", out var s) || s.ValueKind != JsonValueKind.Object) return null;
        return ((int)Number(s, "filesNew"), (int)Number(s, "filesChanged"), (long)Number(s, "dataAdded"));
    }

    private static readonly System.Text.RegularExpressions.Regex StatsRx = new(
        @"· ([\d,]+) new, ([\d,]+) changed, [\d,]+ unchanged · (\d+(?:\.\d)?) (B|KB|MB|GB|TB) added", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The same figures read back from a recap line, for runs saved
    /// before they were recorded (to the precision the line shows).</summary>
    internal static (int New, int Changed, long Added)? StatsFromRecap(string line)
    {
        if (!line.StartsWith(RecapPrefix, StringComparison.Ordinal) || StatsRx.Match(line) is not { Success: true } m) return null;
        var scale = Array.IndexOf(new[] { "B", "KB", "MB", "GB", "TB" }, m.Groups[4].Value);
        return (int.Parse(m.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[2].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture),
            (long)(double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) * Math.Pow(1024, scale)));
    }

    /// <summary>The recap's figures from the end of a run's log, or null.</summary>
    internal static (int New, int Changed, long Added)? StatsFromLog(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
            using var file = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
            file.Position = Math.Max(0, file.Length - 64 * 1024);
            using var reader = new System.IO.StreamReader(file);
            (int, int, long)? found = null;
            while (reader.ReadLine() is { } line)
                if (StatsFromRecap(line) is { } stats) found = stats;
            return found;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { return null; }
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
