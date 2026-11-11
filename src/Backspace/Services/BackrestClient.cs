using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Backspace.Services;

/// <summary>
/// Thin Connect-Go RPC client for the Backrest sidecar daemon.
///
/// Connect protocol notes (https://connectrpc.com/docs/protocol):
///   - Unary call = HTTP POST to <c>/{package}.{service}/{method}</c>
///     with Content-Type: application/json and a JSON-encoded request
///     body matching the proto message.
///   - Successful response is HTTP 200 + JSON-encoded response message.
///   - Error response is HTTP 4xx/5xx + JSON envelope
///     <c>{ "code": "...", "message": "..." }</c> — see
///     <see cref="ConnectError"/>.
///   - Server streaming RPCs (GetOperationEvents, GetLogs) use a
///     length-prefixed framed Connect envelope (1 byte flags +
///     4 bytes BE length + JSON payload). <see cref="StreamAsync"/>
///     handles the wire format; live-progress consumers wrap that
///     stream and yield typed messages as they arrive.
///
/// Auth: Backrest's HTTP middleware enforces <c>Authorization: Bearer
/// {jwt}</c> on every RPC once <c>Config.auth.users</c> has at least
/// one user. We pick up the JWT from Authentication/Login and stash it
/// on the underlying HttpClient via <see cref="SetBearerToken"/>.
/// </summary>
internal sealed class BackrestClient
{
    private static ILogger _log => AppLogger.For<BackrestClient>();

    private readonly HttpClient _http;

    /// <summary>JSON options used for both request and response bodies.
    /// Backrest expects camelCase per the proto's <c>json_name</c>
    /// annotations; <see cref="BackrestRpcModels"/> POCOs carry explicit
    /// <see cref="System.Text.Json.Serialization.JsonPropertyNameAttribute"/>
    /// already, so the naming policy is moot — but keeping it set
    /// guards us if anyone adds an unannotated field by mistake.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public BackrestClient(HttpClient http) => _http = http;

    public void SetBearerToken(string jwt)
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
    }

    /// <summary>Ask the daemon to bcrypt-hash a plaintext password.
    /// Backrest's <c>/v1.Authentication/HashPassword</c> exists
    /// precisely so we don't have to depend on a local BCrypt
    /// library; the daemon already has the same Go bcrypt
    /// implementation it uses to verify passwords on Login.
    /// Returns the hash in the same on-wire shape the
    /// <c>auth.users[].passwordBcrypt</c> config field expects.</summary>
    public async Task<string> HashPasswordAsync(string plaintext, CancellationToken ct)
    {
        var resp = await CallAsync<StringValue, StringValue>(
            "/v1.Authentication/HashPassword",
            new StringValue { Value = plaintext },
            ct).ConfigureAwait(false);
        return resp.Value;
    }

    /// <summary>Login as <paramref name="username"/> and return the JWT
    /// the caller must echo as <c>Authorization: Bearer {token}</c> on
    /// every subsequent RPC. Throws <see cref="BackrestRpcException"/>
    /// on bad credentials (the daemon returns 401 with an empty body).</summary>
    public async Task<string> LoginAsync(string username, string password, CancellationToken ct)
    {
        var resp = await CallAsync<BackrestLoginRequest, BackrestLoginResponse>(
            "/v1.Authentication/Login",
            new BackrestLoginRequest { Username = username, Password = password },
            ct).ConfigureAwait(false);
        return resp.Token;
    }

    /// <summary>POST a request to a Connect endpoint and deserialize the
    /// response body. On non-2xx the body is parsed as a
    /// <see cref="ConnectError"/> and re-thrown as
    /// <see cref="BackrestRpcException"/>. The endpoint string must
    /// already include the leading slash plus package and service —
    /// e.g. <c>/v1.Backrest/Backup</c>.</summary>
    public async Task<TResp> CallAsync<TReq, TResp>(string endpoint, TReq request, CancellationToken ct)
        where TReq : class
        where TResp : class, new()
    {
        using var content = JsonContent.Create(request, options: Json);
        using var resp = await _http.PostAsync(endpoint, content, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsed = TryParseConnectError(errBody);
            throw new BackrestRpcException(endpoint, (int)resp.StatusCode, parsed?.Code ?? "", parsed?.Message ?? errBody);
        }

        // Some RPCs return google.protobuf.Empty — the response body is
        // either "{}" or empty. Either deserializes cleanly into a
        // default-constructed TResp.
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body) || body == "{}") return new TResp();
        return JsonSerializer.Deserialize<TResp>(body, Json) ?? new TResp();
    }

    /// <summary>Variant for RPCs returning <c>google.protobuf.Empty</c>:
    /// we don't model an empty type, the caller doesn't care about the
    /// response body. Errors still surface as
    /// <see cref="BackrestRpcException"/>.</summary>
    public async Task CallVoidAsync<TReq>(string endpoint, TReq request, CancellationToken ct)
        where TReq : class
    {
        using var content = JsonContent.Create(request, options: Json);
        using var resp = await _http.PostAsync(endpoint, content, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsed = TryParseConnectError(errBody);
            throw new BackrestRpcException(endpoint, (int)resp.StatusCode, parsed?.Code ?? "", parsed?.Message ?? errBody);
        }
    }

    /// <summary>
    /// Server-streaming RPC. POSTs the request body, keeps the response
    /// open, and yields each decoded message through the returned
    /// async-enumerable as it arrives off the wire. The stream
    /// completes when the server sends the Connect END frame; the
    /// async-enumerable then stops normally.
    /// </summary>
    /// <remarks>
    /// Cancellation propagates to the underlying HTTP read; an HTTP
    /// 4xx/5xx surfaces as a thrown <see cref="BackrestRpcException"/>
    /// before any message is yielded, matching the unary call's error
    /// shape.
    /// </remarks>
    public async IAsyncEnumerable<TResp> StreamAsync<TReq, TResp>(
        string endpoint, TReq request,
        [EnumeratorCancellation] CancellationToken ct)
        where TReq : class
        where TResp : class, new()
    {
        // Connect's streaming protocol requires:
        //   • Content-Type: application/connect+json — the unary
        //     application/json shape is rejected with HTTP 415.
        //   • The REQUEST body itself must be Connect envelope-framed:
        //     one frame of [1 byte flags=0x00][4 bytes BE length][JSON].
        //     Sending the raw JSON (the unary shape) made Backrest
        //     1.13 silently EOF the stream without ever yielding a
        //     frame back — every backup completed on disk but the
        //     orchestrator saw zero events and mapped lastStatus=""
        //     to Failed.
        // ConnectStreamReader handles the response-side framing.
        // See https://connectrpc.com/docs/protocol/#streaming-protocol.
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        var envelope = new byte[5 + payload.Length];
        envelope[0] = 0x00; // flags: not end-of-stream, no compression
        BinaryPrimitives.WriteUInt32BigEndian(envelope.AsSpan(1, 4), (uint)payload.Length);
        payload.AsSpan().CopyTo(envelope.AsSpan(5));

        using var content = new ByteArrayContent(envelope);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/connect+json");
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsed = TryParseConnectError(errBody);
            throw new BackrestRpcException(endpoint, (int)resp.StatusCode, parsed?.Code ?? "", parsed?.Message ?? errBody);
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await foreach (var msg in ConnectStreamReader.ReadFramesAsync<TResp>(stream, ct).ConfigureAwait(false))
        {
            yield return msg;
        }
    }

    private static ConnectError? TryParseConnectError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonSerializer.Deserialize<ConnectError>(body, Json); }
        catch { return null; }
    }
}

/// <summary>Surfaces an RPC failure with enough context for the GUI to
/// show a meaningful "the sidecar said no" message. <see cref="Code"/>
/// is the Connect protocol code string (e.g. <c>"unauthenticated"</c>,
/// <c>"not_found"</c>); <see cref="HttpStatus"/> is the underlying HTTP
/// status if you need to discriminate transport vs. application errors.</summary>
internal sealed class BackrestRpcException : Exception
{
    public string Endpoint { get; }
    public int HttpStatus { get; }
    public string Code { get; }

    public BackrestRpcException(string endpoint, int httpStatus, string code, string message)
        : base($"{endpoint} failed: {(string.IsNullOrEmpty(code) ? "HTTP " + httpStatus : code)}: {message}")
    {
        Endpoint = endpoint;
        HttpStatus = httpStatus;
        Code = code;
    }
}
