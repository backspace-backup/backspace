using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// Size estimates measured by the background service (U106, 4 November 2026).
/// The app runs as the person and can't open some folders (another account's
/// profile, for example), so its estimate leaves them out although the
/// service's backup includes them. The service lists the same folders with
/// the same rights as the backup and returns only totals: bytes, files and
/// how many folders even it couldn't read.
/// </summary>
internal sealed class ServiceSizeMeasurements(string? dataDirectory = null)
{

    internal sealed record Reply(int Version, string? RunId = null, string? State = null, long Bytes = 0, long Files = 0,
        bool Truncated = false, int Unreadable = 0, string? Error = null, long CloudOnly = 0);

    internal static TimeSpan MaxDuration = TimeSpan.FromMinutes(3);
    private const int MaxKept = 8;

    private readonly ConcurrentDictionary<string, (DateTime Started, Task<Reply> Work)> _runs = new();

    /// <summary>A local folder that exists; anything else is refused.</summary>
    internal static bool Acceptable(string? path)
        => !string.IsNullOrWhiteSpace(path) && path.Length < 1024 && Path.IsPathFullyQualified(path)
           && !path.StartsWith(@"\\", StringComparison.Ordinal) && Directory.Exists(path);

    internal byte[] Start(string path, SourceSizeRules? rules = null)
    {
        if (!Acceptable(path)) return JsonSerializer.SerializeToUtf8Bytes(new Reply(1, Error: "not-a-folder"));
        foreach (var old in _runs.OrderBy(r => r.Value.Started).SkipLast(MaxKept - 1)) _runs.TryRemove(old.Key, out _);
        var id = Guid.NewGuid().ToString("N");
        _runs[id] = (DateTime.UtcNow, Task.Run(() => Measure(path, rules ?? SourceSizeRules.None, dataDirectory)));
        return JsonSerializer.SerializeToUtf8Bytes(new Reply(1, id, "running"));
    }

    internal byte[] Result(string runId)
    {
        if (!_runs.TryGetValue(runId, out var run)) return JsonSerializer.SerializeToUtf8Bytes(new Reply(1, runId, Error: "unknown"));
        if (!run.Work.IsCompleted) return JsonSerializer.SerializeToUtf8Bytes(new Reply(1, runId, "running"));
        _runs.TryRemove(runId, out _);
        return JsonSerializer.SerializeToUtf8Bytes(run.Work.IsCompletedSuccessfully ? run.Work.Result with { RunId = runId }
            : new Reply(1, runId, Error: "failed"));
    }

    /// <summary>Counts as the app does, with the backup's own rules (U123),
    /// keeping the count where the service's backups find it (U129).</summary>
    internal static Reply Measure(string path, SourceSizeRules rules, string? dataDirectory = null)
    {
        if (dataDirectory is not null
            && new SizeEstimateCache(dataDirectory).CountOrReuse(path, rules, SizeEstimateCache.FreshFor, MaxDuration, CancellationToken.None) is { } kept)
            return new Reply(1, State: "done", Bytes: kept.Bytes, Files: kept.Files, Unreadable: kept.Unreadable, CloudOnly: kept.CloudOnly);
        var count = new SourceSizeProbe.Count();
        var finished = SourceSizeProbe.CountIncluded(TreeWalk.Root(path), rules, DateTime.UtcNow + MaxDuration, CancellationToken.None, count);
        return new Reply(1, State: "done", Bytes: count.Bytes, Files: count.Files, Truncated: !finished, Unreadable: count.Unreadable,
            CloudOnly: count.CloudOnly);
    }
}
