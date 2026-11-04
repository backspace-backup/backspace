using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Backspace.Services;

/// <summary>
/// Sizes counted in the background service's folder, shared by the service
/// (which counts for the app) and its backup worker (which needs the size a
/// backup is expected to read), so the same folder isn't counted twice
/// (U129, 4 November 2026: "we shouldn't count twice the same thing").
/// Totals only, keyed by folder and backup rules; a lost update is harmless.
/// </summary>
internal sealed class SizeEstimateCache(string directory)
{
    internal const string FileName = "size-estimates.json";

    /// <summary>How long a count is reused: long enough not to count the same
    /// folder twice in a row, short enough never to show an old figure.</summary>
    internal static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(10);
    internal sealed record Entry(long Bytes, long Files, int Unreadable, long CloudOnly, DateTime CountedUtc);

    private string PathOf => Path.Combine(directory, FileName);

    internal static string Key(string path, SourceSizeRules rules) => TreeWalk.Root(path).ToLowerInvariant() + "|" + rules.Key;

    /// <summary>A count no older than <paramref name="maxAge"/>, or null.</summary>
    internal Entry? Read(string key, TimeSpan maxAge)
        => Load().TryGetValue(key, out var entry) && DateTime.UtcNow - entry.CountedUtc <= maxAge ? entry : null;

    internal void Write(string key, Entry entry)
    {
        try
        {
            var all = Load();
            all[key] = entry;
            // Keep it small: the most recent counts only.
            if (all.Count > 64)
                foreach (var old in new List<KeyValuePair<string, Entry>>(all).FindAll(p => p.Value.CountedUtc < DateTime.UtcNow.AddDays(-7))) all.Remove(old.Key);
            Directory.CreateDirectory(directory);
            var temporary = PathOf + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(all));
            File.Move(temporary, PathOf, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private Dictionary<string, Entry> Load()
    {
        try
        {
            if (!File.Exists(PathOf)) return new();
            using var file = new FileStream(PathOf, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(file) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    /// <summary>Counts <paramref name="path"/> with <paramref name="rules"/>,
    /// or reuses a count no older than <paramref name="maxAge"/>; null when a
    /// count couldn't finish within <paramref name="limit"/>.</summary>
    internal Entry? CountOrReuse(string path, SourceSizeRules rules, TimeSpan maxAge, TimeSpan limit, System.Threading.CancellationToken ct)
    {
        var key = Key(path, rules);
        if (Read(key, maxAge) is { } fresh) return fresh;
        var count = new SourceSizeProbe.Count();
        if (!SourceSizeProbe.CountIncluded(TreeWalk.Root(path), rules, DateTime.UtcNow + limit, ct, count)) return null;
        var entry = new Entry(count.Bytes, count.Files, count.Unreadable, count.CloudOnly, DateTime.UtcNow);
        Write(key, entry);
        return entry;
    }
}
