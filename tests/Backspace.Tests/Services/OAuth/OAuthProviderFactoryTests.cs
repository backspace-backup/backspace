using Backspace.Models;
using Backspace.Services.OAuth;
using Xunit;

namespace Backspace.Tests.Services.OAuth;

/// <summary>
/// Pins the factory's BYO-override priority + family-fallback
/// behaviour. A regression here would silently mint tokens against
/// the wrong client_id — the user wouldn't notice until the next
/// rclone refresh failed.
/// </summary>
public class OAuthProviderFactoryTests
{
    [Fact]
    public void For_DropboxAppScoped_UsesBakedInClientId_WhenNoOverride()
    {
        var cfg = new AppConfig(); // no RcloneOAuthApps entries
        var provider = OAuthProviderFactory.For(DestinationKind.DropboxAppScoped, cfg);

        Assert.NotNull(provider);
        Assert.Equal(DestinationKind.DropboxAppScoped, provider!.Kind);
        Assert.Equal(OAuthAppRegistry.DropboxClientId, provider.ClientId);
    }

    [Fact]
    public void For_DropboxAppScoped_HonoursBYOOverride()
    {
        // User pasted their own Dropbox app key under Settings →
        // OAuth. The factory must hand that to the provider so
        // the OAuth dance + the eventual rclone refresh use it.
        var cfg = new AppConfig
        {
            RcloneOAuthApps =
            {
                new RcloneOAuthApp
                {
                    Provider = DestinationKind.DropboxAppScoped,
                    ClientId = "user-provided-dropbox-id",
                },
            },
        };
        var provider = OAuthProviderFactory.For(DestinationKind.DropboxAppScoped, cfg);

        Assert.NotNull(provider);
        Assert.Equal("user-provided-dropbox-id", provider!.ClientId);
    }

    [Fact]
    public void For_DropboxAppScoped_FallsBackToBakedIn_WhenOverrideIsBlank()
    {
        // Settings stores empty-string when the user clears the
        // input. We MUST NOT pass "" through as the OAuth flow
        // client_id — the coordinator would reject it as a
        // placeholder. Treat blank as "use the baked-in default".
        var cfg = new AppConfig
        {
            RcloneOAuthApps =
            {
                new RcloneOAuthApp
                {
                    Provider = DestinationKind.DropboxAppScoped,
                    ClientId = "   ", // whitespace
                },
            },
        };
        var provider = OAuthProviderFactory.For(DestinationKind.DropboxAppScoped, cfg);

        Assert.Equal(OAuthAppRegistry.DropboxClientId, provider!.ClientId);
    }

    [Fact]
    public void For_DropboxFullAccess_ReturnsNull_UntilRegistered()
    {
        // Backspace's official Dropbox app is App-folder only.
        // The full-access kind exists in DestinationKind for
        // future expansion; the factory returns null so the UI
        // can show an actionable "not wired yet" message rather
        // than running the OAuth dance against an unregistered
        // app and getting a 400 from Dropbox.
        Assert.Null(OAuthProviderFactory.For(DestinationKind.DropboxFullAccess, new AppConfig()));
    }

    [Fact]
    public void For_OneDriveAndGoogleDrive_ReturnNull_UntilRegistered()
    {
        // Until the OneDrive/Google Drive app keys land in
        // OAuthAppRegistry the factory has no provider to hand
        // back. UI must surface "OAuth flow not wired yet"
        // instead of falling through to a non-functional Connect.
        Assert.Null(OAuthProviderFactory.For(DestinationKind.OneDrivePersonal, new AppConfig()));
        Assert.Null(OAuthProviderFactory.For(DestinationKind.OneDriveBusiness, new AppConfig()));
        Assert.Null(OAuthProviderFactory.For(DestinationKind.GoogleDrive,      new AppConfig()));
    }

    [Fact]
    public void For_AcceptsNullConfig()
    {
        // Defensive: a null config (the locator isn't fully
        // hydrated yet) shouldn't NRE — return the baked-in
        // default.
        var provider = OAuthProviderFactory.For(DestinationKind.DropboxAppScoped, null!);
        Assert.NotNull(provider);
        Assert.Equal(OAuthAppRegistry.DropboxClientId, provider!.ClientId);
    }
}
