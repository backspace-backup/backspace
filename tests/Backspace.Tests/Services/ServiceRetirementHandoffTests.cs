using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services.Platform.Windows;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U050: turning administrator access off asks Windows once. The approved
/// process freezes the service, then waits for the desktop to take its
/// sign-ins back before it removes anything.
/// </summary>
public sealed class ServiceRetirementHandoffTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bs-retire-" + Guid.NewGuid().ToString("N"));
    private string Config => ServicePortableLayout.Config(_root);
    private string Prepared => Path.Combine(Config, ServiceRetirement.PreparedHintFile);
    private string Returned => Path.Combine(Config, ServiceRetirement.ReturnedHintFile);

    public ServiceRetirementHandoffTests() => Directory.CreateDirectory(Config);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private Task Start(TimeSpan timeout)
    {
        var waiting = Task.Run(() => ServiceRetirement.CompleteAfterReturn(_root, timeout));
        Assert.True(SpinWait.SpinUntil(() => File.Exists(Prepared), TimeSpan.FromSeconds(10)), "The approved process never said it was prepared.");
        return waiting;
    }

    [Fact]
    public async Task Cancelling_before_the_sign_ins_return_removes_nothing()
    {
        // A signal left by an earlier, interrupted attempt must not count.
        File.WriteAllText(Returned, "");
        var waiting = Start(TimeSpan.FromSeconds(30));
        await Task.Delay(600);
        Assert.False(waiting.IsCompleted, "The removal went ahead on a stale signal.");

        File.Delete(Prepared); // The desktop gave up.
        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
        Assert.Contains("Nothing was removed", ex.Message);
        Assert.False(File.Exists(Returned));
    }

    [Fact]
    public async Task Without_the_desktop_the_removal_stops_after_the_timeout()
    {
        var waiting = Start(TimeSpan.FromMilliseconds(500));
        var ex = await Assert.ThrowsAsync<System.TimeoutException>(() => waiting);
        Assert.Contains("Nothing was removed", ex.Message);
        Assert.False(File.Exists(Prepared));
    }

    [Fact]
    public async Task The_desktop_signal_lets_the_removal_go_on_to_its_verified_steps()
    {
        var waiting = Start(TimeSpan.FromSeconds(30));
        File.WriteAllText(Returned, "");
        // The next step checks administrator rights, the removal record, the
        // service's receipt and the returned file. None exists here, so it
        // refuses: proof that the wait ended and nothing was skipped.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => waiting);
        Assert.IsNotType<OperationCanceledException>(ex);
        Assert.IsNotType<System.TimeoutException>(ex);
        Assert.False(File.Exists(Prepared));
        Assert.False(File.Exists(Returned));
    }
}
