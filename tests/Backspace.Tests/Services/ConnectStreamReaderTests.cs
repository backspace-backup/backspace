using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Pin the Connect-Go framed-stream format so a Backrest upgrade that
/// changes the wire framing (or a refactor that mis-counts bytes)
/// surfaces here as a clear failure rather than as live-progress
/// updates that silently never arrive.
/// </summary>
public class ConnectStreamReaderTests
{
    [Fact]
    public async Task EmptyStream_YieldsNothing()
    {
        // A server that closes the connection before sending any
        // frames is treated as a clean end-of-stream (not a fault).
        var bytes = Array.Empty<byte>();
        var items = await ReadAll<Msg>(bytes);
        Assert.Empty(items);
    }

    [Fact]
    public async Task SingleDataFrameThenEnd_YieldsOneMessage()
    {
        var frames = Concat(
            DataFrame("{\"value\":\"hello\"}"),
            EndFrame());
        var items = await ReadAll<Msg>(frames);
        Assert.Single(items);
        Assert.Equal("hello", items[0].Value);
    }

    [Fact]
    public async Task ThreeDataFramesInOrder()
    {
        var frames = Concat(
            DataFrame("{\"value\":\"one\"}"),
            DataFrame("{\"value\":\"two\"}"),
            DataFrame("{\"value\":\"three\"}"),
            EndFrame());
        var items = await ReadAll<Msg>(frames);
        Assert.Equal(new[] { "one", "two", "three" }, items.Select(m => m.Value));
    }

    [Fact]
    public async Task EndFrameWithErrorTrailer_StopsCleanly()
    {
        // The END frame may carry a JSON Connect-error trailer; the
        // reader is responsible for stopping at the END boundary
        // regardless of body content. Surfacing the trailer to the
        // caller is the BackrestClient's job, not the reader's.
        var frames = Concat(
            DataFrame("{\"value\":\"partial\"}"),
            EndFrame("{\"error\":{\"code\":\"unavailable\"}}"));
        var items = await ReadAll<Msg>(frames);
        Assert.Single(items);
        Assert.Equal("partial", items[0].Value);
    }

    [Fact]
    public async Task TruncatedHeader_StopsCleanly()
    {
        // The server hangs up after a complete frame but before the
        // next header arrives — that's just clean end-of-stream from
        // the reader's perspective. (A truncated header IN THE MIDDLE
        // of a header is the same situation; the next test covers
        // mid-frame truncation which is genuinely fatal.)
        var frames = DataFrame("{\"value\":\"only\"}");
        var items = await ReadAll<Msg>(frames);
        Assert.Single(items);
    }

    [Fact]
    public async Task TruncatedBody_Throws()
    {
        // Header says "next message is 50 bytes" but only 20 bytes
        // arrive before the socket closes. That's a real failure
        // mode and must surface, not be swallowed as "end of stream".
        var hdr = new byte[5];
        hdr[0] = 0; // data frame
        BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(1, 4), 50);
        var truncated = Concat(hdr, new byte[20]);

        await Assert.ThrowsAsync<EndOfStreamException>(
            async () => await ReadAll<Msg>(truncated));
    }

    [Fact]
    public async Task Cancellation_StopsRead()
    {
        // Verify cancellation propagates through to the underlying
        // stream read — otherwise a pending streaming RPC ignores the
        // user's "stop" button.
        var slow = new BlockingStream();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in ConnectStreamReader.ReadFramesAsync<Msg>(slow, cts.Token))
            {
                // never reached
            }
        });
    }

    // ---- helpers ----

    private static async Task<System.Collections.Generic.List<T>> ReadAll<T>(byte[] bytes)
        where T : class, new()
    {
        using var ms = new MemoryStream(bytes);
        var items = new System.Collections.Generic.List<T>();
        await foreach (var item in ConnectStreamReader.ReadFramesAsync<T>(ms, CancellationToken.None))
            items.Add(item);
        return items;
    }

    private static byte[] DataFrame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var hdr = new byte[5];
        hdr[0] = 0; // flags = 0 → data frame
        BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(1, 4), (uint)body.Length);
        return Concat(hdr, body);
    }

    private static byte[] EndFrame(string trailerJson = "")
    {
        var body = Encoding.UTF8.GetBytes(trailerJson);
        var hdr = new byte[5];
        hdr[0] = ConnectStreamReader.EndStreamFlag;
        BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(1, 4), (uint)body.Length);
        return Concat(hdr, body);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var len = parts.Sum(p => p.Length);
        var result = new byte[len];
        int offset = 0;
        foreach (var part in parts) { Buffer.BlockCopy(part, 0, result, offset, part.Length); offset += part.Length; }
        return result;
    }

    private sealed class Msg
    {
        [JsonPropertyName("value")] public string Value { get; set; } = "";
    }

    /// <summary>Stream that blocks on every read until cancelled — lets
    /// us prove cancellation actually reaches the reader without
    /// racing against an arbitrary timeout.</summary>
    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return 0; // unreachable
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
