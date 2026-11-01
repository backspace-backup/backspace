using System;
using Backspace.Models;
using Backspace.Services;

namespace Backspace.Services.OAuth;

/// <summary>
/// Pick the right <see cref="IOAuthProvider"/> for a destination
/// kind, honouring the user's per-provider override in Settings →
/// OAuth (the <see cref="AppConfig.RcloneOAuthApps"/> list) before
/// falling back to Backspace's baked-in
/// <see cref="OAuthAppRegistry"/> client_id.
///
/// Returns null for destination kinds without an OAuth flow, including
/// the intentionally unsupported full-Dropbox destination.
/// </summary>
public static class OAuthProviderFactory
{
    public static IOAuthProvider? For(DestinationKind kind, AppConfig config, Func<string, string>? resolveSecret = null)
    {
        // Per-user override wins over the baked-in app key.
        var byoApp = config is null ? null
            : RcloneConfigBuilder.FindMatchingOAuthApp(config.RcloneOAuthApps, kind);
        var byoClientId = byoApp is null || string.IsNullOrWhiteSpace(byoApp.ClientId)
            ? null
            : byoApp.ClientId.Trim();

        return kind switch
        {
            DestinationKind.DropboxAppScoped =>
                new DropboxOAuthProvider(byoClientId ?? OAuthAppRegistry.DropboxClientId),

            // DropboxFullAccess intentionally NOT mapped — Backspace
            // doesn't register a full-access app today; the
            // duplicacy.com helper was the only path and it's
            // gone. Future work: register a full-access app and
            // add DropboxFullAccessOAuthProvider.

            DestinationKind.OneDrivePersonal or DestinationKind.OneDriveBusiness =>
                ResolveOneDrive(kind, byoClientId),

            DestinationKind.GoogleDrive =>
                ResolveGoogleDrive(byoClientId, byoClientId is not null && !string.IsNullOrEmpty(byoApp?.ClientSecretRef)
                    ? resolveSecret?.Invoke(byoApp.ClientSecretRef) : null),

            _ => null,
        };
    }

    private static IOAuthProvider? ResolveOneDrive(DestinationKind kind, string? byoClientId)
    {
        var clientId = byoClientId ?? OAuthAppRegistry.OneDriveClientId;
        // Empty string sentinel = no app registered yet on this build.
        // Return null so the UI surfaces "OAuth not yet wired" instead
        // of dispatching a request that would 400 at the authorize
        // endpoint.
        return string.IsNullOrWhiteSpace(clientId) ? null
            : new OneDriveOAuthProvider(kind, clientId);
    }

    private static IOAuthProvider? ResolveGoogleDrive(string? byoClientId, string? clientSecret)
    {
        var clientId = byoClientId ?? OAuthAppRegistry.GoogleDriveClientId;
        return string.IsNullOrWhiteSpace(clientId) ? null
            : new GoogleDriveOAuthProvider(clientId, clientSecret);
    }

}
