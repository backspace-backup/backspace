using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Backspace.Services.OAuth;

/// <summary>
/// Drives the OAuth 2.0 Authorization Code + PKCE flow for desktop
/// apps end-to-end:
///
///   1. Generate a random <c>code_verifier</c> + the matching
///      <c>code_challenge</c> = base64url(sha256(verifier)).
///   2. Start an <see cref="HttpListener"/> on
///      the provider's registered loopback host and port — the
///      redirect target the provider sends the user back to.
///   3. Open the user's default browser at the provider's
///      authorize URL with all the standard query params plus the
///      code_challenge.
///   4. Wait (up to <see cref="TimeoutSeconds"/> seconds) for a
///      callback hit on <c>/callback?code=…&amp;state=…</c>.
///   5. Validate the returned state matches what we generated.
///   6. POST the code + code_verifier to the provider's token
///      endpoint, get back JSON.
///   7. Hand the JSON to the provider's
///      <see cref="IOAuthProvider.FormatTokenForRclone"/> for
///      rclone-shape conversion, return the result.
///   8. Tell the browser whether the token exchange succeeded.
///
/// User cancels (closes the browser tab before consenting), provider
/// returns an OAuth error (<c>?error=access_denied</c>), or the
/// dance times out → the listener is torn down and the caller gets
/// an <see cref="OAuthFlowResult.Failed"/> with a human-readable
/// reason.
///
/// Why all this rather than a hosted third-party broker page: the
/// brokers we've seen don't ship the rclone-format JSON Backspace's
/// engine layer expects, and they introduce an external uptime
/// dependency. Wiring our own flow keeps Backspace self-contained,
/// gives us PKCE end-to-end, and never funnels a refresh token
/// through someone else's server.
/// </summary>
public sealed class OAuthFlowCoordinator
{
    private static ILogger _log => AppLogger.For<OAuthFlowCoordinator>();

    /// <summary>Wall-clock seconds the coordinator waits for the
    /// user to complete the browser consent flow before giving up.
    /// Users typically need 15-45s; 5 minutes is generous enough
    /// for someone who has to dig out their password manager or
    /// switch devices for an MFA prompt.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>HTTP client used for the token exchange POST.
    /// Injected so tests can substitute a stub.
    ///
    /// Shared process-wide for the parameterless ctor path: a new
    /// HttpClient per OAuthFlowCoordinator instance would leak
    /// socket handles (no Dispose path on the VM), and OAuth flows
    /// happen often enough during onboarding that socket exhaustion
    /// is a real risk. Tests still get isolated instances via the
    /// injecting ctor.</summary>
    private readonly HttpClient _http;
    private readonly Action<string> _openBrowser;

    private static readonly HttpClient s_sharedHttp = new();

    public OAuthFlowCoordinator() : this(s_sharedHttp) { }
    public OAuthFlowCoordinator(HttpClient http, Action<string>? openBrowser = null)
    {
        _http = http;
        _openBrowser = openBrowser ?? (url => { using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); });
    }

    public async Task<OAuthFlowResult> RunAsync(IOAuthProvider provider, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider.ClientId)
            || provider.ClientId.StartsWith("REPLACE_", StringComparison.Ordinal))
        {
            return OAuthFlowResult.Failed(
                $"Backspace isn't yet configured with an OAuth app for {provider.Kind}. " +
                $"Either upgrade to a build that ships an official app key, or paste " +
                $"your own client_id under Settings → OAuth.");
        }

        // PKCE: random 64-byte verifier → base64url → 86 chars,
        // safely inside Dropbox's 43-128 char window. Generated
        // here (not in the provider) because the verifier MUST be
        // unique per flow and must never leave this method.
        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = ComputeCodeChallenge(codeVerifier);
        var state = GenerateRandomToken(24);

        // Allocate a loopback port up-front so we can stitch it
        // into the redirect_uri the authorize URL points at.
        // Dropbox requires an exact registered port. Entra and Google's
        // installed-app flows allow an available port chosen at runtime.
        var port = provider.LoopbackPort ?? AllocateEphemeralPort();
        var loopbackHost = provider.LoopbackHost;
        var redirectUri = $"http://{loopbackHost}:{port}/callback";

        var authorizeUrl = BuildAuthorizationUrl(provider, redirectUri, state, codeChallenge);

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://{loopbackHost}:{port}/");
        try { listener.Start(); }
        catch (HttpListenerException ex)
        {
            _log.Warning(ex, "OAuth loopback listener failed to start on port {Port}", port);
            return OAuthFlowResult.Failed(
                $"Couldn't start sign-in on {loopbackHost}:{port}. Close another sign-in window or the app using that port, then try again. " +
                $"{ex.Message}");
        }

        _log.Information("OAuth flow starting for {Kind} (local callback port {Port})",
            provider.Kind, port);

        // Open the browser AFTER the listener is live — the
        // provider may redirect very quickly on auto-consent
        // (refresh-token flow) and we don't want to race.
        try
        {
            _openBrowser(authorizeUrl);
        }
        catch (Exception ex)
        {
            listener.Stop();
            // Don't echo the authorize URL into the user-visible
            // error: it embeds the `state` CSRF token in the query
            // string, and UI toasts / logs that capture this message
            // could leak it. Tell the user how to retry instead, and
            // keep the state and authorization URL out of logs as well.
            _log.Debug(ex, "Couldn't open browser for OAuth");
            return OAuthFlowResult.Failed(
                $"Couldn't open your browser to start sign-in. Close any blocking dialog and try Sign in again. " +
                $"Reason: {ex.Message}");
        }

        // Wait for the redirect. The user could close the browser,
        // hit ESC, deny access — all those land here as either a
        // /callback?error=… hit or a timeout.
        OAuthCallback? callback;
        try
        {
            callback = await WaitForCallbackAsync(listener, state, ct);
        }
        catch (OperationCanceledException)
        {
            return OAuthFlowResult.Failed("OAuth flow cancelled.");
        }

        if (callback is null)
        {
            // Distinguish "the caller cancelled us" (e.g. user closed
            // the editor mid-flow) from "the user never finished the
            // browser dance". The two paths both null-out callback
            // because WaitForCallbackAsync swallows the
            // ObjectDisposedException from the stopped listener.
            return ct.IsCancellationRequested
                ? OAuthFlowResult.Failed("OAuth flow cancelled.")
                : OAuthFlowResult.Failed(
                    $"Sign-in didn't complete within {TimeoutSeconds}s. " +
                    $"Try again; Backspace will reopen the browser.");
        }

        if (!string.IsNullOrEmpty(callback.Error))
        {
            await Respond(callback.Context, 200, "Sign-in cancelled", BuildResultPage(false)).ConfigureAwait(false);
            return OAuthFlowResult.Failed(
                $"The provider refused the sign-in ({callback.Error}" +
                (string.IsNullOrEmpty(callback.ErrorDescription)
                    ? ")."
                    : $": {callback.ErrorDescription})."));
        }

        if (string.IsNullOrEmpty(callback.Code))
        {
            await Respond(callback.Context, 400, "Sign-in failed", BuildResultPage(false)).ConfigureAwait(false);
            return OAuthFlowResult.Failed("Provider redirected without a code or error (unusual); try again.");
        }

        // Exchange the code using PKCE and any shared desktop registration
        // credential the provider also requires.
        // Wrap in try/catch so a network blip or 4xx from the
        // provider surfaces as a Failed result the UI can show as
        // a toast — not a stack trace bubbling out of the command.
        try
        {
            var tokenJson = await ExchangeCodeForTokenAsync(
                provider, callback.Code, codeVerifier, redirectUri, ct).ConfigureAwait(false);
            await Respond(callback.Context, 200, "Connected", BuildResultPage(true)).ConfigureAwait(false);
            _log.Information("OAuth flow completed for {Kind}", provider.Kind);
            return OAuthFlowResult.Ok(tokenJson);
        }
        catch (Exception ex)
        {
            await Respond(callback.Context, 400, "Sign-in failed", BuildResultPage(false)).ConfigureAwait(false);
            _log.Warning(ex, "OAuth token exchange threw for {Kind}", provider.Kind);
            return OAuthFlowResult.Failed("Token exchange failed: " + ex.Message);
        }
    }

    /// <summary>Compose the authorize-URL query string. Standard
    /// PKCE flow params plus whatever provider-specific extras the
    /// IOAuthProvider asks for.</summary>
    private static string BuildAuthorizationUrl(
        IOAuthProvider provider, string redirectUri, string state, string codeChallenge)
    {
        var q = new List<KeyValuePair<string, string>>
        {
            new("client_id",             provider.ClientId),
            new("redirect_uri",          redirectUri),
            new("response_type",         "code"),
            new("scope",                 provider.Scope),
            new("state",                 state),
            new("code_challenge",        codeChallenge),
            new("code_challenge_method", "S256"),
        };
        foreach (var kv in provider.ExtraAuthorizationParameters)
            q.Add(new KeyValuePair<string, string>(kv.Key, kv.Value));

        var sb = new StringBuilder(provider.AuthorizationEndpoint);
        sb.Append('?');
        bool first = true;
        foreach (var (k, v) in q)
        {
            if (!first) sb.Append('&');
            first = false;
            sb.Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v));
        }
        return sb.ToString();
    }

    private async Task<OAuthCallback?> WaitForCallbackAsync(
        HttpListener listener, string expectedState, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

        // GetContextAsync doesn't accept a CancellationToken, so we
        // race it against the timeout. Stopping the listener on
        // timeout makes the pending GetContextAsync throw
        // ObjectDisposedException, which we treat as "no callback".
        using var reg = timeoutCts.Token.Register(() => { try { listener.Stop(); } catch { } });

        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (HttpListenerException) { return null; }   // listener stopped → timeout
            catch (ObjectDisposedException) { return null; }

            // Many browsers prefetch favicon.ico against the
            // redirect host; ignore anything that isn't our
            // /callback path so a 404 toaster doesn't drown the
            // real callback.
            if (!ctx.Request.Url!.AbsolutePath.Equals("/callback", StringComparison.OrdinalIgnoreCase))
            {
                await Respond(ctx, 404, "Not Found", "Backspace OAuth flow: wrong path.").ConfigureAwait(false);
                continue;
            }

            var query = System.Web.HttpUtility.ParseQueryString(ctx.Request.Url.Query);
            var code = query["code"];
            var state = query["state"];
            var error = query["error"];
            var errorDesc = query["error_description"];

            // State mismatch → reject. A passing CSRF check before
            // we'd accept the code is a hard requirement; without
            // it a malicious site could fire its own /callback
            // with a stolen code. Constant-time comparison so a
            // localhost-listener side channel can't recover a state
            // prefix character-by-character; the entropy is 192 bits
            // so brute force is infeasible regardless, but matching
            // the canonical pattern closes any cleverness.
            if (!CsrfStateEquals(state, expectedState))
            {
                await Respond(ctx, 400, "Bad State",
                    "The OAuth state didn't match the request. You can close this tab and try again.")
                    .ConfigureAwait(false);
                continue; // wait for the real one, if any
            }

            // Keep this response open until token exchange finishes. Receiving
            // an authorization code alone does not establish a connection.
            return new OAuthCallback(code, error, errorDesc, ctx);
        }
        return null;
    }

    private async Task<string> ExchangeCodeForTokenAsync(
        IOAuthProvider provider, string code, string codeVerifier, string redirectUri, CancellationToken ct)
    {
        // Token endpoint is application/x-www-form-urlencoded.
        // The provider checks the verifier against the original challenge.
        // Google also requires its desktop app credential in this POST.
        var values = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("grant_type",    "authorization_code"),
            new KeyValuePair<string, string>("code",          code),
            new KeyValuePair<string, string>("client_id",     provider.ClientId),
            new KeyValuePair<string, string>("redirect_uri",  redirectUri),
            new KeyValuePair<string, string>("code_verifier", codeVerifier),
        };
        if (!string.IsNullOrWhiteSpace(provider.ClientSecret))
            values.Add(new KeyValuePair<string, string>("client_secret", provider.ClientSecret));
        using var form = new FormUrlEncodedContent(values);

        using var resp = await _http.PostAsync(provider.TokenEndpoint, form, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // Keep useful provider diagnostics, but do not allow an echoed
            // request credential into the UI or the exception log.
            foreach (var credential in new[] { code, codeVerifier, provider.ClientSecret })
            {
                if (string.IsNullOrEmpty(credential)) continue;
                body = body.Replace(credential, "[redacted]", StringComparison.Ordinal)
                    .Replace(Uri.EscapeDataString(credential), "[redacted]", StringComparison.Ordinal);
            }
            throw new InvalidOperationException(
                $"Token exchange failed ({(int)resp.StatusCode} {resp.StatusCode}): {body}");
        }

        using var doc = JsonDocument.Parse(body);
        return provider.FormatTokenForRclone(doc.RootElement);
    }

    /// <summary>Constant-time comparison for the OAuth state CSRF
    /// token. Equal lengths route through CryptographicOperations
    /// .FixedTimeEquals; unequal lengths still run the constant-time
    /// path (against the shorter length) so the duration of the
    /// rejection doesn't depend on where the mismatch is.</summary>
    private static bool CsrfStateEquals(string? actual, string? expected)
    {
        if (actual is null || expected is null) return false;
        var actualBytes = System.Text.Encoding.UTF8.GetBytes(actual);
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
        // FixedTimeEquals requires equal-length inputs. Compare the
        // lengths via XOR + an unconditional equality probe so the
        // length-mismatch path takes the same shape as the
        // content-mismatch path.
        var minLen = Math.Min(actualBytes.Length, expectedBytes.Length);
        var probeA = new byte[minLen];
        var probeB = new byte[minLen];
        Array.Copy(actualBytes, probeA, minLen);
        Array.Copy(expectedBytes, probeB, minLen);
        var equal = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(probeA, probeB);
        return equal && actualBytes.Length == expectedBytes.Length;
    }

    private static async Task Respond(HttpListenerContext ctx, int status, string description, string html)
    {
        // Best-effort: if the listener is torn down mid-response
        // (timeout fired while we were writing the success HTML)
        // the OutputStream throws ObjectDisposedException /
        // HttpListenerException. Swallow — the user's browser may
        // see a partial response, but the flow proceeds; the
        // important state ("we have the callback") is already
        // captured by the caller.
        try
        {
            ctx.Response.StatusCode = status;
            ctx.Response.StatusDescription = description;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException or InvalidOperationException or System.IO.IOException)
        {
            // Listener went away. Nothing to clean up; the using-
            // scope on the outer listener handles disposal.
        }
        finally
        {
            try { ctx.Response.OutputStream.Close(); } catch { }
            try { ctx.Response.Close(); } catch { }
        }
    }

    /// <summary>The HTML page the user sees in their browser after
    /// the provider redirects back. Deliberately minimal — no
    /// branding, no JS, no remote assets — so it works on any
    /// browser, including embedded webviews / locked-down corporate
    /// browsers / Tor. Tells the user the flow worked and they
    /// can close the tab.</summary>
    private static string BuildResultPage(bool connected)
    {
        if (!connected)
        {
            return "<!doctype html><meta charset=utf-8>" +
                   "<title>Backspace - sign-in not completed</title>" +
                   "<style>body{font-family:system-ui;margin:4rem auto;max-width:30rem;color:#333}</style>" +
                   "<h2>Sign-in was not completed</h2>" +
                   "<p>Return to Backspace for details. You can close this tab and try again.</p>";
        }
        return "<!doctype html><meta charset=utf-8>" +
               "<title>Backspace - connected</title>" +
               "<style>body{font-family:system-ui;margin:4rem auto;max-width:30rem;color:#333}</style>" +
               "<h2>You're connected.</h2>" +
               "<p>Return to Backspace to finish setting up your destination. You can close this tab.</p>";
    }

    // ---- helpers ----

    internal static int AllocateEphemeralPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    /// <summary>RFC 7636 PKCE code_verifier: 43-128 chars from the
    /// unreserved set [A-Z a-z 0-9 - . _ ~]. We use 64 random
    /// bytes → base64url, yielding an 86-char URL-safe string. The
    /// verifier MUST be cryptographically random — guessable values
    /// neutralise PKCE's protection.</summary>
    internal static string GenerateCodeVerifier()
    {
        Span<byte> buf = stackalloc byte[64];
        RandomNumberGenerator.Fill(buf);
        return Base64UrlEncode(buf);
    }

    /// <summary>The S256 code challenge: base64url(sha256(verifier)).</summary>
    internal static string ComputeCodeChallenge(string verifier)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.ASCII.GetBytes(verifier), hash);
        return Base64UrlEncode(hash);
    }

    /// <summary>Opaque random token used for the OAuth state
    /// parameter (CSRF protection on the redirect). Length is in
    /// bytes pre-encoding; the encoded string is roughly
    /// <c>length * 4 / 3</c> chars.</summary>
    internal static string GenerateRandomToken(int byteLength)
    {
        var buf = new byte[byteLength];
        RandomNumberGenerator.Fill(buf);
        return Base64UrlEncode(buf);
    }

    internal static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        // Standard base64 then translate the URL-unsafe chars per
        // RFC 4648 §5. Padding is stripped because PKCE forbids it.
        var s = Convert.ToBase64String(bytes);
        return s.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private sealed record OAuthCallback(string? Code, string? Error, string? ErrorDescription, HttpListenerContext Context);
}

/// <summary>Outcome of <see cref="OAuthFlowCoordinator.RunAsync"/>.
/// <see cref="TokenJson"/> is non-null iff <see cref="Succeeded"/>
/// is true and carries the rclone-format JSON ready to drop into
/// <see cref="Models.Destination.OAuthTokenRef"/>.</summary>
public readonly record struct OAuthFlowResult(bool Succeeded, string? TokenJson, string Message)
{
    public static OAuthFlowResult Ok(string tokenJson) => new(true, tokenJson, "Connected.");
    public static OAuthFlowResult Failed(string reason) => new(false, null, reason);
}
