using System;
using System.Collections.Generic;
using System.Text.Json;
using Backspace.Models;

namespace Backspace.Services.OAuth;

/// <summary>
/// OAuth 2.0 + PKCE flow for Google Drive.
///
/// Endpoints (per Google Identity Platform docs):
///   - Authorize: <c>https://accounts.google.com/o/oauth2/v2/auth</c>
///   - Token:     <c>https://oauth2.googleapis.com/token</c>
///
/// Uses <c>drive.appdata</c>, Google's private application-data folder.
/// Its contents are hidden from the normal Drive UI and other apps.
/// Neither full-drive nor per-file access is requested.
///   Registration and verification guidance: docs/OAUTH_REGISTRATION.md.
///
/// Token response shape (Google docs):
/// <code>
///   {
///     "access_token": "ya29.…",
///     "expires_in":   3599,
///     "refresh_token":"1//…",     // present only when access_type=offline + prompt=consent
///     "scope":        "https://www.googleapis.com/auth/drive.appdata",
///     "token_type":   "Bearer",
///     "id_token":     "eyJ…"      // openid claims, optional
///   }
/// </code>
///
/// Google's quirks the implementation captures:
///   1. <c>access_type=offline</c> alone is NOT enough — Google
///      returns a refresh_token only on the user's FIRST consent.
///      Subsequent flows for the same client_id + user return only
///      an access_token (Google decides "user already approved, no
///      need to mint a new refresh token"). The workaround is
///      <c>prompt=consent</c> which forces the consent screen
///      every time and guarantees a refresh_token comes back. We
///      pay the cost of a re-shown consent screen for the
///      guarantee that token rotation always works.
///   2. The token endpoint expects <c>client_id</c> + <c>code</c>
///      + <c>code_verifier</c> + <c>redirect_uri</c> +
///      <c>grant_type=authorization_code</c>, plus the issued desktop
///      <c>client_secret</c>. PKCE remains required; the shared app
///      credential is not confidential in a distributed desktop app.
/// </summary>
public sealed class GoogleDriveOAuthProvider : IOAuthProvider
{
    private readonly string _clientId;
    private readonly string? _clientSecret;

    public GoogleDriveOAuthProvider(string clientId, string? clientSecret = null)
    {
        _clientId = clientId;
        _clientSecret = clientSecret ?? (clientId == OAuthAppRegistry.GoogleDriveClientId
            ? OAuthAppRegistry.GoogleDriveClientSecret : null);
    }

    public DestinationKind Kind => DestinationKind.GoogleDrive;
    public string ClientId => _clientId;
    public string? ClientSecret => _clientSecret;
    public string AuthorizationEndpoint => "https://accounts.google.com/o/oauth2/v2/auth";
    public string TokenEndpoint => "https://oauth2.googleapis.com/token";

    /// <summary>Access only to this application's private data folder.</summary>
    public string Scope => CloudFolderAccess.GoogleScope;

    /// <summary><c>access_type=offline</c> requests a refresh_token;
    /// <c>prompt=consent</c> forces the consent screen so the token
    /// endpoint actually returns one (see class docstring quirk #1).
    /// Do not combine this request with any earlier, broader grant.</summary>
    public IReadOnlyDictionary<string, string> ExtraAuthorizationParameters { get; } =
        new Dictionary<string, string>
        {
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["include_granted_scopes"] = "false",
        };

    public string FormatTokenForRclone(JsonElement r)
    {
        var scope = CloudFolderAccess.RequireScope(r, Kind);
        var accessToken = RequireString(r, "access_token");
        // refresh_token is REQUIRED for an installed-app flow that
        // outlives the user's session. Without it rclone silently
        // breaks once the ~1h access_token expires and the user
        // has to re-OAuth — exactly the friction we built this for.
        var refreshToken = r.TryGetProperty("refresh_token", out var rt)
                           && rt.ValueKind == JsonValueKind.String
            ? rt.GetString()!
            : throw new InvalidOperationException(
                "Google Drive token response missing refresh_token. " +
                "Make sure access_type=offline AND prompt=consent are in the auth URL " +
                "(set by GoogleDriveOAuthProvider.ExtraAuthorizationParameters).");
        var expiresIn = r.TryGetProperty("expires_in", out var exp)
            ? exp.GetInt32()
            : throw new InvalidOperationException("Google Drive token response missing expires_in.");

        var expiry = DateTime.UtcNow.AddSeconds(expiresIn).ToString("o");

        return JsonSerializer.Serialize(new
        {
            access_token = accessToken,
            token_type = "Bearer",
            refresh_token = refreshToken,
            expiry,
            scope,
        });
    }

    private static string RequireString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new InvalidOperationException($"Google Drive token response missing or wrong-typed '{name}'.");
}
