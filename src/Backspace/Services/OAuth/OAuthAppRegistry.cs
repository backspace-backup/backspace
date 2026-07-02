namespace Backspace.Services.OAuth;

/// <summary>
/// Per-provider OAuth client_id constants for Backspace's "official"
/// app registrations. Public client_ids only — see
/// <see cref="IOAuthProvider"/> for why no client_secret ships in
/// source (PKCE).
///
/// To override a baked-in client_id with the user's own registered
/// app, go through Settings &gt; OAuth — the per-provider Settings
/// fields land in <see cref="Models.AppConfig.RcloneOAuthApps"/>
/// and <see cref="OAuthProviderFactory"/> consults that list before
/// falling back to the values here.
///
/// What you need to do on each provider's dashboard if you ever
/// re-register Backspace's app:
///   • Dropbox: create a "Scoped Access" app with type "App folder",
///     enable PKCE (Settings &gt; OAuth 2 &gt; "Code grant requires
///     PKCE": yes), add redirect URI <c>http://localhost</c> (any
///     port), grant the <c>files.content.read</c> +
///     <c>files.content.write</c> scopes.
///   • OneDrive: TBD when registered.
///   • Google Drive: TBD when registered.
/// </summary>
public static class OAuthAppRegistry
{
    /// <summary>Backspace's official Dropbox app client_id (App
    /// folder scope). Public key — pasted verbatim into the
    /// authorize URL by <see cref="OAuthFlowCoordinator"/>.</summary>
    public const string DropboxClientId = "xnmsuqfgzzr9gdw";

    /// <summary>Backspace's official OneDrive app client_id.
    /// Placeholder until the Azure App Registrations dashboard
    /// yields an app — a OneDriveOAuthProvider class symmetric to
    /// <see cref="DropboxOAuthProvider"/> will land alongside this
    /// once a client_id is filled in.</summary>
    public const string OneDriveClientId = "";

    /// <summary>Backspace's official Google Drive app client_id.
    /// Placeholder, same shape as OneDrive's.</summary>
    public const string GoogleDriveClientId = "";
}
