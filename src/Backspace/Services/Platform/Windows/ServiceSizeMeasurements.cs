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
internal sealed class ServiceSizeMeasurements
{
    internal sealed record Reply(int Version, string? RunId = null, string? State = null, long Bytes = 0, long Files = 0,
        bool Truncated = false, int Unreadable = 0, string? Error = null);

    internal static TimeSpan MaxDuration = TimeSpan.FromMinutes(3);
    private const int MaxKept = 8;

    private readonly ConcurrentDictionary<string, (DateTime Started, Task<Reply> Work)> _runs = new();

    /// <summary>A local folder that exists; anything else is refused.</summary>
    internal static bool Acceptable(string? path)
        => !string.IsNullOrWhiteSpace(path) && path.Length < 1024 && Path.IsPathFullyQualified(path)
           && !path.StartsWith(@"\\", StringComparison.Ordinal) && Directory.Exists(path);

    internal byte[] Start(string path)
    {
        if (!Acceptable(path)) return JsonSerializer.SerializeToUtf8Bytes(new Reply(1, Error: "not-a-folder"));
        foreach (var old in _runs.OrderBy(r => r.Value.Started).SkipLast(MaxKept - 1)) _runs.TryRemove(old.Key, out _);
        var id = Guid.NewGuid().ToString("N");
        _runs[id] = (DateTime.UtcNow, Task.Run(() => Measure(path)));
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

    internal static Reply Measure(string path)
    {
        long bytes = 0, files = 0; var unreadable = 0;
        var finished = TreeWalk.Walk(TreeWalk.Root(path), (entry, _) =>
        {
            if (entry.IsDirectory) return false;
            Interlocked.Add(ref bytes, entry.Length);
            Interlocked.Increment(ref files);
            return null;
        }, DateTime.UtcNow + MaxDuration, CancellationToken.None, _ => Interlocked.Increment(ref unreadable));
        return new Reply(1, State: "done", Bytes: bytes, Files: files, Truncated: !finished, Unreadable: unreadable);
    }
}
