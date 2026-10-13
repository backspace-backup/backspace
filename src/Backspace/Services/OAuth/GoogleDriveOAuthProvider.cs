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
/// Scope choice — <c>drive.file</c> (not <c>drive</c>):
///   - <c>drive.file</c>: "Per-file access to files created or opened
///     by the app". Files Backspace creates (the entire restic
///     repository) are app-scoped; files outside that scope are
///     invisible to us. This is the right scope for a backup tool
///     that owns its repository folder.
///   - <c>drive</c>: full Drive access, a restricted scope with
///     additional verification requirements. It is not requested here.
///   Registration and verification guidance: docs/OAUTH_REGISTRATION.md.
///
/// Token response shape (Google docs):
/// <code>
///   {
///     "access_token": "ya29.…",
///     "expires_in":   3599,
///     "refresh_token":"1//…",     // present only when access_type=offline + prompt=consent
///     "scope":        "https://www.googleapis.com/auth/drive.file",
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
///      <c>grant_type=authorization_code</c> — same as Dropbox; the
///      coordinator handles this generically. No Google-specific
///      glue here beyond the scope + auth-URL extras.
/// </summary>
public sealed class GoogleDriveOAuthProvider : IOAuthProvider
{
    private readonly string _clientId;

    public GoogleDriveOAuthProvider(string clientId) => _clientId = clientId;

    public DestinationKind Kind => DestinationKind.GoogleDrive;
    public string ClientId => _clientId;
    public string AuthorizationEndpoint => "https://accounts.google.com/o/oauth2/v2/auth";
    public string TokenEndpoint => "https://oauth2.googleapis.com/token";

    /// <summary>Per-file Drive scope (NOT full drive). See class
    /// docstring for the access boundary.</summary>
    public string Scope => "https://www.googleapis.com/auth/drive.file";

    /// <summary><c>access_type=offline</c> requests a refresh_token;
    /// <c>prompt=consent</c> forces the consent screen so the token
    /// endpoint actually returns one (see class docstring quirk #1).
    /// <c>include_granted_scopes=true</c> is courtesy: if the user
    /// has previously granted additional scopes to our client_id for
    /// other reasons, they're included in the new token without
    /// re-prompting.</summary>
    public IReadOnlyDictionary<string, string> ExtraAuthorizationParameters { get; } =
        new Dictionary<string, string>
        {
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["include_granted_scopes"] = "true",
        };

    public string FormatTokenForRclone(JsonElement r)
    {
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
        });
    }

    private static string RequireString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new InvalidOperationException($"Google Drive token response missing or wrong-typed '{name}'.");
}
