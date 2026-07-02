using System.Linq;
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
/// Returns null when the requested kind has no OAuth flow wired
/// yet (OneDrive / Google Drive, for now) — caller surfaces a
/// "not supported yet" toast.
/// </summary>
public static class OAuthProviderFactory
{
    public static IOAuthProvider? For(DestinationKind kind, AppConfig config)
    {
        // Per-user override wins over the baked-in app key.
        var byoApp = config?.RcloneOAuthApps
            .FirstOrDefault(a => MatchesFamily(a.Provider, kind));
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
                ResolveGoogleDrive(byoClientId),

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

    private static IOAuthProvider? ResolveGoogleDrive(string? byoClientId)
    {
        var clientId = byoClientId ?? OAuthAppRegistry.GoogleDriveClientId;
        return string.IsNullOrWhiteSpace(clientId) ? null
            : new GoogleDriveOAuthProvider(clientId);
    }

    /// <summary>Map a stored OAuth-app entry's kind to the broader
    /// family for lookup. Two kinds (DropboxAppScoped /
    /// DropboxFullAccess; OneDrivePersonal / OneDriveBusiness)
    /// share an OAuth app on Backspace's side because the consent
    /// scope is the differentiator, not the registered app.</summary>
    private static bool MatchesFamily(DestinationKind storedKind, DestinationKind requested) =>
        Family(storedKind) == Family(requested);

    private static string Family(DestinationKind k) => k switch
    {
        DestinationKind.DropboxAppScoped or DestinationKind.DropboxFullAccess => "dropbox",
        DestinationKind.OneDrivePersonal or DestinationKind.OneDriveBusiness  => "onedrive",
        DestinationKind.GoogleDrive                                            => "googledrive",
        _ => k.ToString(),
    };
}
