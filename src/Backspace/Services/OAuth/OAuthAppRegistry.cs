namespace Backspace.Services.OAuth;

/// <summary>
/// Per-provider OAuth client_id constants for Backspace's "official"
/// app registrations. Google also requires its issued desktop-app secret;
/// it is shared app configuration, not a confidential credential or user token.
/// PKCE and each user's consent remain required.
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
///     allow public clients (PKCE), register the exact callback
///     <c>http://localhost:53682/callback</c>, and enable the scopes
///     requested by <see cref="DropboxOAuthProvider"/>.
///   • OneDrive: native <c>http://localhost/callback</c>, organizational
///     and personal accounts, delegated Files.ReadWrite.AppFolder. See the
///     registration guide for public-client and publisher settings.
///   • Google Drive: Desktop app, drive.appdata, External audience.
/// </summary>
public static class OAuthAppRegistry
{
    /// <summary>Backspace's official Dropbox app client_id (App
    /// folder scope). Public key — pasted verbatim into the
    /// authorize URL by <see cref="OAuthFlowCoordinator"/>.</summary>
    public const string DropboxClientId = "xnmsuqfgzzr9gdw";

    /// <summary>Dropbox matches the port as well as the callback path.</summary>
    public const int DropboxCallbackPort = 53682;

    /// <summary>Backspace's public Microsoft application ID, registered
    /// for organizational and personal accounts.</summary>
    public const string OneDriveClientId = "d23e7f5b-7a24-4a74-88f8-37990e6e066a";

    /// <summary>Backspace's official Google Drive app client_id.
    /// Registered under the owner-controlled backspace-511510 project.</summary>
    public const string GoogleDriveClientId = "470920767794-287o09bvro3keb6hqrjenafetkbp62cb.apps.googleusercontent.com";

    /// <summary>Google's shared desktop registration credential. Installed
    /// apps cannot keep it confidential; it grants no account access by itself.
    /// Never replace this with a web-server secret or a user's account token.</summary>
    public const string GoogleDriveClientSecret = "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";
}
