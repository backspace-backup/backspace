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

    internal BackgroundServiceActivity(Func<CancellationToken, Task<ServiceActivity.Snapshot?>> read,
        Func<string, CancellationToken, Task> cancel) { _read = read; _cancel = cancel; }

    internal static BackgroundServiceActivity? ForDesktop()
    {
        var root = AppContext.BaseDirectory.TrimEnd('\\');
        if (!ServiceOwnerSecrets.Applies(root, AppPaths.ConfigRoot)) return null;
        var monitor = new BackgroundServiceActivity(async token =>
        {
            if (ServiceRetirement.Read(root) is not null) return null;
            var path = ServicePortableLayout.Record(root, ServiceWorkerBinding.FileName);
            try { _ = File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            var binding = ServiceWorkerBinding.LoadForOwner(root);
            return binding.CanRun ? await ServiceActivity.ReadAsync(binding, token).ConfigureAwait(false) : null;
        }, (id, token) => ServiceActivity.CancelAsync(ServiceWorkerBinding.LoadForOwner(root), id, token));
        _ = monitor.PollAsync();
        return monitor;
    }

    internal async Task RefreshAsync(CancellationToken token)
    {
        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false)) return;
        try
        {
            var next = await _read(token).ConfigureAwait(false);
            // A restarted service retains the interrupted host run ID but may
            // not know its display metadata. Preserve only this same run's
            // previously authenticated identity, never a different run.
            if (next is { BackupId: null, RunId: not null } && next.RunId == Current?.RunId)
                next = next with { BackupId = Current.BackupId, Operation = Current.Operation, StartedUtc = Current.StartedUtc };
            if (next is null && Current?.State is "starting" or "running" or "stopping" or "unknown")
                throw new IOException("The running background backup is no longer reachable.");
            Current = next; Error = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception)
        { Error = "Cannot reach the background backup service. Its last known activity is shown; completion has not been confirmed."; }
        finally { _gate.Release(); }
        try { Changed?.Invoke(); } catch { /* One view cannot stop polling. */ }
    }

    internal Task CancelAsync(string runId, CancellationToken token) => _cancel(runId, token);
    private async Task PollAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await RefreshAsync(_stop.Token).ConfigureAwait(false);
                await Task.Delay(1000, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public void Dispose() => _stop.Cancel();
}
