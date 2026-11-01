using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Threading;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Views;

public sealed class HeadlessDispatcherClaimTests
{
    // Reproduces the intermittent "a different thread owns it" setup failure:
    // a leftover background continuation reads Dispatcher.UIThread while the
    // per-test reset has cleared it, so that thread becomes the UI thread.
    [Fact]
    public async Task IsolatedSetupKeepsTheUiThreadWhenABackgroundThreadTouchesTheDispatcher()
    {
        using var stop = new CancellationTokenSource();
        var stray = new Thread(() =>
        {
            while (!stop.IsCancellationRequested) { _ = Dispatcher.UIThread; Thread.Sleep(0); }
        }) { IsBackground = true };
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        stray.Start();
        try
        {
            for (var i = 0; i < 20; i++)
                Assert.True(await session.Dispatch(() => Dispatcher.UIThread.CheckAccess(), TestContext.Current.CancellationToken));
        }
        finally { stop.Cancel(); stray.Join(); }
    }
}
