using System.Collections.Generic;
using System.Text.Json;
using Backspace.Models;

namespace Backspace.Services.OAuth;

/// <summary>
/// Provider-specific contract for an OAuth 2.0 + PKCE installed-app
/// flow. One implementation per cloud destination family: Dropbox,
/// OneDrive and Google Drive. Registration availability is handled by
/// the provider factory, independently of this protocol contract.
///
/// The flow itself — loopback HttpListener for the redirect,
/// state validation, code-for-token exchange — lives in
/// <see cref="OAuthFlowCoordinator"/>. Providers just answer "where
/// do I send the user?", "what scopes?", and "how do I shape the
/// token response into something rclone will accept?".
///
/// Why PKCE (Proof Key for Code Exchange) is required:
///   • Desktop apps cannot keep a shared application secret confidential.
///   • PKCE uses a per-flow random code_verifier
///     that's hashed (SHA-256) into the auth request and revealed at
///     token-exchange time. The provider verifies sha256(verifier)
///     matches the original challenge.
///   • Every provider we care about (Dropbox, OneDrive, Google) has
///     PKCE support.
///   • Google still requires its issued desktop client_secret during
///     exchange and refresh. That shared app credential is separate from
///     user tokens and does not replace PKCE or account consent.
///
/// rclone's token JSON shape (the same blob this contract returns
/// from <see cref="FormatTokenForRclone"/>) is what
/// <c>RcloneConfigBuilder</c> drops verbatim into each remote's
/// <c>token = &lt;json&gt;</c> field. The shape is documented in
/// rclone's source as:
/// <c>{"access_token":"…","token_type":"bearer","refresh_token":"…","expiry":"&lt;RFC3339&gt;"}</c>.
/// </summary>
public interface IOAuthProvider
{
    /// <summary>The destination family this provider authorises.
    /// Used by <see cref="OAuthProviderFactory"/> to pick the right
    /// implementation from a UI selection.</summary>
    DestinationKind Kind { get; }

    /// <summary>Public client_id Backspace registered with the
    /// provider. Public by design.</summary>
    string ClientId { get; }

    /// <summary>Optional shared desktop-app credential required by Google.
    /// Send it only to the token endpoint, never in the browser URL.</summary>
    string? ClientSecret => null;

    /// <summary>Provider's OAuth2 authorize endpoint URL. The
    /// coordinator appends query params (client_id, redirect_uri,
    /// response_type, state, code_challenge, scope) before opening
    /// the browser at it.</summary>
    string AuthorizationEndpoint { get; }

    /// <summary>Provider's OAuth2 token endpoint URL. The
    /// coordinator POSTs the code + code_verifier here after
    /// receiving the redirect callback.</summary>
    string TokenEndpoint { get; }

    /// <summary>Loopback host expected by the app registration.</summary>
    string LoopbackHost => "127.0.0.1";

    /// <summary>A fixed port when the provider requires an exact registered
    /// redirect URI; otherwise choose an unused port for each sign-in.</summary>
    int? LoopbackPort => null;

    /// <summary>Space-separated OAuth scope string for the
    /// authorize request. Provider-specific; e.g. Dropbox's app-
    /// folder scope is <c>files.content.read files.content.write</c>.</summary>
    string Scope { get; }

    /// <summary>Provider-specific extra query parameters for the
    /// authorize URL. Dropbox uses <c>token_access_type=offline</c>
    /// to receive a refresh token; Google Drive uses
    /// <c>access_type=offline&amp;prompt=consent</c> for the same
    /// effect; OneDrive has no extras beyond the standard set.</summary>
    IReadOnlyDictionary<string, string> ExtraAuthorizationParameters { get; }

    /// <summary>Transform the provider's token-endpoint response
    /// JSON into the exact shape rclone's <c>config file</c>
    /// emits — <c>{"access_token":"…","token_type":"bearer",
    /// "refresh_token":"…","expiry":"&lt;RFC3339&gt;"}</c>.
    /// rclone is strict here: a missing field or wrong field
    /// name makes the remote unusable.</summary>
    string FormatTokenForRclone(JsonElement tokenResponse);
}
