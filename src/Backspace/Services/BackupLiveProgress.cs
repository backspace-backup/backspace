using System;
using System.Globalization;
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
        return Format(sample, rate);
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

    internal static string Format(Sample sample, double? bytesPerSecond)
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
        return Prefix + string.Join(" · ", parts);
    }

    internal static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes; var unit = 0;
        while (value >= 1000 && unit < units.Length - 1) { value /= 1000; unit++; }
        return (unit == 0 ? value.ToString("0", CultureInfo.InvariantCulture) : value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture)) + " " + units[unit];
    }

    /// <summary>What the card shows for a progress line: the text after its percentage.</summary>
    internal static string? Detail(string line)
    {
        if (!line.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var separator = line.IndexOf(" · ", StringComparison.Ordinal);
        return separator < 0 ? null : line[(separator + 3)..];
    }
}
