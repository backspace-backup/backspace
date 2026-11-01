using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>Called under LogStore's history transaction. A failed Windows
/// replacement must not delete the pending outcome or its retained predecessor.</summary>
internal static class RunHistoryFile
{
    internal static bool Exists(string path) => File.Exists(path) || RecoveryFiles(path).Any();

    internal static List<RunRecord> Read(string path, JsonSerializerOptions options)
    {
        var recovery = RecoveryFiles(path).ToArray();
        var files = File.Exists(path) ? new[] { path } : recovery.Where(p => IsKind(path, p, "previous"));
        // A pending file is a complete, flushed list, written before publication.
        // Later appends merge it before retention or another replacement occurs.
        files = files.Concat(recovery.Where(p => IsKind(path, p, "pending")));
        var records = new List<RunRecord>();
        foreach (var file in files)
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            records.AddRange(JsonSerializer.Deserialize<List<RunRecord>>(stream, options)
                ?? throw new JsonException("History contains no run list."));
        }
        return records.GroupBy(r => r.Id).Select(g => g.Last()).OrderByDescending(r => r.StartedUtc).ToList();
    }

    internal static void Write(string path, List<RunRecord> records, JsonSerializerOptions options,
        Action<string, string, string>? replace = null, Action<int>? delay = null)
    {
        var recoveries = RecoveryFiles(path).ToArray();
        var latest = recoveries.Select(p => Sequence(path, p)).DefaultIfEmpty(0).Max();
        var sequence = Math.Max(DateTime.UtcNow.Ticks, checked(latest + 1));
        var identity = $"{sequence:D19}-{Guid.NewGuid():N}.json";
        var pending = path + ".pending-" + identity;
        var temporary = pending + ".tmp";
        var previous = path + ".previous-" + identity;
        var staged = false;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, records, options);
                output.Flush(flushToDisk: true);
            }
            // An interrupted serialization must never look like recoverable history.
            File.Move(temporary, pending);
            staged = true;
            // The new flushed list already incorporates earlier pending lists.
            // Remove those BEFORE publication, and fail if any is still held.
            // Otherwise an undeletable stale pending file could override a newer
            // committed outcome on the next read. The new pending copy survives
            // a failure here, so no result depends on deleting the older copy.
            foreach (var old in recoveries.Where(p => IsKind(path, p, "pending"))) File.Delete(old);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) (replace ?? File.Replace)(pending, path, previous);
                    else File.Move(pending, path);
                    break;
                }
                catch (Exception ex) when (attempt < 5 && FileReplacementErrors.CanRetry(ex))
                {
                    // No delay on success; at most 1.55 seconds for a held file.
                    // Do not retry 1176/1177: Windows may already have renamed
                    // the old file. Both complete copies remain recoverable.
                    (delay ?? Thread.Sleep)(50 << attempt);
                }
            }
        }
        catch
        {
            if (!staged) { try { File.Delete(temporary); } catch { } }
            // Keep complete pending and previous lists on any publication failure.
            // Reads and the next append recover them under the same transaction.
            throw;
        }
        foreach (var file in recoveries.Append(previous))
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { AppLogger.For("RunHistory").Debug(ex, "Could not remove a retained history copy at {Path}", file); }
        }
    }

    private static bool IsKind(string path, string file, string kind) =>
        file.StartsWith(path + "." + kind + "-", StringComparison.OrdinalIgnoreCase);

    private static long Sequence(string path, string file)
    {
        var suffix = file[(path.Length + 1)..];
        var separator = suffix.IndexOf('-');
        if (separator < 0 || suffix[..separator] is not ("pending" or "previous")) return 0;
        var id = suffix[(separator + 1)..];
        return id.Length == 57 && id[19] == '-' && id.EndsWith(".json", StringComparison.Ordinal)
            && long.TryParse(id[..19], out var ticks) && ticks > 0 && ticks < DateTime.MaxValue.Ticks
            && Guid.TryParseExact(id.Substring(20, 32), "N", out _) ? ticks : 0;
    }

    private static IEnumerable<string> RecoveryFiles(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory)) return Array.Empty<string>();
        return Directory.EnumerateFiles(directory, Path.GetFileName(path) + ".*.json")
            .Where(p => Sequence(path, p) > 0).OrderBy(p => Sequence(path, p)).ThenBy(p => p, StringComparer.Ordinal).ToArray();
    }
}
