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
    internal const int MaximumBytes = 512 * 1024;
    internal sealed record Source(string Path, double Percent, bool Running, string Detail, string Phase);
    internal sealed record Destination(string Name, double Percent);
    internal sealed record Progress(string BackupId, double Percent, string Queued,
        Source[] Sources, Destination[] Destinations, RunRecord? Result = null, string TranscriptTail = "", string? LogRunId = null, long TranscriptEnd = 0);
    internal const int TranscriptCharacters = 32768;
    internal sealed record Snapshot(int Version, string? RunId, string State, string? BackupId,
        string? Operation, DateTime? StartedUtc, Progress? Progress, string? Error = null);

    internal static Progress Capture(string backupId, BackupProgressService progress, string queued, RunRecord? result = null)
    {
        var current = progress.Snapshot(backupId);
        return new(backupId, current?.OverallPercent ?? 0, queued,
            current?.Sources.Values.Select(s => new Source(s.SourcePath, s.PercentComplete, s.IsRunning, s.Detail, s.Phase)).ToArray() ?? [],
            current?.Destinations.Values.Select(d => new Destination(d.Name, d.PercentComplete)).ToArray() ?? [],
            result is null ? null : Metadata(result));
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
        if (value.Version != 1 || value.Error is not null
            || value.State is not ("idle" or "starting" or "running" or "stopping" or "completed" or "failed" or "failed-to-start" or "interrupted" or "unknown")
            || (value.RunId is not null && !Guid.TryParseExact(value.RunId, "N", out _))
            || (value.BackupId is not null && (value.RunId is null || value.StartedUtc is null || string.IsNullOrWhiteSpace(value.BackupId)
                || value.Operation is not ("backup" or "check" or "prune"))))
            throw new InvalidDataException("The background service could not report its activity.");
        if (value.Progress is not null) Validate(value.Progress, value.BackupId ?? "");
        return value;
    }

    internal static async Task<Snapshot> ReadAsync(ServiceWorkerBinding binding, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var pipe = await BackupServicePipe.ConnectAsync(binding.ServiceName + "-runs", binding.ServiceSid, TimeSpan.FromSeconds(2), deadline.Token).ConfigureAwait(false);
        await BackupServicePipe.WriteMessageAsync(pipe, Encode(new ServiceHostRequest(1, "activity")), TimeSpan.FromSeconds(2), deadline.Token).ConfigureAwait(false);
        return Parse(await BackupServicePipe.ReadResponseAsync(pipe, TimeSpan.FromSeconds(2), deadline.Token).ConfigureAwait(false));
    }

    internal static async Task CancelAsync(ServiceWorkerBinding binding, string runId, CancellationToken token)
    {
        if (!Guid.TryParseExact(runId, "N", out _)) throw new ArgumentException("Invalid run identity.");
        using var pipe = await BackupServicePipe.ConnectAsync(binding.ServiceName + "-runs", binding.ServiceSid, TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        await BackupServicePipe.WriteMessageAsync(pipe, Encode(new ServiceHostRequest(1, "cancel", RunId: runId)), TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        var reply = JsonSerializer.Deserialize<ServiceScheduledRun.Response>(await BackupServicePipe.ReadResponseAsync(pipe, TimeSpan.FromSeconds(3), token).ConfigureAwait(false), ServiceRunAuthorization.Json);
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
            _tailEnd += (long)line.Length + Environment.NewLine.Length;
            // Avoid first allocating a copy of a potentially huge line.
            _tail.Append(line.AsSpan(Math.Max(0, line.Length - ServiceActivity.TranscriptCharacters))).AppendLine();
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
        value = value with { TranscriptTail = tail, LogRunId = value.Result?.Id ?? running?.Id, TranscriptEnd = end };
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
