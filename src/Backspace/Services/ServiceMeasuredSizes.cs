using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Backspace.Services;

/// <summary>
/// The size each source had in the last backup the background service ran.
/// The service keeps it in its own copy of the settings, so the card showed
/// the estimate ("~178 GB") after local_c's first background backup had
/// measured 177 GB (4 November 2026). Display only: read at most every 30
/// seconds, and anything unreadable just leaves the estimate.
/// </summary>
internal static class ServiceMeasuredSizes
{
    private static readonly object Gate = new();
    private static (DateTime At, Dictionary<string, Dictionary<string, long>> Sizes)? _cache;

    internal static long For(string backupId, string sourcePath)
    {
#if WINDOWS
        try
        {
            Dictionary<string, Dictionary<string, long>> sizes;
            lock (Gate)
            {
                if (_cache is not { } cached || DateTime.UtcNow - cached.At > TimeSpan.FromSeconds(30))
                    _cache = cached = (DateTime.UtcNow, Read());
                sizes = cached.Sizes;
            }
            return sizes.TryGetValue(backupId, out var bySource)
                && bySource.TryGetValue(sourcePath, out var bytes) ? bytes : 0;
        }
        catch (Exception) { return 0; }
#else
        return 0;
#endif
    }

    /// <summary>Forget the cached sizes, after a background run ends.</summary>
    internal static void Invalidate() { lock (Gate) _cache = null; }

#if WINDOWS
    private static Dictionary<string, Dictionary<string, long>> Read()
    {
        var result = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        if (Platform.Windows.SystemService.ForCurrentUser() is not { } system) return result;
        var path = Path.Combine(system.DataDirectory, "config.json");
        if (!File.Exists(path)) return result;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("backups", out var backups) || backups.ValueKind != JsonValueKind.Array) return result;
        foreach (var backup in backups.EnumerateArray())
        {
            if (!backup.TryGetProperty("id", out var id) || id.GetString() is not { } backupId) continue;
            if (!backup.TryGetProperty("sourceLastSizeBytes", out var sizes) || sizes.ValueKind != JsonValueKind.Object) continue;
            var bySource = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var size in sizes.EnumerateObject())
                if (size.Value.TryGetInt64(out var bytes) && bytes > 0) bySource[size.Name] = bytes;
            result[backupId] = bySource;
        }
        return result;
    }
#endif
}
