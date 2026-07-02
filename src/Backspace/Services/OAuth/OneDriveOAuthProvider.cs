using System;
using System.Collections.Generic;
using System.Text.Json;
using Backspace.Models;

namespace Backspace.Services.OAuth;

/// <summary>
/// OAuth 2.0 + PKCE flow for Microsoft OneDrive (Personal + Business
/// share one app registration; the <see cref="DestinationKind"/> only
/// affects the consent scope shown to the user, not the registered
/// app).
///
/// Endpoints (per Microsoft Identity Platform v2.0):
///   - Authorize: <c>https://login.microsoftonline.com/common/oauth2/v2.0/authorize</c>
///   - Token:     <c>https://login.microsoftonline.com/common/oauth2/v2.0/token</c>
/// The "common" tenant accepts both personal Microsoft accounts and
/// org-tenant work/school accounts; we register Backspace as
/// "Multitenant + personal accounts" so a single client_id serves
/// both DestinationKind.OneDrivePersonal and OneDriveBusiness.
///
/// Scopes:
///   - <c>Files.ReadWrite.All</c> — full Drive read/write under the
///     authorising user's account (rclone's onedrive backend uses
///     this; without it we'd lose access to nested folders).
///   - <c>offline_access</c> — Microsoft's opt-in for refresh
///     tokens. Without it the token endpoint returns only a
///     short-lived access_token (~60 min); the user would have to
///     re-run the OAuth flow on every token expiry.
///
/// Token response shape (Microsoft Identity Platform v2.0 docs):
/// <code>
///   {
///     "token_type":   "Bearer",
///     "scope":        "Files.ReadWrite.All offline_access",
///     "expires_in":   3600,
///     "ext_expires_in":3600,
///     "access_token": "eyJ0eXAi…",
///     "refresh_token":"M.C507_BAY.0.U.…"
///   }
/// </code>
///
/// rclone's onedrive backend writes the token field in the same
/// generic shape every rclone OAuth remote uses, so we project
/// access_token + refresh_token + computed expiry into that shape
/// — exactly like <see cref="DropboxOAuthProvider"/>.
/// </summary>
public sealed class OneDriveOAuthProvider : IOAuthProvider
{
    private readonly string _clientId;
    private readonly DestinationKind _kind;

    public OneDriveOAuthProvider(DestinationKind kind, string clientId)
    {
        if (kind != DestinationKind.OneDrivePersonal && kind != DestinationKind.OneDriveBusiness)
            throw new ArgumentOutOfRangeException(nameof(kind),
                $"OneDriveOAuthProvider only supports OneDrivePersonal / OneDriveBusiness; got {kind}.");
        _kind = kind;
        _clientId = clientId;
    }

    public DestinationKind Kind => _kind;
    public string ClientId => _clientId;
    public string AuthorizationEndpoint => "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
    public string TokenEndpoint => "https://login.microsoftonline.com/common/oauth2/v2.0/token";

    /// <summary>Full-Drive read/write + offline (refresh token).
    /// rclone's onedrive backend needs Files.ReadWrite.All to traverse
    /// folder hierarchies the user explicitly chose as a destination
    /// path; the narrower Files.ReadWrite.AppFolder would only work
    /// for a sandboxed Backspace folder the user can't pick.</summary>
    public string Scope => "Files.ReadWrite.All offline_access";

    /// <summary>Microsoft's v2.0 authorize endpoint accepts the standard
    /// PKCE / state / scope params with no provider-specific extras.
    /// (Some older Microsoft samples pass <c>response_mode=query</c>;
    /// it's the default for <c>response_type=code</c>, so omitting it
    /// is equivalent and keeps the URL shorter.)</summary>
    public IReadOnlyDictionary<string, string> ExtraAuthorizationParameters { get; } =
        new Dictionary<string, string>();

    public string FormatTokenForRclone(JsonElement r)
    {
        var accessToken = RequireString(r, "access_token");
        // Microsoft returns refresh_token only when offline_access was
        // in the requested scope. We always request it; if it's still
        // missing the user's tenant policy probably disabled it —
        // throw with a readable message so the UI shows what to fix.
        var refreshToken = r.TryGetProperty("refresh_token", out var rt)
                           && rt.ValueKind == JsonValueKind.String
            ? rt.GetString()!
            : throw new InvalidOperationException(
                "OneDrive token response missing refresh_token. Make sure 'offline_access' is granted " +
                "for the Backspace app and that your tenant allows refresh tokens.");
        var expiresIn = r.TryGetProperty("expires_in", out var exp)
            ? exp.GetInt32()
            : throw new InvalidOperationException("OneDrive token response missing expires_in.");

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
            : throw new InvalidOperationException($"OneDrive token response missing or wrong-typed '{name}'.");
}
