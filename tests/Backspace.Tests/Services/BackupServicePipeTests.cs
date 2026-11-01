using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services.Platform.Windows;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class BackupServicePipeTests
{
    private const string ServiceSid = "S-1-5-80-101-202-303-404-505";
    private const string OwnerSid = "S-1-5-21-101-202-303-1001";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    [Fact]
    public void DescriptorAllowsOnlyOwnerServiceAndSystemWithoutClientInstanceCreation()
    {
        var descriptor = new RawSecurityDescriptor(BackupServicePipe.SecurityDescriptor(ServiceSid, OwnerSid));
        Assert.Equal(ServiceSid, descriptor.Owner!.Value);
        Assert.True(descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
        Assert.Equal(3, descriptor.DiscretionaryAcl!.Count);
        foreach (CommonAce ace in descriptor.DiscretionaryAcl)
        {
            Assert.Contains(ace.SecurityIdentifier.Value, new[] { ServiceSid, OwnerSid, "S-1-5-18" });
            Assert.Equal(AceQualifier.AccessAllowed, ace.AceQualifier);
            if (ace.SecurityIdentifier.Value == OwnerSid)
            {
                Assert.Equal(BackupServicePipe.ClientAccess, (uint)ace.AccessMask);
                Assert.Equal(0, ace.AccessMask & 4); // Cannot create another server instance.
                Assert.Equal(0, ace.AccessMask & 0x000c0000); // No WRITE_DAC/WRITE_OWNER.
            }
        }
    }

    [Theory]
    [InlineData("S-1-5-18", OwnerSid)]
    [InlineData(ServiceSid, "S-1-1-0")]
    [InlineData(ServiceSid, ServiceSid)]
    public void RejectsUnsafeIdentities(string service, string owner) =>
        Assert.Throws<ArgumentException>(() => BackupServicePipe.SecurityDescriptor(service, owner));

    [Fact]
    public void DesktopCannotCreateTheServiceEndpoint() => Assert.Throws<SecurityException>(() =>
        BackupServicePipe.CreateListener("Backspace-test-" + Guid.NewGuid().ToString("N"), ServiceSid, OwnerSid));

    [Theory]
    [InlineData("../another-pipe")]
    [InlineData("server\\pipe")]
    [InlineData("")]
    public async Task InvalidNamesNeverConnect(string name) => await Assert.ThrowsAsync<ArgumentException>(() =>
        BackupServicePipe.ConnectAsync(name, ServiceSid, Timeout, CancellationToken.None));

    [Fact]
    public async Task FakeServiceIsRejectedBeforeAnyPayloadIsSent()
    {
        var name = "Backspace-test-" + Guid.NewGuid().ToString("N");
        using var fake = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(Timeout);
        var connected = fake.WaitForConnectionAsync(deadline.Token);
        await Assert.ThrowsAsync<SecurityException>(() => BackupServicePipe.ConnectAsync(name,
            ServiceSid, Timeout, deadline.Token));
        await connected;
        Assert.Equal(0, await fake.ReadAsync(new byte[1], deadline.Token));
    }

    [Fact]
    public async Task MissingServiceConnectionIsBounded() =>
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BackupServicePipe.ConnectAsync(
            "Backspace-absent-" + Guid.NewGuid().ToString("N"), ServiceSid,
            TimeSpan.FromMilliseconds(100), CancellationToken.None));

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(BackupServicePipe.MaximumMessageBytes + 1)]
    [InlineData(int.MaxValue)]
    public async Task InvalidLengthRejectedBeforeAllocatingBody(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupServicePipe.ReadMessageAsync(stream, Timeout, default));
        Assert.Equal(4, stream.Position);
    }

    [Fact]
    public async Task TruncatedMessageCannotLookSuccessful()
    {
        using var stream = new MemoryStream(new byte[] { 4, 0, 0, 0, 1, 2 });
        await Assert.ThrowsAsync<EndOfStreamException>(() => BackupServicePipe.ReadMessageAsync(stream, Timeout, default));
    }

    [Fact]
    public async Task ConsecutiveFramesRemainSeparate()
    {
        using var stream = new MemoryStream();
        await BackupServicePipe.WriteMessageAsync(stream, new byte[] { 1, 2, 3 }, Timeout, default);
        await BackupServicePipe.WriteMessageAsync(stream, new byte[] { 4, 5 }, Timeout, default);
        stream.Position = 0;
        Assert.Equal(new byte[] { 1, 2, 3 }, await BackupServicePipe.ReadMessageAsync(stream, Timeout, default));
        Assert.Equal(new byte[] { 4, 5 }, await BackupServicePipe.ReadMessageAsync(stream, Timeout, default));
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task SilentConnectedClientCannotHoldReadForever()
    {
        var name = "Backspace-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        using var deadline = new CancellationTokenSource(Timeout);
        var connected = server.WaitForConnectionAsync(deadline.Token);
        await client.ConnectAsync(deadline.Token);
        await connected;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BackupServicePipe.ReadMessageAsync(
            server, TimeSpan.FromMilliseconds(100), deadline.Token));
    }

    [Fact]
    public async Task OversizedOutgoingMessageWritesNothing()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupServicePipe.WriteMessageAsync(stream,
            new byte[BackupServicePipe.MaximumMessageBytes + 1], Timeout, default));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task ServerDoesNotDisconnectUntilTheClientReceivesTheWholeResponse()
    {
        var name = "Backspace-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(Timeout);
        var connected = server.WaitForConnectionAsync(deadline.Token);
        await client.ConnectAsync(deadline.Token); await connected;
        var message = new byte[300_000];
        new Random(42).NextBytes(message);
        var send = BackupServicePipe.SendResponseAsync(server, message, Timeout, deadline.Token);
        // No receiver yet: the server must not report completed delivery.
        Assert.False(send.IsCompleted);
        var received = await BackupServicePipe.ReadResponseAsync(client, Timeout, deadline.Token);
        await send; server.Disconnect();
        Assert.Equal(message, received);
    }

    [Fact]
    public async Task AClientThatDoesNotAcknowledgeCannotHoldTheServiceForever()
    {
        var name = "Backspace-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(Timeout);
        var connected = server.WaitForConnectionAsync(deadline.Token);
        await client.ConnectAsync(deadline.Token); await connected;
        var send = BackupServicePipe.SendResponseAsync(server, new byte[] { 1, 2 }, TimeSpan.FromMilliseconds(150), deadline.Token);
        Assert.Equal(new byte[] { 1, 2 }, await BackupServicePipe.ReadMessageAsync(client, Timeout, deadline.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
    }
}
