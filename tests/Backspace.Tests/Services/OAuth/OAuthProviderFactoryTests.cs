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
    public void For_GoogleDrive_UsesBackspaceRegistration()
    {
        var provider = OAuthProviderFactory.For(DestinationKind.GoogleDrive, new AppConfig());
        Assert.IsType<GoogleDriveOAuthProvider>(provider);
        Assert.Equal(OAuthAppRegistry.GoogleDriveClientId, provider!.ClientId);
        Assert.False(string.IsNullOrWhiteSpace(provider.ClientSecret));
        Assert.True(provider.ClientSecret == OAuthAppRegistry.GoogleDriveClientSecret,
            "Google sign-in must use its matching desktop registration credential.");
    }

    [Theory]
    [InlineData("custom-google-id", "secret-ref", "fixture-secret")]
    [InlineData("custom-google-id", null, null)]
    [InlineData("   ", "unused-secret-ref", null)]
    public void For_GoogleDrive_KeepsCustomAndOfficialCredentialsTogether(string clientId, string? secretRef, string? secret)
    {
        var cfg = new AppConfig
        {
            RcloneOAuthApps = { new RcloneOAuthApp { Provider = DestinationKind.GoogleDrive, ClientId = clientId, ClientSecretRef = secretRef } }
        };
        var resolved = 0;
        var provider = OAuthProviderFactory.For(DestinationKind.GoogleDrive, cfg, key =>
        {
            resolved++;
            Assert.Equal("secret-ref", key);
            return "fixture-secret";
        })!;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            Assert.Equal(OAuthAppRegistry.GoogleDriveClientId, provider.ClientId);
            Assert.True(provider.ClientSecret == OAuthAppRegistry.GoogleDriveClientSecret,
                "A blank override must retain the official credential pair.");
            Assert.Equal(0, resolved);
        }
        else
        {
            Assert.Equal(clientId, provider.ClientId);
            Assert.Equal(secret, provider.ClientSecret);
            Assert.Equal(secretRef is null ? 0 : 1, resolved);
        }
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

    [Fact]
    public void For_OneDriveBusiness_UsesSameExactRegistrationAsRefresh()
    {
        var cfg = new AppConfig
        {
            RcloneOAuthApps =
            {
                new RcloneOAuthApp { Provider = DestinationKind.OneDrivePersonal, ClientId = "family-registration" },
                new RcloneOAuthApp { Provider = DestinationKind.OneDriveBusiness, ClientId = " business-registration " }
            }
        };
        Assert.Equal("business-registration", OAuthProviderFactory.For(DestinationKind.OneDriveBusiness, cfg)!.ClientId);
    }
}
