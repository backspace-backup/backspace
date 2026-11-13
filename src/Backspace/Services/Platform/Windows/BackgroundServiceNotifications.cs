using System;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Backspace.Models;

namespace Backspace.Services.Platform.Windows;

/// <summary>Only the ordinary interactive app presents alerts. The service
/// retains a pending alert until a window was actually shown (or sound played).</summary>
internal sealed class BackgroundServiceNotifications : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<ServiceNotificationProtocol.Request, CancellationToken, Task<ServiceNotificationProtocol.Response>> _send;
    private readonly Func<BackgroundNotification, CancellationToken, Task> _present;
    private readonly Func<bool> _desktopReady;

    internal BackgroundServiceNotifications(
        Func<ServiceNotificationProtocol.Request, CancellationToken, Task<ServiceNotificationProtocol.Response>> send,
        Func<BackgroundNotification, CancellationToken, Task> present, Func<bool> desktopReady)
    { _send = send; _present = present; _desktopReady = desktopReady; }

    internal static BackgroundServiceNotifications? Start(NotificationService notifier)
    {
        var root = AppContext.BaseDirectory.TrimEnd('\\');
        if (!ServicePortableLayout.IsPortableConfig(root, AppPaths.ConfigRoot)) return null;
        var monitor = new BackgroundServiceNotifications(async (request, token) =>
        {
            if (ServiceWorkerBinding.ForDesktop(root) is not { } binding) return new(1);
            if (!binding.CanRun) return new(1);
            using var service = new ServiceController(binding.ServiceName);
            // The service is started by opening Backspace and stopped by Quit
            // (U100); polling never starts it again.
            if (service.Status != ServiceControllerStatus.Running) return new(1);
            return await ServiceNotificationProtocol.SendAsync(binding, request, token).ConfigureAwait(false);
        }, async (notice, token) =>
        {
            var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Recheck on the UI thread too: a Windows lock may have happened
            // while waiting for the pipe response.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!DesktopReady() || App.QuitRequested) return;
                var delivery = notifier.NotifyAsync(notice.Title, notice.Detail, notice.Severity,
                    () => Open(notice), null, new NotificationChannels { Toast = notice.Toast, Sound = notice.Sound, Email = false },
                    onPresented: () => shown.TrySetResult(), canPresent: DesktopReady);
                _ = delivery.ContinueWith(t => shown.TrySetException(t.Exception!.GetBaseException()),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            });
            await shown.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        }, DesktopReady);
        _ = monitor.PollAsync();
        return monitor;
    }

    internal async Task<bool> DeliverOneAsync(CancellationToken token)
    {
        if (!_desktopReady()) return false;
        var result = await _send(new(1, "take"), token).ConfigureAwait(false);
        if (result.Error is not null) throw new IOException("Pending alerts could not be read.");
        if (result.Claim is not { } claim) return false;
        if (!_desktopReady()) return false; // Lease expires; never acknowledge a hidden alert.
        await _present(claim.Notice, token).ConfigureAwait(false);
        var receipt = await _send(new(1, "ack", claim.Notice.Id, claim.Receipt), token).ConfigureAwait(false);
        if (!receipt.Accepted) throw new IOException("Alert receipt was not saved; it may be shown again.");
        return true;
    }

    private async Task PollAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var delivered = false;
                try { delivered = await DeliverOneAsync(_stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex) { AppLogger.For<BackgroundServiceNotifications>().Warning(ex, "Background alerts remain pending"); }
                // An accumulated backlog is spaced out so it does not open a
                // stack of alerts at sign-in. Otherwise it looks again when the
                // service's activity changes (a run ended), or every ten minutes:
                // nothing polls in between (U224).
                if (delivered) await Task.Delay(TimeSpan.FromSeconds(10), _stop.Token).ConfigureAwait(false);
                // Locked or signed out: alerts wait for the desktop, looked for twice a minute.
                else if (!_desktopReady()) await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token).ConfigureAwait(false);
                else await WaitForNewsAsync(_stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    internal static TimeSpan QuietLook = TimeSpan.FromMinutes(10);

    private static async Task WaitForNewsAsync(CancellationToken stop)
    {
        var news = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Activity() => news.TrySetResult();
        BackgroundServiceActivity.ActivityChanged += Activity;
        try
        {
            await Task.WhenAny(news.Task, Task.Delay(QuietLook, stop)).ConfigureAwait(false);
            // The alert is saved as the run ends: a moment for it to land.
            if (news.Task.IsCompleted) await Task.Delay(TimeSpan.FromSeconds(1), stop).ConfigureAwait(false);
        }
        finally
        {
            BackgroundServiceActivity.ActivityChanged -= Activity;
        }
    }

    internal static void Open(BackgroundNotification notice)
    {
        if (notice.BackupId is { } backup && notice.RunId is { } run) AppNavigator.OpenRun(backup, run);
        else AppNavigator.Go(ViewModels.NavItem.Settings);
        if (Avalonia.Application.Current is App app) app.RestoreMainWindow();
    }

    // Opening the input desktop is a read-only check. Locked or disconnected
    // sessions cannot open their default input desktop; errors retain alerts.
    internal static bool DesktopReady()
    {
        if (App.QuitRequested) return false;
        var desktop = OpenInputDesktop(0, false, 0x0001 /* DESKTOP_READOBJECTS */);
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var name = new StringBuilder(256);
            return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _)
                && string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder buffer, int length, out int needed);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(IntPtr desktop);
    public void Dispose() => _stop.Cancel();
}
