using System;

namespace Backspace.Services.Platform.Windows;

/// <summary>A scheduled dispatcher must remain alive for its worker to run.
/// Task Scheduler can terminate the dispatcher on battery or at its time limit;
/// a monotonic deadline then stops the worker instead of leaving it detached.</summary>
internal sealed class ServiceRunLease(TimeProvider? clock = null)
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private string? _runId;
    private long _renewed;
    private bool _expired;

    internal void Begin(string runId)
    {
        if (!Guid.TryParseExact(runId, "N", out _)) throw new ArgumentException("Invalid run identity.");
        _runId = runId;
        _renewed = _clock.GetTimestamp();
        _expired = false;
    }

    internal bool IsExpired => _runId is not null
        && (_expired || (_expired = _clock.GetElapsedTime(_renewed) >= Timeout));

    internal bool Renew(string runId)
    {
        if (_runId != runId || IsExpired) return false;
        _renewed = _clock.GetTimestamp();
        return true;
    }

    internal void Clear() { _runId = null; _expired = false; }
}
