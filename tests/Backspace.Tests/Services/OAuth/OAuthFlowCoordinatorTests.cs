using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services.OAuth;
using Xunit;

namespace Backspace.Tests.Services.OAuth;

/// <summary>
/// Real loopback callbacks with controlled token responses. These checks
/// cover the PKCE handshake and failures, but not provider registration
/// or a real account's consent, which need a separate live check.
/// </summary>
public class OAuthFlowCoordinatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Google_SendsDesktopCredentialOnlyToTokenEndpoint_AndRedactsRejectedRequest(bool rejected)
    {
        const string desktopSecret = "fixture-desktop-secret";
        const string code = "fixture-google-code";
        string? authorizeUrl = null;
        string? verifier = null;
        var exchanges = 0;
        using var tokenHttp = new HttpClient(new CallbackHandler(async (request, ct) =>
        {
            exchanges++;
            Assert.Equal("https://oauth2.googleapis.com/token", request.RequestUri!.AbsoluteUri);
            var form = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct));
            var authorization = System.Web.HttpUtility.ParseQueryString(new Uri(authorizeUrl!).Query);
            Assert.Equal("fixture-google-id", form["client_id"]);
            Assert.Equal(desktopSecret, form["client_secret"]);
            Assert.Equal(code, form["code"]);
            Assert.Equal(authorization["redirect_uri"], form["redirect_uri"]);
            verifier = form["code_verifier"];
            Assert.Equal(authorization["code_challenge"], OAuthFlowCoordinator.ComputeCodeChallenge(verifier!));
            var body = rejected
                ? JsonSerializer.Serialize(new { error = "invalid_grant", error_description = $"Rejected {code} {verifier} {desktopSecret}" })
                : JsonSerializer.Serialize(new { access_token = "fixture-access", refresh_token = "fixture-refresh", expires_in = 3600, scope = CloudFolderAccess.GoogleScope });
            return new HttpResponseMessage(rejected ? HttpStatusCode.BadRequest : HttpStatusCode.OK) { Content = new StringContent(body) };
        }));
        using var loopback = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        Task<HttpResponseMessage>? callback = null;
        var coordinator = new OAuthFlowCoordinator(tokenHttp, url =>
        {
            authorizeUrl = url;
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            Assert.Equal(CloudFolderAccess.GoogleScope, query["scope"]);
            Assert.Equal("S256", query["code_challenge_method"]);
            Assert.Null(query["client_secret"]);
            Assert.DoesNotContain(desktopSecret, url);
            callback = loopback.GetAsync(query["redirect_uri"] + "?code=" + code + "&state=" + Uri.EscapeDataString(query["state"]!));
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await coordinator.RunAsync(new GoogleDriveOAuthProvider("fixture-google-id", desktopSecret), timeout.Token);
        using var response = await callback!;
        var page = await response.Content.ReadAsStringAsync(timeout.Token);
        Assert.Equal(1, exchanges);
        Assert.Equal(!rejected, result.Succeeded);
        Assert.Contains(rejected ? "Sign-in was not completed" : "You're connected", page);
        foreach (var credential in new[] { desktopSecret, code, verifier!, "fixture-access", "fixture-refresh" })
        {
            Assert.DoesNotContain(credential, page);
            Assert.DoesNotContain(credential, result.Message);
        }
        if (rejected)
        {
            Assert.Null(result.TokenJson);
            Assert.Contains("invalid_grant", result.Message);
            Assert.Contains("[redacted]", result.Message);
            Assert.DoesNotContain("You're connected", page);
        }
        else
        {
            Assert.Contains("fixture-refresh", result.TokenJson!);
            Assert.DoesNotContain(desktopSecret, result.TokenJson!);
            Assert.DoesNotContain(verifier!, result.TokenJson!);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dropbox_UsesRegisteredCallback_AndWaitsForTokenExchangeBeforeReportingSuccess(bool tokenRejected)
    {
        var tokenStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishToken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? authorizeUrl = null;
        using var tokenHttp = new HttpClient(new CallbackHandler(async (request, ct) =>
        {
            var form = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct));
            var authorization = System.Web.HttpUtility.ParseQueryString(new Uri(authorizeUrl!).Query);
            Assert.Equal("http://localhost:53682/callback", form["redirect_uri"]);
            Assert.Equal(OAuthAppRegistry.DropboxClientId, form["client_id"]);
            Assert.Equal("test-code", form["code"]);
            Assert.Equal(authorization["code_challenge"], OAuthFlowCoordinator.ComputeCodeChallenge(form["code_verifier"]!));
            Assert.Null(form["client_secret"]);
            tokenStarted.SetResult();
            await finishToken.Task.WaitAsync(ct);
            return new HttpResponseMessage(tokenRejected ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            {
                Content = new StringContent(tokenRejected ? "{\"error\":\"invalid_grant\"}" :
                    "{\"access_token\":\"test-access\",\"refresh_token\":\"test-refresh\",\"expires_in\":14400}")
            };
        }));
        using var loopback = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        Task<HttpResponseMessage>? callback = null;
        var coordinator = new OAuthFlowCoordinator(tokenHttp, url =>
        {
            authorizeUrl = url;
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            Assert.Equal("http://localhost:53682/callback", query["redirect_uri"]);
            Assert.Contains("files.metadata.read", query["scope"]!);
            Assert.Equal("S256", query["code_challenge_method"]);
            callback = loopback.GetAsync(query["redirect_uri"] + "?code=test-code&state=" + Uri.EscapeDataString(query["state"]!));
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var flow = coordinator.RunAsync(new DropboxOAuthProvider(), timeout.Token);
        try
        {
            await tokenStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(callback);
            Assert.False(callback!.IsCompleted); // No false Connected page while exchange is pending.

            var openedAnotherBrowser = false;
            var another = new OAuthFlowCoordinator(tokenHttp, _ => openedAnotherBrowser = true);
            var occupied = await another.RunAsync(new DropboxOAuthProvider(), timeout.Token);
            Assert.False(occupied.Succeeded);
            Assert.False(openedAnotherBrowser);
            Assert.Contains("53682", occupied.Message);
            Assert.Contains("try again", occupied.Message);
        }
        finally { finishToken.TrySetResult(); }
        var result = await flow;
        using var response = await callback!;
        var page = await response.Content.ReadAsStringAsync(timeout.Token);
        Assert.Equal(!tokenRejected, result.Succeeded);
        if (tokenRejected)
        {
            Assert.Contains("Sign-in was not completed", page);
            Assert.DoesNotContain("You're connected", page);
            Assert.Null(result.TokenJson);
        }
        else
        {
            Assert.Contains("You're connected", page);
            Assert.DoesNotContain("test-access", page);
            Assert.Contains("test-refresh", result.TokenJson!);
        }
    }

    [Fact]
    public async Task CallbackWithWrongStateCannotExchangeAToken_AndValidCallbackCanStillFollow()
    {
        var urlReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exchanges = 0;
        using var tokenHttp = new HttpClient(new CallbackHandler((_, _) =>
        {
            exchanges++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var coordinator = new OAuthFlowCoordinator(tokenHttp, url => urlReady.SetResult(url));
        var flow = coordinator.RunAsync(new StubProvider("fixture", "https://token.example.invalid"), timeout.Token);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(await urlReady.Task).Query);
        using var loopback = new HttpClient();
        using var bad = await loopback.GetAsync(query["redirect_uri"] + "?code=test&state=wrong", timeout.Token);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(0, exchanges);
        Assert.False(flow.IsCompleted);
        using var valid = await loopback.GetAsync(query["redirect_uri"] + "?code=test&state=" + Uri.EscapeDataString(query["state"]!), timeout.Token);
        Assert.True((await flow).Succeeded);
        Assert.Equal(1, exchanges);
    }

    private sealed class CallbackHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    [Fact]
    public async Task RunAsync_RefusesPlaceholderClientId()
    {
        // An unfinished registration must fail before opening the browser.
        var provider = new StubProvider(
            clientId: "REPLACE_WITH_DROPBOX_APP_KEY",
            tokenEndpoint: "http://unused.invalid");
        var coord = new OAuthFlowCoordinator();

        var result = await coord.RunAsync(provider);
        Assert.False(result.Succeeded);
        Assert.Contains("OAuth app", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_RefusesEmptyClientId()
    {
        // Same guard, different cause: a BYO client_id field
        // left blank in Settings.
        var provider = new StubProvider(clientId: "", tokenEndpoint: "http://unused.invalid");
        var coord = new OAuthFlowCoordinator();

        var result = await coord.RunAsync(provider);
        Assert.False(result.Succeeded);
        Assert.Contains("OAuth app", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_PropagatesCancellation()
    {
        // The browser dance can hang for up to TimeoutSeconds.
        // The caller must be able to cancel — e.g. when the user
        // closes the destination editor mid-flow.
        var provider = new StubProvider("real-looking-id", "http://unused.invalid");
        using var http = new System.Net.Http.HttpClient();
        var coord = new OAuthFlowCoordinator(http, _ => { }) { TimeoutSeconds = 60 };
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(200);

        var result = await coord.RunAsync(provider, cts.Token);
        Assert.False(result.Succeeded);
        Assert.Contains("cancelled", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_TimesOut_WithReadableMessage()
    {
        // Exercise the real listener timeout without opening an
        // unrelated browser tab. No callback or token HTTP occurs.
        var provider = new StubProvider("real-looking-id", "http://unused.invalid");
        using var http = new System.Net.Http.HttpClient();
        var coord = new OAuthFlowCoordinator(http, _ => { }) { TimeoutSeconds = 1 };

        var result = await coord.RunAsync(provider);
        Assert.False(result.Succeeded);
        // Timeout path returns either "didn't complete within" or
        // "cancelled" depending on which CancellationToken fired
        // first — both indicate the flow ended without a token.
        Assert.True(
            result.Message.Contains("didn't complete", StringComparison.OrdinalIgnoreCase) ||
            result.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase),
            $"Unexpected timeout message: {result.Message}");
    }

    private sealed class StubProvider : IOAuthProvider
    {
        public StubProvider(string clientId, string tokenEndpoint)
        {
            ClientId = clientId;
            TokenEndpoint = tokenEndpoint;
        }
        public DestinationKind Kind => DestinationKind.DropboxAppScoped;
        public string ClientId { get; }
        public string AuthorizationEndpoint => "https://example.invalid/authorize";
        public string TokenEndpoint { get; }
        public string Scope => "test.scope";
        public IReadOnlyDictionary<string, string> ExtraAuthorizationParameters { get; }
            = new Dictionary<string, string>();
        public string FormatTokenForRclone(JsonElement r) => "{}";
    }
}
