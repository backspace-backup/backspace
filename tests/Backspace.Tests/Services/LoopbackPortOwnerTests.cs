using System;
using System.Net;
using System.Net.Sockets;
using Backspace.Services.Platform.Windows;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U152: the engine's port is checked to belong to the engine before
/// any password is sent to it.</summary>
public sealed class LoopbackPortOwnerTests
{
    [Fact]
    public void Names_the_process_listening_on_a_loopback_port_and_nobody_once_it_closes()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try { Assert.Equal(Environment.ProcessId, LoopbackPortOwner.Of(port)); }
        finally { listener.Stop(); }
        Assert.Null(LoopbackPortOwner.Of(port));
    }
}
