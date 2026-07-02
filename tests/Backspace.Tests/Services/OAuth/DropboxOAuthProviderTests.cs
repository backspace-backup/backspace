using System;
using System.Text.Json;
using Backspace.Models;
using Backspace.Services.OAuth;
using Xunit;

namespace Backspace.Tests.Services.OAuth;

/// <summary>
/// Pins the Dropbox token-response → rclone JSON shape down. A
/// regression here would manifest as "remote unreachable" on every
/// Dropbox backup the user runs — bad to catch in production.
/// </summary>
public class DropboxOAuthProviderTests
{
    [Fact]
    public void Kind_MapsToDropboxAppScoped()
    {
        Assert.Equal(DestinationKind.DropboxAppScoped, new DropboxOAuthProvider().Kind);
    }

    [Fact]
    public void AuthorizationEndpoint_IsTheCanonicalDropboxUrl()
    {
        // If Dropbox ever changes their authorize URL, the OAuth
        // dance would silently break with a "host not found" in the
        // browser. Pin the constant so the change forces a code
        // edit.
        Assert.Equal("https://www.dropbox.com/oauth2/authorize",
                     new DropboxOAuthProvider().AuthorizationEndpoint);
    }

    [Fact]
    public void Scope_RequestsBothReadAndWrite()
    {
        // Backup needs write; restore + the auth probe need read.
        // Asking for both up-front means one consent grant covers
        // the lifetime of the destination.
        var scope = new DropboxOAuthProvider().Scope;
        Assert.Contains("files.content.read", scope);
        Assert.Contains("files.content.write", scope);
    }

    [Fact]
    public void OfflineAccess_IsRequested_SoRefreshTokensWork()
    {
        // Without token_access_type=offline, Dropbox returns only a
        // short-lived access_token and rclone has no way to renew
        // when it expires after 4h. Users would re-OAuth daily.
        var extras = new DropboxOAuthProvider().ExtraAuthorizationParameters;
        Assert.True(extras.TryGetValue("token_access_type", out var v));
        Assert.Equal("offline", v);
    }

    [Fact]
    public void FormatTokenForRclone_ProducesRcloneShape()
    {
        // Synthetic Dropbox response. The expected output is the
        // exact JSON shape rclone's config writer expects:
        // {"access_token":"...","token_type":"bearer","refresh_token":"...","expiry":"<RFC3339>"}
        var dropboxResp = """
            {
              "access_token": "sl.ABC123",
              "token_type":   "bearer",
              "expires_in":   14400,
              "refresh_token": "REFRESH456",
              "scope": "files.content.read files.content.write",
              "uid": "12345",
              "account_id": "dbid:abc"
            }
            """;

        using var doc = JsonDocument.Parse(dropboxResp);
        var rcloneJson = new DropboxOAuthProvider().FormatTokenForRclone(doc.RootElement);
        using var parsed = JsonDocument.Parse(rcloneJson);
        var root = parsed.RootElement;

        Assert.Equal("sl.ABC123",  root.GetProperty("access_token").GetString());
        Assert.Equal("bearer",     root.GetProperty("token_type").GetString());
        Assert.Equal("REFRESH456", root.GetProperty("refresh_token").GetString());

        // expiry must parse as RFC3339 and be ~14400 seconds out.
        var expiry = DateTime.Parse(root.GetProperty("expiry").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
        var deltaSec = (expiry - DateTime.UtcNow).TotalSeconds;
        Assert.InRange(deltaSec, 14380, 14420); // ±20s slack for test wall-clock skew

        // We never leak Dropbox's uid / account_id / scope into the
        // rclone blob — those are Dropbox-specific metadata, not
        // part of rclone's token grammar.
        Assert.False(root.TryGetProperty("uid", out _));
        Assert.False(root.TryGetProperty("account_id", out _));
        Assert.False(root.TryGetProperty("scope", out _));
        Assert.False(root.TryGetProperty("expires_in", out _));
    }

    [Fact]
    public void FormatTokenForRclone_ThrowsWithFieldNameOnMissingAccessToken()
    {
        // A future Dropbox change that renames the field shouldn't
        // surface as a NullReferenceException — surface the missing
        // field name so the operator can patch the provider quickly.
        var bad = JsonDocument.Parse("""{"refresh_token":"x","expires_in":60}""");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new DropboxOAuthProvider().FormatTokenForRclone(bad.RootElement));
        Assert.Contains("access_token", ex.Message);
    }

    [Fact]
    public void FormatTokenForRclone_ThrowsWithFieldNameOnMissingExpiresIn()
    {
        var bad = JsonDocument.Parse("""{"access_token":"a","refresh_token":"r"}""");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new DropboxOAuthProvider().FormatTokenForRclone(bad.RootElement));
        Assert.Contains("expires_in", ex.Message);
    }
}
