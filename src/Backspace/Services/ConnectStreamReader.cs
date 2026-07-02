using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services;

/// <summary>
/// Reads a Connect-Go server-streaming response off the wire and
/// yields one decoded message at a time. The Connect protocol frames
/// each message as:
///
/// <code>
///   1 byte  flags          (LSB set on the END-stream frame)
///   4 bytes message length (big-endian, uint32)
///   N bytes message body   (JSON-encoded protobuf for the JSON codec)
/// </code>
///
/// The END frame carries an empty body when the stream completes
/// cleanly, or a JSON Connect-error envelope when the server aborts.
/// Either way it's the last frame; we yield up to (but not including)
/// the END frame and surface the trailer in
/// <see cref="StreamCompletion.Error"/> for the caller to inspect.
///
/// Stays a separate concern from <see cref="BackrestClient"/> so the
/// framing logic can be unit-tested against a hand-rolled byte stream
/// without spinning up an HttpClient.
/// </summary>
internal static class ConnectStreamReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The marker bit on the flag byte that says "this is the
    /// last frame". Connect-Go calls this the "EndStream" flag.</summary>
    internal const byte EndStreamFlag = 0x02;

    /// <summary>Parse a length-prefixed Connect framed stream into a
    /// sequence of decoded messages. The returned async-enumerable
    /// stops on the END frame. Cancellation flows through to the
    /// underlying stream read.</summary>
    public static async IAsyncEnumerable<T> ReadFramesAsync<T>(
        Stream input,
        [EnumeratorCancellation] CancellationToken ct = default)
        where T : class, new()
    {
        var header = new byte[5];
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (!await ReadExactlyAsync(input, header, 0, header.Length, ct).ConfigureAwait(false))
                yield break;

            var flags = header[0];
            var length = BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(header, 1, 4));

            // Reject pathological frame lengths up-front. The Backrest
            // daemon's largest realistic message is ~1 MB (a snapshot
            // listing for a huge repo); anything in the GB range is
            // either a protocol bug on the daemon side or a corrupt
            // stream that would otherwise cause us to allocate a
            // multi-GB buffer here. Capping at 64 MB gives plenty of
            // headroom for any sane message and turns a memory bomb
            // into a clean InvalidDataException the orchestrator's
            // retry cascade will surface.
            const uint MaxFrameLengthBytes = 64u * 1024 * 1024;
            if (length > MaxFrameLengthBytes)
                throw new InvalidDataException(
                    $"Backrest stream frame length {length} exceeds the {MaxFrameLengthBytes}-byte cap. " +
                    "Either the daemon is sending unexpectedly large messages or the stream is corrupt.");

            byte[] body;
            if (length == 0)
            {
                body = Array.Empty<byte>();
            }
            else
            {
                body = ArrayPool<byte>.Shared.Rent((int)length);
                try
                {
                    if (!await ReadExactlyAsync(input, body, 0, (int)length, ct).ConfigureAwait(false))
                        yield break;
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(body);
                    throw;
                }
            }

            try
            {
                if ((flags & EndStreamFlag) != 0)
                {
                    // End frame — body holds the trailer envelope (or
                    // is empty on clean completion). Stop yielding here.
                    yield break;
                }

                // Data frame: decode the body to T and yield it.
                if (length == 0) yield return new T();
                else yield return JsonSerializer.Deserialize<T>(new ReadOnlySpan<byte>(body, 0, (int)length), Json) ?? new T();
            }
            finally
            {
                if (length != 0) ArrayPool<byte>.Shared.Return(body);
            }
        }
    }

    /// <summary>Read exactly <paramref name="count"/> bytes into
    /// <paramref name="buffer"/> at <paramref name="offset"/>. Returns
    /// <c>false</c> when the stream closes before any byte arrives
    /// (clean end-of-stream); throws when it closes mid-frame (truncated
    /// transport).</summary>
    private static async Task<bool> ReadExactlyAsync(Stream s, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int got = 0;
        while (got < count)
        {
            var n = await s.ReadAsync(buffer.AsMemory(offset + got, count - got), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (got == 0) return false;
                throw new EndOfStreamException(
                    $"Connect stream truncated mid-frame: wanted {count} bytes, got {got} before EOF.");
            }
            got += n;
        }
        return true;
    }
}
