using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// What the background service is doing, for the desktop, independent of the
/// selected page (U224, owner, 13 November 2026: "I want this to be purely
/// event based! So no polling"). The app keeps one request open that the
/// service answers when something changes: while the window is shown, every
/// change (a run's progress, at most twice a second); while it is hidden, only
/// a run starting, stopping or ending. Nothing is asked on a timer, with one
/// exception, the watchdog: while a run is going, a worker reports at least
/// every <see cref="WorkerHeartbeat"/>; when nothing has been heard for
/// <see cref="Watchdog"/> (twelve times that), the app asks once whether the
/// run is still moving. A failed read retains the last known operation; it
/// never declares success.
/// </summary>
internal sealed class BackgroundServiceActivity : IDisposable
{
    private readonly Func<CancellationToken, Task<ServiceActivity.Snapshot?>> _read;
    private readonly Func<string, CancellationToken, Task> _cancel;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    internal ServiceActivity.Snapshot? Current { get; private set; }
    internal string? Error { get; private set; }
    internal event Action? Changed;

    /// <summary>Asks the service for its activity once it differs from the
    /// sequence given (U224): the answer comes when something changes; with
    /// stateOnly, when a run starts, stops or ends.</summary>
    private readonly Func<long, bool, CancellationToken, Task<ServiceActivity.Snapshot?>>? _wait;

    /// <summary>Raised when a run of the service started, stopped or ended
    /// (not for its progress): background alerts look for news then (U224).</summary>
    internal static event Action? ActivityChanged;

    /// <summary>A worker reports its run at least this often, even when nothing
    /// moves (ServiceWorkerActivity); the service gives up on one silent for a minute.</summary>
    internal static readonly TimeSpan WorkerHeartbeat = TimeSpan.FromSeconds(10);

    /// <summary>Silent this long while a run is going, the app asks once
    /// whether it still moves: twelve heartbeats.</summary>
    internal static TimeSpan Watchdog = WorkerHeartbeat * 12;

    internal BackgroundServiceActivity(Func<CancellationToken, Task<ServiceActivity.Snapshot?>> read,
        Func<string, CancellationToken, Task> cancel, Func<long, bool, CancellationToken, Task<ServiceActivity.Snapshot?>>? wait = null)
    { _read = read; _cancel = cancel; _wait = wait; }

    /// <summary>The app started something in the service (Run now): ask now,
    /// so a monitor that had nothing to wait for hears it.</summary>
    internal static void Poke() { try { Poked?.Invoke(); } catch { /* a listener can't stop the app */ } }
    private static event Action? Poked;

    internal static BackgroundServiceActivity? ForDesktop()
    {
        var root = AppContext.BaseDirectory.TrimEnd('\\');
        if (!ServicePortableLayout.IsPortableConfig(root, AppPaths.ConfigRoot)) return null;
        var monitor = new BackgroundServiceActivity(async token =>
        {
            if (ServiceWorkerBinding.ForDesktop(root) is not { } binding) return null;
            // A stopped service (after Quit) has nothing running.
            if (SystemService.Status(binding.ServiceName) != System.ServiceProcess.ServiceControllerStatus.Running) return null;
            return binding.CanRun ? await ServiceActivity.ReadAsync(binding, token).ConfigureAwait(false) : null;
        }, (id, token) => ServiceActivity.CancelAsync(ServiceWorkerBinding.LoadForOwner(root), id, token), async (known, stateOnly, token) =>
        {
            if (ServiceWorkerBinding.ForDesktop(root) is not { } binding) return null;
            if (SystemService.Status(binding.ServiceName) != System.ServiceProcess.ServiceControllerStatus.Running) return null;
            return binding.CanRun ? await ServiceActivity.WaitAsync(binding, known, stateOnly, token).ConfigureAwait(false) : null;
        });
        _ = monitor.PollAsync();
        return monitor;
    }

    internal async Task RefreshAsync(CancellationToken token, bool waitForChange = false, bool stateOnly = false)
    {
        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false)) return;
        var before = Current;
        try
        {
            var next = waitForChange && _wait is not null
                ? await _wait((stateOnly ? Current?.StateSequence : Current?.Sequence) ?? -1, stateOnly, token).ConfigureAwait(false)
                : await _read(token).ConfigureAwait(false);
            // A restarted service retains the interrupted host run ID but may
            // not know its display metadata. Preserve only this same run's
            // previously authenticated identity, never a different run.
            if (next is { BackupId: null, RunId: not null } && next.RunId == Current?.RunId)
                next = next with { BackupId = Current.BackupId, Operation = Current.Operation, StartedUtc = Current.StartedUtc };
            if (next is null && (Current?.State is "starting" or "running" or "stopping" or "unknown" || ServiceActivity.AnyRunning(Current)))
                throw new IOException("The running background backup is no longer reachable.");
            Current = next; Error = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception)
        { Error = "Cannot reach the background backup service. Its last known activity is shown; completion has not been confirmed."; }
        finally { _gate.Release(); }
        try { Changed?.Invoke(); } catch { /* One view cannot stop polling. */ }
        if (Current?.StateSequence != before?.StateSequence || (Current is null) != (before is null))
            try { ActivityChanged?.Invoke(); } catch { /* a listener can't stop the app */ }
    }

    internal Task CancelAsync(string runId, CancellationToken token) => _cancel(runId, token);

    /// <summary>Without a service that can wait (tests only): once a second
    /// while the window is shown, every five seconds while it is hidden.</summary>
    internal static TimeSpan IdleOrBusyDelay(bool windowShown) =>
        windowShown ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);

    private static volatile bool _windowShown;
    private static event Action? WindowOpened;

    /// <summary>Whether Backspace's window is on screen; set by the app.
    /// Showing it asks the service at once, for every change from then on.</summary>
    internal static bool WindowShown
    {
        get => _windowShown;
        set
        {
            var opened = value && !_windowShown;
            _windowShown = value;
            if (opened) try { WindowOpened?.Invoke(); } catch { /* a listener can't stop the app */ }
        }
    }

    private async Task PollAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (_wait is null)
                {
                    await RefreshAsync(_stop.Token).ConfigureAwait(false);
                    await WakeableDelay(IdleOrBusyDelay(WindowShown)).ConfigureAwait(false);
                    continue;
                }
                var shown = WindowShown;
                var running = ServiceActivity.AnyRunning(Current);
                var heard = Current?.Sequence;
                // One request, answered when something changes. Showing the
                // window ends a hidden wait at once: from then on every change.
                var watchdogFired = false;
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    void Shown() { try { wait.Cancel(); } catch (ObjectDisposedException) { } }
                    if (!shown) WindowOpened += Shown;
                    using var watchdog = running ? new Timer(_ => { watchdogFired = true; Shown(); }, null, Watchdog, Timeout.InfiniteTimeSpan) : null;
                    try { await RefreshAsync(wait.Token, waitForChange: true, stateOnly: !shown).ConfigureAwait(false); }
                    finally { WindowOpened -= Shown; }
                }
                _stop.Token.ThrowIfCancellationRequested();
                if (watchdogFired)
                {
                    // Silent for twelve heartbeats while a run goes: ask once.
                    LastWatchdogUtc = DateTime.UtcNow;
                    await RefreshAsync(_stop.Token).ConfigureAwait(false);
                    if (Error is null && ServiceActivity.AnyRunning(Current) && Current?.Sequence == heard)
                    {
                        Error = "The background backup has not reported any progress for a while. It may still be running; completion has not been confirmed.";
                        try { Changed?.Invoke(); } catch { /* One view cannot stop it. */ }
                    }
                    continue;
                }
                if (Error is not null || Current is null)
                {
                    // The service can't be reached (restarting, say): tried
                    // again after the watchdog's time, only while that lasts.
                    // No service at all: nothing to ask until the window is
                    // shown or Backspace starts a run there.
                    await WakeableDelay(Error is not null ? Watchdog : Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    /// <summary>When the watchdog last asked (tests).</summary>
    internal DateTime? LastWatchdogUtc { get; private set; }

    /// <summary>Waits, or less when the window is shown again or Backspace
    /// starts a run in the service.</summary>
    private async Task WakeableDelay(TimeSpan delay)
    {
        var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Woken() => wake.TrySetResult();
        WindowOpened += Woken;
        Poked += Woken;
        try { await Task.WhenAny(Task.Delay(delay, _stop.Token), wake.Task).ConfigureAwait(false); }
        finally { WindowOpened -= Woken; Poked -= Woken; }
        _stop.Token.ThrowIfCancellationRequested();
    }

    /// <summary>Tests: start the loop.</summary>
    internal void StartPolling() => _ = PollAsync();

    public void Dispose() => _stop.Cancel();
}
