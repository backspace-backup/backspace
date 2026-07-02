using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services.OAuth;
using Xunit;

namespace Backspace.Tests.Services.OAuth;

/// <summary>
/// Coordinator-level checks that don't need a real browser. The
/// full handshake (state validation + code-for-token exchange) is
/// covered by the E07 Dropbox round-trip scenario when secrets.env
/// has a Dropbox token; here we focus on the failure modes and
/// pre-flight guards.
/// </summary>
public class OAuthFlowCoordinatorTests
{
    [Fact]
    public async Task RunAsync_RefusesPlaceholderClientId()
    {
        // OAuthAppRegistry's Dropbox slot ships as
        // "REPLACE_WITH_DROPBOX_APP_KEY" until the user pastes
        // their app key. The coordinator refuses to open a flow
        // against a placeholder so we don't open the browser to
        // a 404 from the provider.
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
        var coord = new OAuthFlowCoordinator { TimeoutSeconds = 60 };
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(200);

        var result = await coord.RunAsync(provider, cts.Token);
        Assert.False(result.Succeeded);
        Assert.Contains("cancelled", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_TimesOut_WithReadableMessage()
    {
        // Set a very short timeout so the test runs in <1s. The
        // browser will likely open (Process.Start succeeds against
        // example.invalid → user's browser shows a "site not
        // reachable" error). The coordinator stays in the wait
        // loop until the timeout elapses, then returns Failed.
        var provider = new StubProvider("real-looking-id", "http://unused.invalid");
        var coord = new OAuthFlowCoordinator { TimeoutSeconds = 1 };

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
