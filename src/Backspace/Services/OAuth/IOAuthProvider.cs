using System.Collections.Generic;
using System.Text.Json;
using Backspace.Models;

namespace Backspace.Services.OAuth;

/// <summary>
/// Provider-specific contract for an OAuth 2.0 + PKCE installed-app
/// flow. One implementation per cloud destination family: Dropbox
/// (<see cref="DropboxOAuthProvider"/>), with OneDrive / Google Drive
/// to follow once their apps are registered.
///
/// The flow itself — loopback HttpListener for the redirect,
/// state validation, code-for-token exchange — lives in
/// <see cref="OAuthFlowCoordinator"/>. Providers just answer "where
/// do I send the user?", "what scopes?", and "how do I shape the
/// token response into something rclone will accept?".
///
/// Why PKCE (Proof Key for Code Exchange) instead of the classic
/// confidential-client flow with a client_secret:
///   • Desktop apps can't safely ship a client_secret — anyone who
///     extracts strings from the binary has it.
///   • PKCE replaces the secret with a per-flow random code_verifier
///     that's hashed (SHA-256) into the auth request and revealed at
///     token-exchange time. The provider verifies sha256(verifier)
///     matches the original challenge.
///   • Every provider we care about (Dropbox, OneDrive, Google) has
///     PKCE support.
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
    /// provider. Public by design — see PKCE note above for why
    /// there's no client_secret.</summary>
    string ClientId { get; }

    /// <summary>Provider's OAuth2 authorize endpoint URL. The
    /// coordinator appends query params (client_id, redirect_uri,
    /// response_type, state, code_challenge, scope) before opening
    /// the browser at it.</summary>
    string AuthorizationEndpoint { get; }

    /// <summary>Provider's OAuth2 token endpoint URL. The
    /// coordinator POSTs the code + code_verifier here after
    /// receiving the redirect callback.</summary>
    string TokenEndpoint { get; }

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
