using System;
using System.Collections.Generic;
using System.Text.Json;
using Backspace.Models;

namespace Backspace.Services.OAuth;

/// <summary>
/// OAuth 2.0 + PKCE flow for Dropbox's App-folder scope.
///
/// Why App-folder rather than Full Dropbox: Backspace's official
/// Dropbox app is registered as App-folder ("Scoped Access — App
/// folder"). Backups land at
/// <c>/Apps/&lt;backspace-app-name&gt;/...</c> in the user's
/// Dropbox — sandboxed away from the user's other files. The Full-
/// access kind exists in <see cref="DestinationKind"/> for future
/// use; we don't wire OAuth for it until there's a registered
/// full-access app.
///
/// The token-response shape (per Dropbox's OAuth2 docs) is:
/// <code>
///   {
///     "access_token": "sl.…",
///     "token_type":   "bearer",
///     "expires_in":   14400,
///     "refresh_token":"…",
///     "scope":        "files.content.read files.content.write",
///     "uid":          "…",
///     "account_id":   "dbid:…"
///   }
/// </code>
/// We convert <c>expires_in</c> to an absolute <c>expiry</c>
/// timestamp because that's the field name rclone's config writer
/// uses. Other fields rclone doesn't read get dropped.
/// </summary>
public sealed class DropboxOAuthProvider : IOAuthProvider
{
    private readonly string _clientId;

    public DropboxOAuthProvider() : this(OAuthAppRegistry.DropboxClientId) { }

    /// <summary>Construct against a specific client_id — used by
    /// <see cref="OAuthProviderFactory"/> when the user has pasted
    /// a BYO Dropbox app key into Settings.</summary>
    public DropboxOAuthProvider(string clientId) => _clientId = clientId;

    public DestinationKind Kind => DestinationKind.DropboxAppScoped;
    public string ClientId => _clientId;
    public string AuthorizationEndpoint => "https://www.dropbox.com/oauth2/authorize";
    public string TokenEndpoint => "https://api.dropboxapi.com/oauth2/token";
    public string LoopbackHost => "localhost";
    public int? LoopbackPort => OAuthAppRegistry.DropboxCallbackPort;

    /// <summary>Content access backs up and restores files, metadata read
    /// lists them, and account info supports rclone's space-usage query.
    /// The app registration confines file access to Backspace's app folder.</summary>
    public string Scope => "account_info.read files.metadata.read files.content.read files.content.write";

    /// <summary><c>token_access_type=offline</c> is Dropbox's
    /// opt-in for refresh tokens; without it the token endpoint
    /// returns only a short-lived access_token and rclone has no
    /// way to renew once it expires (which it does after four
    /// hours). The user would have to re-run the OAuth flow daily.
    /// With offline-mode we get a long-lived refresh_token and
    /// rclone transparently renews.</summary>
    public IReadOnlyDictionary<string, string> ExtraAuthorizationParameters { get; } =
        new Dictionary<string, string>
        {
            ["token_access_type"] = "offline",
        };

    public string FormatTokenForRclone(JsonElement r)
    {
        // Required fields. Throw with the offending field name so
        // a Dropbox API shape change surfaces a readable error
        // rather than a NullReferenceException somewhere downstream.
        var accessToken = RequireString(r, "access_token");
        var refreshToken = RequireString(r, "refresh_token");
        var expiresIn = r.TryGetProperty("expires_in", out var exp)
            ? exp.GetInt32()
            : throw new InvalidOperationException("Dropbox token response missing expires_in.");

        // Convert to absolute RFC3339 expiry like rclone writes
        // ("2026-05-19T10:23:45.123Z"). rclone uses Go's
        // time.RFC3339Nano which round-trips through .NET's "o".
        var expiry = DateTime.UtcNow.AddSeconds(expiresIn).ToString("o");

        return JsonSerializer.Serialize(new
        {
            access_token = accessToken,
            token_type = "bearer",
            refresh_token = refreshToken,
            expiry,
        });
    }

    private static string RequireString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new InvalidOperationException($"Dropbox token response missing or wrong-typed '{name}'.");
}
