using System;
using System.Threading.Tasks;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U235 (owner: "I said NO FILE LOCK, can't they use equivalent
/// mutexes from the OS?"): the gate that stops a backup running twice is the
/// system's named mutex on every platform, released from any thread.</summary>
public sealed class RuntimeGateHoldTests
{
    [Fact]
    public async Task A_named_mutex_is_held_once_and_released_from_another_thread()
    {
        var name = @"Global\Backspace-test-" + Guid.NewGuid().ToString("N");
        var first = RuntimeGate.TryHold(name);
        Assert.NotNull(first);
        Assert.Null(RuntimeGate.TryHold(name));
        await Task.Run(() => first!.Dispose());
        using var again = RuntimeGate.TryHold(name);
        Assert.NotNull(again);
    }
}
