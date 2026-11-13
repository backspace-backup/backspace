using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;

namespace Backspace.Services.Platform.Windows;

/// <summary>Bounded, read-only activity. Only the owned worker writes the feed;
/// desktop requests cannot supply progress, file paths or execution arguments.</summary>
internal static class ServiceActivity
{
    // Several runs at once (U156), each with its transcript tail.
    internal const int MaximumBytes = 2 * 1024 * 1024;
    internal sealed record Source(string Path, double Percent, bool Running, string Detail, string Phase);
    internal sealed record Destination(string Name, double Percent);
    internal sealed record Progress(string BackupId, double Percent, string Queued,
        Source[] Sources, Destination[] Destinations, RunRecord? Result = null, string TranscriptTail = "", string? LogRunId = null, long TranscriptEnd = 0,
        double QueuedSeconds = 0, string Activity = "", string ActivityStep = "", string ActivityWhere = "", double ActivityPercent = -1);
    internal const int TranscriptCharacters = 32768;
    internal sealed record Snapshot(int Version, string? RunId, string State, string? BackupId,
        string? Operation, DateTime? StartedUtc, Progress? Progress, string? Error = null, Snapshot[]? Runs = null, long Sequence = 0,
        long StateSequence = 0);

    /// <summary>The run of one backup in an activity report: the service runs
    /// several at once (U156). <see cref="Snapshot.Runs"/> lists each one
    /// running and the last one finished for each backup; the report itself
    /// is the run started last. Without one for this backup, the report
    /// itself, so a card can tell its own run has gone.</summary>
    internal static Snapshot? ForBackup(Snapshot? activity, string backupId)
    {
        if (activity?.Runs is not { Length: > 0 } runs) return activity;
        return runs.Where(r => r.BackupId == backupId)
            .OrderByDescending(r => r.State is "starting" or "running" or "stopping" or "unknown")
            .ThenByDescending(r => r.StartedUtc).FirstOrDefault() ?? activity;
    }

    /// <summary>True while any run of the report is going.</summary>
    internal static bool AnyRunning(Snapshot? activity) => activity is not null
        && (activity.State is "starting" or "running" or "stopping" || activity.Runs?.Any(r => r.State is "starting" or "running" or "stopping") == true);

    internal static Progress Capture(string backupId, BackupProgressService progress, string queued, RunRecord? result = null)
    {
        var current = progress.Snapshot(backupId);
        return new(backupId, current?.OverallPercent ?? 0, queued,
            current?.Sources.Values.Select(s => new Source(s.SourcePath, s.PercentComplete, s.IsRunning, s.Detail, s.Phase)).ToArray() ?? [],
            current?.Destinations.Values.Select(d => new Destination(d.Name, d.PercentComplete)).ToArray() ?? [],
            result is null ? null : Metadata(result), Activity: current?.Activity ?? "",
            ActivityStep: current?.ActivityStep ?? "", ActivityWhere: current?.ActivityWhere ?? "", ActivityPercent: current?.ActivityPercent ?? -1);
    }

    private static RunRecord Metadata(RunRecord result)
    {
        var copy = JsonSerializer.Deserialize<RunRecord>(JsonSerializer.SerializeToUtf8Bytes(result, ServiceRunAuthorization.Json), ServiceRunAuthorization.Json)!;
        copy.LogPath = "";
        foreach (var cell in copy.Cells) cell.LogPath = "";
        return copy;
    }

    internal static byte[] Encode<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, ServiceRunAuthorization.Json);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Background activity is too large to display.");
        return bytes;
    }

    internal static Progress ParseProgress(byte[] bytes, string backupId)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException();
        var progress = JsonSerializer.Deserialize<Progress>(bytes, ServiceRunAuthorization.Json) ?? throw new InvalidDataException();
        Validate(progress, backupId);
        return progress;
    }

    private static void Validate(Progress p, string backupId)
    {
        if (p.BackupId != backupId || !Percent(p.Percent) || p.Queued is null || p.Queued.Length > 4096
            || !double.IsFinite(p.QueuedSeconds) || p.QueuedSeconds < 0
            || p.Activity is null || p.Activity.Length > 4096 || p.ActivityStep is null || p.ActivityStep.Length > 4096
            || p.ActivityWhere is null || p.ActivityWhere.Length > 4096 || !(p.ActivityPercent == -1 || Percent(p.ActivityPercent))
            || p.TranscriptTail is null || p.TranscriptTail.Length > TranscriptCharacters
            || p.TranscriptEnd < (p.TranscriptTail?.Length ?? 0)
            || (p.LogRunId is not null && !Guid.TryParseExact(p.LogRunId, "N", out _))
            || p.Sources is null || p.Destinations is null || p.Sources.Length > 4096 || p.Destinations.Length > 4096
            || p.Sources.Any(s => s is null || string.IsNullOrEmpty(s.Path) || !Percent(s.Percent) || s.Detail is null || s.Phase is null)
            || p.Destinations.Any(d => d is null || string.IsNullOrEmpty(d.Name) || !Percent(d.Percent))
            || p.Sources.Select(s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != p.Sources.Length
            || p.Destinations.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != p.Destinations.Length
            || (p.Result is { } r && (r.BackupId != backupId || !Guid.TryParseExact(r.Id, "N", out _)
                || r.EndedUtc is null || r.EndedUtc < r.StartedUtc || r.LogPath != ""
                || r.Status is BackupRunStatus.NeverRun or BackupRunStatus.Running
                || r.Cells is null || r.Cells.Any(c => c is null || c.LogPath != ""))))
            throw new InvalidDataException("The background activity response is inconsistent.");
    }

    private static bool Percent(double value) => double.IsFinite(value) && value is >= 0 and <= 100;

    internal static Snapshot Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException();
        var value = JsonSerializer.Deserialize<Snapshot>(bytes, ServiceRunAuthorization.Json) ?? throw new InvalidDataException();
        Check(value);
        if (value.Runs is { } runs)
        {
            if (runs.Length > 256) throw new InvalidDataException("The background service could not report its activity.");
            foreach (var run in runs)
            {
                if (run is null || run.Runs is not null) throw new InvalidDataException("The background service could not report its activity.");
                Check(run);
            }
        }
        return value;
    }

    private static void Check(Snapshot value)
    {
        if (value.Version != 1 || value.Error is not null
            || value.State is not ("idle" or "starting" or "running" or "stopping" or "completed" or "failed" or "failed-to-start" or "interrupted" or "unknown")
            || (value.RunId is not null && !Guid.TryParseExact(value.RunId, "N", out _))
            || (value.BackupId is not null && (value.RunId is null || value.StartedUtc is null || string.IsNullOrWhiteSpace(value.BackupId)
                || value.Operation is not ("backup" or "check" or "prune"))))
            throw new InvalidDataException("The background service could not report its activity.");
        if (value.Progress is not null) Validate(value.Progress, value.BackupId ?? "");
    }

    internal static async Task<Snapshot> ReadAsync(ServiceWorkerBinding binding, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var pipe = await BackupServicePipe.ConnectAsync(binding.ServiceName + "-runs", binding.ServiceSid, TimeSpan.FromSeconds(2), deadline.Token).ConfigureAwait(false);
        await BackupServicePipe.WriteMessageAsync(pipe, Encode(new ServiceHostRequest(1, "activity")), TimeSpan.FromSeconds(2), deadline.Token).ConfigureAwait(false);
        return Parse(await BackupServicePipe.ReadResponseAsync(pipe, TimeSpan.FromSeconds(2), deadline.Token).ConfigureAwait(false));
    }

    /// <summary>The activity once it differs from <paramref name="known"/>
    /// (U224): the service answers when something changes, never on a timer;
    /// with <paramref name="stateOnly"/>, when a run starts, stops or ends.
    /// The service stopping breaks the connection, which ends the wait.</summary>
    internal static async Task<Snapshot> WaitAsync(ServiceWorkerBinding binding, long known, bool stateOnly, CancellationToken token)
    {
        using var pipe = await BackupServicePipe.ConnectAsync(binding.ServiceName + "-runs", binding.ServiceSid, TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        await BackupServicePipe.WriteMessageAsync(pipe, Encode(new ServiceHostRequest(1, stateOnly ? "state-wait" : "activity-wait",
                Revision: known.ToString(System.Globalization.CultureInfo.InvariantCulture))), TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        return Parse(await BackupServicePipe.ReadResponseAsync(pipe, Timeout.InfiniteTimeSpan, token).ConfigureAwait(false));
    }

    internal static async Task CancelAsync(ServiceWorkerBinding binding, string runId, CancellationToken token)
    {
        if (!Guid.TryParseExact(runId, "N", out _)) throw new ArgumentException("Invalid run identity.");
        // A Stop waits its turn instead of giving up after 3 seconds (U224).
        using var pipe = await BackupServicePipe.ConnectAsync(binding.ServiceName + "-runs", binding.ServiceSid, TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        await BackupServicePipe.WriteMessageAsync(pipe, Encode(new ServiceHostRequest(1, "cancel", RunId: runId)), TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        var reply = JsonSerializer.Deserialize<ServiceScheduledRun.Response>(await BackupServicePipe.ReadResponseAsync(pipe, TimeSpan.FromSeconds(15), token).ConfigureAwait(false), ServiceRunAuthorization.Json);
        if (reply is null || reply.Version != 1 || reply.Error is not (null or "not-running"))
            throw new IOException("The background backup has not confirmed the stop request.");
    }
}

/// <summary>At most two snapshots per second; never block the engine's stdout
/// handler on a desktop or write a progress file for every line.</summary>
internal sealed class ServiceWorkerActivity : IAsyncDisposable
{
    private readonly Stream _output;
    private readonly string _backupId;
    private readonly BackupProgressService _progress;
    private readonly BackupOrchestrator _orchestrator;
    private readonly IBackupEngine _engine;
    private readonly object _tailGate = new();
    private readonly System.Text.StringBuilder _tail = new();
    private long _tailEnd;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pump;
    private string _queued = "";
    private RunRecord? _result;
    private int _dirty = 1;

    internal ServiceWorkerActivity(Stream output, string backupId, BackupProgressService progress, BackupOrchestrator orchestrator, IBackupEngine engine)
    {
        _output = output; _backupId = backupId; _progress = progress; _orchestrator = orchestrator; _engine = engine;
        progress.Updated += Updated; orchestrator.RunQueuedReasonChanged += Queued;
        orchestrator.RunEnded += Ended;
        engine.LineWritten += Line;
        _pump = PumpAsync();
    }
    private void Updated(string id) { if (id == _backupId) Interlocked.Exchange(ref _dirty, 1); }
    private void Queued(string id, string reason) { if (id == _backupId) { Volatile.Write(ref _queued, reason); Updated(id); } }
    private void Ended(string id, RunRecord record) { if (id == _backupId) { Volatile.Write(ref _result, record); Updated(id); } }
    private void Line(string line)
    {
        lock (_tailGate)
        {
            // Each line starts with when it was written (U176), shown in the
            // console's Timestamps column.
            var stamp = LogLineStamp.Prefix(DateTime.UtcNow);
            var kept = line.AsSpan(Math.Max(0, line.Length - ServiceActivity.TranscriptCharacters));
            _tailEnd += (long)stamp.Length + kept.Length + Environment.NewLine.Length;
            // Avoid first allocating a copy of a potentially huge line.
            _tail.Append(stamp).Append(kept).AppendLine();
            // Trim in batches so verbose output does not shift a full buffer
            // for every individual line after the first overflow.
            if (_tail.Length > ServiceActivity.TranscriptCharacters) _tail.Remove(0, _tail.Length - ServiceActivity.TranscriptCharacters / 2);
            if (_tail.Length > 0 && char.IsLowSurrogate(_tail[0])) _tail.Remove(0, 1);
        }
        Updated(_backupId);
    }
    private async Task WriteAsync()
    {
        var value = ServiceActivity.Capture(_backupId, _progress, Volatile.Read(ref _queued), Volatile.Read(ref _result));
        string tail; long end; lock (_tailGate) { tail = _tail.ToString(); end = _tailEnd; }
        _orchestrator.TryGetRunningRecord(_backupId, out var running);
        value = value with { TranscriptTail = tail, LogRunId = value.Result?.Id ?? running?.Id, TranscriptEnd = end,
            QueuedSeconds = _orchestrator.QueuedTime(_backupId).TotalSeconds };
        await BackupServicePipe.WriteMessageAsync(_output, ServiceActivity.Encode(value), TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
    }
    private async Task PumpAsync()
    {
        var ticks = 0;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (Interlocked.Exchange(ref _dirty, 0) != 0 || ++ticks >= 20)
                { ticks = 0; await WriteAsync().ConfigureAwait(false); }
                await Task.Delay(500, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { AppLogger.For<ServiceWorkerActivity>().Warning(ex, "Live progress could not be sent; backup history remains authoritative"); }
    }
    public async ValueTask DisposeAsync()
    {
        _progress.Updated -= Updated; _orchestrator.RunQueuedReasonChanged -= Queued; _orchestrator.RunEnded -= Ended;
        _engine.LineWritten -= Line;
        _stop.Cancel(); await _pump.ConfigureAwait(false);
        try { await WriteAsync().ConfigureAwait(false); }
        catch (Exception ex) { AppLogger.For<ServiceWorkerActivity>().Warning(ex, "The final live progress could not be sent"); }
        _stop.Dispose();
    }
}
