using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services;

public enum RestoreControlState { Running, Pausing, Paused, Finishing, Complete }

/// <summary>A pause stops the owned transfer before acknowledging Paused.
/// The operation's repository lease remains held until resume or cancellation.</summary>
public sealed class RestoreControl
{
    private readonly object _sync = new();
    private CancellationTokenSource _pause = new();
    private readonly List<CancellationTokenSource> _retired = new();
    private TaskCompletionSource? _resume;
    private RestoreControlState _state;
    internal bool RollbackRequired { get; private set; }
    internal bool WasCancelled { get; private set; }
    internal string? CompletionError { get; private set; }
    internal RestoreOutputTransaction Output { get; private set; } = new();
    public RestoreControlState State { get { lock (_sync) return _state; } }
    public event Action? Changed;

    public RestoreControl(bool paused = false)
    {
        if (!paused) return;
        _state = RestoreControlState.Paused;
        _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pause.Cancel();
    }

    public void Pause()
    {
        CancellationTokenSource signal;
        lock (_sync)
        {
            if (_state != RestoreControlState.Running) return;
            _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _state = RestoreControlState.Pausing;
            signal = _pause;
        }
        // Persist the intent before interrupting the transfer. A failed durable
        // write leaves the running transfer alone and makes Pause fail visibly.
        try { Changed?.Invoke(); }
        catch
        {
            lock (_sync)
            {
                if (_state is RestoreControlState.Pausing or RestoreControlState.Paused) _state = RestoreControlState.Running;
                _resume?.TrySetResult(); _resume = null;
            }
            throw;
        }
        try { signal.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Resume()
    {
        TaskCompletionSource? resume;
        lock (_sync)
        {
            if (_state != RestoreControlState.Paused) return;
            resume = _resume;
            _state = RestoreControlState.Running;
        }
        try { Changed?.Invoke(); }
        catch { lock (_sync) { if (_state == RestoreControlState.Running) _state = RestoreControlState.Paused; } throw; }
        lock (_sync)
        {
            if (_state == RestoreControlState.Complete) return;
            _retired.Add(_pause); _pause = new(); _resume = null;
        }
        resume?.TrySetResult();
    }

    internal async Task WaitWhilePausedAsync(CancellationToken ct)
    {
        Task? wait; bool changed;
        lock (_sync)
        {
            changed = _state == RestoreControlState.Pausing;
            if (changed) _state = RestoreControlState.Paused;
            wait = _state == RestoreControlState.Paused ? _resume?.Task : null;
        }
        if (changed) Changed?.Invoke();
        if (wait is not null) await wait.WaitAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    internal CancellationTokenSource TransferCancellation(CancellationToken ct)
    {
        lock (_sync) return CancellationTokenSource.CreateLinkedTokenSource(ct, _pause.Token);
    }

    internal void BeginPublishing()
    {
        lock (_sync)
        {
            // If Pause won the race, the caller must wait before publishing.
            if (_state is RestoreControlState.Pausing or RestoreControlState.Paused) return;
            _state = RestoreControlState.Finishing;
        }
        Changed?.Invoke();
    }

    internal void ContinueNextBatch()
    {
        lock (_sync) { if (_state != RestoreControlState.Finishing) return; _state = RestoreControlState.Running; }
        Changed?.Invoke();
    }

    internal void Complete(bool cancelled)
    {
        if (State == RestoreControlState.Complete) return;
        WasCancelled = cancelled;
        try { if (cancelled || RollbackRequired) Output.Rollback(); else Output.Commit(); }
        catch (Exception ex) { CompletionError = ex.Message; throw; }
        finally
        {
            lock (_sync)
            {
                _state = RestoreControlState.Complete;
                _resume?.TrySetResult(); _resume = null;
                _pause.Dispose(); foreach (var old in _retired) old.Dispose(); _retired.Clear();
            }
            Changed?.Invoke();
        }
    }

    internal void Failed() => RollbackRequired = true;

    internal void ReleaseVerifiedBatch()
    {
        Output.Commit();
        Output = new();
    }
}
