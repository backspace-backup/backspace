using System;
using System.Text.Json;
using Backspace.Models;
using Backspace.Services.OAuth;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Unit-test the per-provider OAuth glue: endpoint URLs match the
/// documented Microsoft / Google addresses, scopes are exactly the
/// strings the providers need (drive.file vs drive matters — see
/// docstrings), extra auth-URL params include the right opt-ins for
/// refresh tokens, and the token-response parser projects into
/// rclone's expected shape.
/// </summary>
public class OAuthProviderTests
{
    // ---------- OneDrive ----------

    [Fact]
    public void OneDrive_endpoints_target_microsoft_common_tenant()
    {
        var p = new OneDriveOAuthProvider(DestinationKind.OneDrivePersonal, "fake-client-id");
        Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/authorize", p.AuthorizationEndpoint);
        Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/token", p.TokenEndpoint);
    }

    [Fact]
    public void OneDrive_scope_includes_files_readwrite_all_and_offline_access()
    {
        var p = new OneDriveOAuthProvider(DestinationKind.OneDrivePersonal, "x");
        Assert.Contains("Files.ReadWrite.All", p.Scope);
        Assert.Contains("offline_access", p.Scope);
    }

    [Fact]
    public void OneDrive_rejects_unsupported_kind()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OneDriveOAuthProvider(DestinationKind.GoogleDrive, "x"));
    }

    [Fact]
    public void OneDrive_business_and_personal_share_the_app_but_differ_in_kind()
    {
        var personal = new OneDriveOAuthProvider(DestinationKind.OneDrivePersonal, "id");
        var business = new OneDriveOAuthProvider(DestinationKind.OneDriveBusiness, "id");
        Assert.Equal(DestinationKind.OneDrivePersonal, personal.Kind);
        Assert.Equal(DestinationKind.OneDriveBusiness, business.Kind);
        Assert.Equal(personal.ClientId, business.ClientId);
        Assert.Equal(personal.AuthorizationEndpoint, business.AuthorizationEndpoint);
    }

    [Fact]
    public void OneDrive_token_parser_emits_rclone_shape_with_absolute_expiry()
    {
        var p = new OneDriveOAuthProvider(DestinationKind.OneDrivePersonal, "x");
        var response = JsonDocument.Parse("""
        {
            "token_type": "Bearer",
            "scope": "Files.ReadWrite.All offline_access",
            "expires_in": 3600,
            "access_token": "eyJ-fake-access-token",
            "refresh_token": "M.fake-refresh"
        }
        """).RootElement;

        var json = p.FormatTokenForRclone(response);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("eyJ-fake-access-token", doc.RootElement.GetProperty("access_token").GetString());
        Assert.Equal("Bearer", doc.RootElement.GetProperty("token_type").GetString());
        Assert.Equal("M.fake-refresh", doc.RootElement.GetProperty("refresh_token").GetString());
        Assert.True(DateTime.TryParse(doc.RootElement.GetProperty("expiry").GetString(), out _),
            "expiry must be a valid RFC3339 timestamp");
    }

    [Fact]
    public void OneDrive_token_parser_throws_when_refresh_token_missing()
    {
        // If tenant policy disabled refresh tokens, the response would
        // come back without one. The parser refuses silently — restore
        // would otherwise break at the first ~1h boundary with no
        // explanation.
        var p = new OneDriveOAuthProvider(DestinationKind.OneDrivePersonal, "x");
        var response = JsonDocument.Parse("""
        {
            "token_type": "Bearer",
            "expires_in": 3600,
            "access_token": "x"
        }
        """).RootElement;
        var ex = Assert.Throws<InvalidOperationException>(() => p.FormatTokenForRclone(response));
        Assert.Contains("refresh_token", ex.Message);
        Assert.Contains("offline_access", ex.Message);
    }

    // ---------- Google Drive ----------

    [Fact]
    public void GoogleDrive_endpoints_target_official_google_urls()
    {
        var p = new GoogleDriveOAuthProvider("fake");
        Assert.Equal("https://accounts.google.com/o/oauth2/v2/auth", p.AuthorizationEndpoint);
        Assert.Equal("https://oauth2.googleapis.com/token", p.TokenEndpoint);
    }

    [Fact]
    public void GoogleDrive_scope_is_drive_file_not_full_drive()
    {
        // Critical: drive.file dodges Google's CASA Tier-2 security
        // audit. If a future refactor ever flips this to "drive",
        // someone is about to spend $15K+ on certification we
        // explicitly designed around.
        var p = new GoogleDriveOAuthProvider("x");
        Assert.Equal("https://www.googleapis.com/auth/drive.file", p.Scope);
        Assert.DoesNotContain("/drive ", p.Scope + " ");  // standalone "drive" not present
    }

    [Fact]
    public void GoogleDrive_auth_url_extras_force_consent_and_offline()
    {
        // Google's gotcha: access_type=offline alone returns a
        // refresh_token only on first consent. prompt=consent forces
        // the consent screen so refresh tokens ALWAYS come back —
        // see provider class docstring for the full story.
        var p = new GoogleDriveOAuthProvider("x");
        Assert.Equal("offline", p.ExtraAuthorizationParameters["access_type"]);
        Assert.Equal("consent", p.ExtraAuthorizationParameters["prompt"]);
    }

    [Fact]
    public void GoogleDrive_token_parser_emits_rclone_shape()
    {
        var p = new GoogleDriveOAuthProvider("x");
        var response = JsonDocument.Parse("""
        {
            "access_token": "ya29.fake-access",
            "expires_in": 3599,
            "refresh_token": "1//fake-refresh",
            "scope": "https://www.googleapis.com/auth/drive.file",
            "token_type": "Bearer"
        }
        """).RootElement;

        var json = p.FormatTokenForRclone(response);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("ya29.fake-access", doc.RootElement.GetProperty("access_token").GetString());
        Assert.Equal("Bearer", doc.RootElement.GetProperty("token_type").GetString());
        Assert.Equal("1//fake-refresh", doc.RootElement.GetProperty("refresh_token").GetString());
        Assert.True(DateTime.TryParse(doc.RootElement.GetProperty("expiry").GetString(), out _));
    }

    [Fact]
    public void GoogleDrive_token_parser_throws_with_actionable_message_when_refresh_missing()
    {
        // Most common cause: an auth URL that omitted prompt=consent.
        // Our provider sets both extras, so this only fires if a BYO
        // user mis-configures their app. Make the message tell them
        // exactly what to fix.
        var p = new GoogleDriveOAuthProvider("x");
        var response = JsonDocument.Parse("""
        {
            "access_token": "ya29.fake",
            "expires_in": 3599,
            "token_type": "Bearer"
        }
        """).RootElement;
        var ex = Assert.Throws<InvalidOperationException>(() => p.FormatTokenForRclone(response));
        Assert.Contains("refresh_token", ex.Message);
        Assert.Contains("access_type=offline", ex.Message);
        Assert.Contains("prompt=consent", ex.Message);
    }

    // ---------- Factory wiring ----------

    [Fact]
    public void Factory_returns_OneDriveProvider_when_clientId_is_set_BYO()
    {
        var config = new AppConfig();
        config.RcloneOAuthApps.Add(new RcloneOAuthApp
        {
            Provider = DestinationKind.OneDrivePersonal,
            ClientId = "byo-client-id-12345",
        });
        var provider = OAuthProviderFactory.For(DestinationKind.OneDrivePersonal, config);
        Assert.IsType<OneDriveOAuthProvider>(provider);
        Assert.Equal("byo-client-id-12345", provider!.ClientId);
    }

    [Fact]
    public void Factory_returns_null_when_OneDrive_clientId_unset_in_both_BYO_and_registry()
    {
        // OAuthAppRegistry.OneDriveClientId ships as "" today (no
        // official app registered yet). With no BYO override, the
        // factory must return null so the UI shows the actionable
        // "not wired" message rather than a 400 at authorize-time.
        var config = new AppConfig();
        var provider = OAuthProviderFactory.For(DestinationKind.OneDrivePersonal, config);
        Assert.Null(provider);
    }

    [Fact]
    public void Factory_returns_GoogleDriveProvider_when_clientId_is_set_BYO()
    {
        var config = new AppConfig();
        config.RcloneOAuthApps.Add(new RcloneOAuthApp
        {
            Provider = DestinationKind.GoogleDrive,
            ClientId = "byo-google-id.apps.googleusercontent.com",
        });
        var provider = OAuthProviderFactory.For(DestinationKind.GoogleDrive, config);
        Assert.IsType<GoogleDriveOAuthProvider>(provider);
        Assert.Equal("byo-google-id.apps.googleusercontent.com", provider!.ClientId);
    }
}
