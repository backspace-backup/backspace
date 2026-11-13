using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services.Platform.Windows;

/// <summary>One polling loop for the desktop, independent of the selected page.
/// A failed read retains the last known operation; it never declares success.</summary>
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
    /// sequence given (U224): the answer comes when something changes.</summary>
    private readonly Func<long, CancellationToken, Task<ServiceActivity.Snapshot?>>? _wait;

    /// <summary>Raised when the service's activity changed (a run started,
    /// moved or ended): background alerts look for news then (U224).</summary>
    internal static event Action? ActivityChanged;

    internal BackgroundServiceActivity(Func<CancellationToken, Task<ServiceActivity.Snapshot?>> read,
        Func<string, CancellationToken, Task> cancel, Func<long, CancellationToken, Task<ServiceActivity.Snapshot?>>? wait = null)
    { _read = read; _cancel = cancel; _wait = wait; }

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
        }, (id, token) => ServiceActivity.CancelAsync(ServiceWorkerBinding.LoadForOwner(root), id, token), async (known, token) =>
        {
            if (ServiceWorkerBinding.ForDesktop(root) is not { } binding) return null;
            if (SystemService.Status(binding.ServiceName) != System.ServiceProcess.ServiceControllerStatus.Running) return null;
            return binding.CanRun ? await ServiceActivity.WaitAsync(binding, known, token).ConfigureAwait(false) : null;
        });
        _ = monitor.PollAsync();
        return monitor;
    }

    internal async Task RefreshAsync(CancellationToken token, bool waitForChange = false)
    {
        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false)) return;
        var before = Current;
        try
        {
            var next = waitForChange && _wait is not null
                ? await _wait(Current?.Sequence ?? -1, token).ConfigureAwait(false)
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
        if (Current?.Sequence != before?.Sequence || (Current is null) != (before is null))
            try { ActivityChanged?.Invoke(); } catch { /* a listener can't stop the app */ }
    }

    internal Task CancelAsync(string runId, CancellationToken token) => _cancel(runId, token);

    /// <summary>Once a second while Backspace's window is shown (owner, U224:
    /// "If the window is visible then we do want to show up to date info"),
    /// every five seconds while it is hidden or minimised.</summary>
    internal static TimeSpan IdleOrBusyDelay(bool windowShown) =>
        windowShown ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);

    private static volatile bool _windowShown;
    private static event Action? WindowOpened;

    /// <summary>Whether Backspace's window is on screen; set by the app.
    /// Showing it asks the service at once instead of within five seconds.</summary>
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
                // Shown: asked every second, so progress moves. Hidden: one
                // request the service answers only when something changes,
                // so nothing runs while nothing happens (U224, owner: "the app
                // is not supposed to use any CPU when not in use").
                if (WindowShown || _wait is null)
                {
                    await RefreshAsync(_stop.Token).ConfigureAwait(false);
                    await WakeableDelay(IdleOrBusyDelay(WindowShown)).ConfigureAwait(false);
                    continue;
                }
                // Showing the window ends the wait at once: it then asks every second.
                using (var shown = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    void Shown() { try { shown.Cancel(); } catch (ObjectDisposedException) { } }
                    WindowOpened += Shown;
                    try { await RefreshAsync(shown.Token, waitForChange: true).ConfigureAwait(false); }
                    finally { WindowOpened -= Shown; }
                }
                if (WindowShown) continue;
                // No service to ask, or it can't be reached: look again later.
                if (Error is not null || Current is null) await WakeableDelay(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
                // A run moves several times a second: hidden, its news is taken every 10 seconds.
                else if (ServiceActivity.AnyRunning(Current)) await WakeableDelay(HiddenRunningInterval).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    internal static TimeSpan HiddenRunningInterval = TimeSpan.FromSeconds(10);

    /// <summary>Waits, or less when the window is shown again.</summary>
    private async Task WakeableDelay(TimeSpan delay)
    {
        var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Woken() => wake.TrySetResult();
        WindowOpened += Woken;
        try { await Task.WhenAny(Task.Delay(delay, _stop.Token), wake.Task).ConfigureAwait(false); }
        finally { WindowOpened -= Woken; }
        _stop.Token.ThrowIfCancellationRequested();
    }

    /// <summary>Tests: start the loop.</summary>
    internal void StartPolling() => _ = PollAsync();

    public void Dispose() => _stop.Cancel();
}
