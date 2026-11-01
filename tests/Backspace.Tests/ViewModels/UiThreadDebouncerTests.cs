using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.ViewModels;

/// <summary>
/// The debouncer's contract is "fast-path on UI thread, debounce
/// off-thread, FlushNow runs synchronously, Dispose stops further
/// firings." These tests pin those down so a future refactor can't
/// silently regress them.
/// </summary>
public class UiThreadDebouncerTests
{
    [AvaloniaFact]
    public void Schedule_OnUiThread_RunsSynchronously()
    {
        // Tests, click handlers, and any other on-UI-thread Schedule()
        // call must run the action immediately. Without this contract
        // every existing synchronous Refresh() unit test (Backups,
        // Destinations, Logs) would have to manually pump the
        // dispatcher to observe state changes.
        int calls = 0;
        var d = new UiThreadDebouncer(() => calls++);
        d.Schedule();
        Assert.Equal(1, calls);
        d.Schedule();
        Assert.Equal(2, calls);
        d.Dispose();
    }

    [AvaloniaFact]
    public void FlushNow_RunsActionImmediately()
    {
        int calls = 0;
        var d = new UiThreadDebouncer(() => calls++, debounceMs: 10_000);
        d.FlushNow();
        // FlushNow uses Dispatcher.Send which queues at high priority;
        // pump jobs so the queued action runs in the test.
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Send);
        Assert.Equal(1, calls);
        d.Dispose();
    }

    [AvaloniaFact]
    public void Dispose_PreventsFurtherFirings()
    {
        int calls = 0;
        var d = new UiThreadDebouncer(() => calls++);
        d.Dispose();
        d.Schedule(); // no-op after dispose
        Assert.Equal(0, calls);
    }

    [AvaloniaFact]
    public async Task Schedule_OffUiThread_CoalescesIntoOneRun()
    {
        int calls = 0;
        using var d = new UiThreadDebouncer(() => calls++, debounceMs: 100);
        await Task.Run(() => { for (var i = 0; i < 20; i++) d.Schedule(); });
        for (var i = 0; i < 100 && calls == 0; i++) await Task.Delay(20);
        Assert.Equal(1, calls);
        await Task.Delay(200);
        Assert.Equal(1, calls); // No delayed duplicate from the burst.
        d.Dispose();
        await Task.Run(() => d.Schedule());
        await Task.Delay(200);
        Assert.Equal(1, calls); // A disposed instance cannot schedule work.
    }
}
