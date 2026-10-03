using System.Collections.Generic;
using System.IO;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Pins the wire shape of the materialised <c>rclone.conf</c>. Real
/// rclone reads this file when restic spawns it for a Dropbox /
/// OneDrive / Google Drive repo URI — any drift from the documented
/// INI shape is a silent backup failure, so we lock the format here.
/// </summary>
public class RcloneConfigBuilderTests
{
    [Fact]
    public void Render_EmitsOneSectionPerCloudDestination()
    {
        var dests = new[]
        {
            new Destination { Id = "abcd1234", Name = "Box", Kind = DestinationKind.DropboxAppScoped, OAuthTokenRef = "tk-1" },
            new Destination { Id = "ef567890", Name = "Personal", Kind = DestinationKind.OneDrivePersonal, OAuthTokenRef = "tk-2" },
            new Destination { Id = "11223344", Name = "Work", Kind = DestinationKind.OneDriveBusiness, OAuthTokenRef = "tk-3" },
            new Destination { Id = "55667788", Name = "Drive", Kind = DestinationKind.GoogleDrive, OAuthTokenRef = "tk-4" },
        };

        var conf = RcloneConfigBuilder.Render(dests, NoCustomApps, ResolveToken);

        // Section headers anchor on the same remote name the URI
        // resolver emits, so restic's `rclone:<remote>:<path>` URIs
        // find the matching block.
        Assert.Contains("[dropbox-app-abcd1234]",       conf);
        Assert.Contains("[onedrive-personal-ef567890]", conf);
        Assert.Contains("[onedrive-business-11223344]", conf);
        Assert.Contains("[googledrive-55667788]",       conf);
    }

    [Fact]
    public void Render_DropboxSectionHasTypeAndToken()
    {
        var d = new Destination
        {
            Id = "abc12345",
            Kind = DestinationKind.DropboxAppScoped,
            OAuthTokenRef = "tok-ref",
        };
        var conf = RcloneConfigBuilder.Render(new[] { d }, NoCustomApps, _ => "{\"access_token\":\"AAA\"}");

        Assert.Contains("type = dropbox", conf);
        Assert.Contains("token = {\"access_token\":\"AAA\"}", conf);
    }

    [Fact]
    public void Render_OneDriveBusinessCarriesDriveTypeBusiness()
    {
        // Without drive_type=business rclone defaults to personal and
        // mounts the wrong endpoint — silent failure that lands files
        // in the user's OneDrive Personal folder.
        var d = new Destination
        {
            Id = "biz12345",
            Kind = DestinationKind.OneDriveBusiness,
            OAuthTokenRef = "tk",
        };
        var conf = RcloneConfigBuilder.Render(new[] { d }, NoCustomApps, _ => "{}");

        Assert.Contains("type = onedrive", conf);
        Assert.Contains("drive_type = business", conf);
    }

    [Fact]
    public void Render_GoogleDriveCarriesDriveScope()
    {
        var d = new Destination
        {
            Id = "gdrv1234",
            Kind = DestinationKind.GoogleDrive,
            OAuthTokenRef = "tk",
        };
        var conf = RcloneConfigBuilder.Render(new[] { d }, NoCustomApps, _ => "{}");

        Assert.Contains("type = drive", conf);
        Assert.Contains("scope = drive.file", conf);
    }

    [Fact]
    public void Render_SkipsLocalAndS3Destinations()
    {
        // restic handles local + S3 natively, no rclone hop — those
        // destinations must NOT appear in rclone.conf or rclone will
        // try to mount them on the wrong endpoint.
        var dests = new[]
        {
            new Destination { Id = "loc-1", Kind = DestinationKind.LocalFolder, PathOrSubpath = "/tmp" },
            new Destination { Id = "s3-1",  Kind = DestinationKind.S3Compatible, S3Bucket = "x", S3Endpoint = "h" },
            new Destination { Id = "drv-1", Kind = DestinationKind.GoogleDrive, OAuthTokenRef = "tk" },
        };
        var conf = RcloneConfigBuilder.Render(dests, NoCustomApps, _ => "{}");

        Assert.DoesNotContain("[loc-1]", conf);
        Assert.DoesNotContain("[s3-1]", conf);
        Assert.Contains("[googledrive-drv1",  conf); // GoogleDrive present
    }

    [Fact]
    public void Render_OrdersSectionsByDestinationName()
    {
        // Stable section ordering keeps the file diff-friendly for
        // operators auditing it by hand.
        var dests = new[]
        {
            new Destination { Id = "abc",  Name = "Zeta",  Kind = DestinationKind.GoogleDrive, OAuthTokenRef = "tk" },
            new Destination { Id = "def",  Name = "Alpha", Kind = DestinationKind.DropboxAppScoped, OAuthTokenRef = "tk" },
        };
        var conf = RcloneConfigBuilder.Render(dests, NoCustomApps, _ => "{}");

        var alphaIdx = conf.IndexOf("[dropbox-app-def]");
        var zetaIdx  = conf.IndexOf("[googledrive-abc]");
        Assert.True(alphaIdx >= 0);
        Assert.True(zetaIdx > alphaIdx, "Alphabetical destination ordering must drive section order");
    }

    [Fact]
    public void Render_CustomOAuthApp_OverridesRcloneDefaultClientId()
    {
        // The whole point of letting users register their own OAuth
        // apps is to escape rclone's shared default client_id quota.
        // The custom client_id+secret must land in the section for
        // matching destinations, NOT in unrelated sections.
        var dropbox = new Destination { Id = "drop1234", Name = "Dropbox", Kind = DestinationKind.DropboxAppScoped, OAuthTokenRef = "tk-drop" };
        var gdrive  = new Destination { Id = "drv12345", Name = "Drive",   Kind = DestinationKind.GoogleDrive, OAuthTokenRef = "tk-drv" };

        var apps = new[]
        {
            new RcloneOAuthApp { Provider = DestinationKind.DropboxAppScoped, ClientId = "custom-dropbox-cid", ClientSecretRef = "drop-secret-ref" },
        };

        var conf = RcloneConfigBuilder.Render(
            new[] { dropbox, gdrive }, apps,
            refKey => refKey == "drop-secret-ref" ? "custom-dropbox-csecret" : "<token-blob>");

        // Dropbox section gets the custom client.
        var dropboxSection = SectionFor(conf, "[dropbox-app-drop1234]");
        Assert.Contains("client_id = custom-dropbox-cid", dropboxSection);
        Assert.Contains("client_secret = custom-dropbox-csecret", dropboxSection);

        // Google Drive section is untouched.
        var gdriveSection = SectionFor(conf, "[googledrive-drv12345]");
        Assert.DoesNotContain("client_id", gdriveSection);
        Assert.DoesNotContain("client_secret", gdriveSection);
    }

    [Fact]
    public void Render_CustomOAuthApp_PublicClientHasNoSecret()
    {
        // Installed-app / public OAuth clients don't mint a secret.
        // Setting ClientSecretRef to null/empty must NOT emit a
        // `client_secret = ` line — rclone treats the empty value
        // as an explicit-but-blank secret and rejects it at auth time.
        var d = new Destination { Id = "pub12345", Kind = DestinationKind.GoogleDrive, OAuthTokenRef = "tk" };
        var apps = new[] { new RcloneOAuthApp { Provider = DestinationKind.GoogleDrive, ClientId = "pub-only", ClientSecretRef = null } };

        var conf = RcloneConfigBuilder.Render(new[] { d }, apps, _ => "{}");

        Assert.Contains("client_id = pub-only", conf);
        Assert.DoesNotContain("client_secret", conf);
    }

    [Fact]
    public void Render_FamilyFallback_DropboxAppScopedAppliesToFullAccess()
    {
        // The Settings UI writes one OAuth app per family
        // (Dropbox covers both AppScoped + FullAccess). A destination
        // configured as DropboxFullAccess should pick up the
        // AppScoped-keyed app's client_id via the family fallback in
        // FindMatchingOAuthApp, so the user doesn't have to enter
        // their client_id twice.
        var dest = new Destination { Id = "full1234", Kind = DestinationKind.DropboxFullAccess, OAuthTokenRef = "tk" };
        var apps = new[]
        {
            new RcloneOAuthApp { Provider = DestinationKind.DropboxAppScoped, ClientId = "shared-dropbox-cid" }
        };

        var conf = RcloneConfigBuilder.Render(new[] { dest }, apps, _ => "{}");

        Assert.Contains("client_id = shared-dropbox-cid", conf);
    }

    [Fact]
    public void Render_FamilyFallback_OneDrivePersonalAppliesToBusiness()
    {
        var dest = new Destination { Id = "biz12345", Kind = DestinationKind.OneDriveBusiness, OAuthTokenRef = "tk" };
        var apps = new[]
        {
            new RcloneOAuthApp { Provider = DestinationKind.OneDrivePersonal, ClientId = "shared-onedrive-cid" }
        };

        var conf = RcloneConfigBuilder.Render(new[] { dest }, apps, _ => "{}");

        Assert.Contains("client_id = shared-onedrive-cid", conf);
    }

    [Fact]
    public void Render_ExactMatchWinsOverFamilyFallback()
    {
        // A user who legitimately needs separate apps for AppScoped
        // vs FullAccess gets to register both and have the exact
        // match win.
        var dest = new Destination { Id = "full1234", Kind = DestinationKind.DropboxFullAccess, OAuthTokenRef = "tk" };
        var apps = new[]
        {
            new RcloneOAuthApp { Provider = DestinationKind.DropboxAppScoped,  ClientId = "shared-app" },
            new RcloneOAuthApp { Provider = DestinationKind.DropboxFullAccess, ClientId = "dedicated-full" }
        };

        var conf = RcloneConfigBuilder.Render(new[] { dest }, apps, _ => "{}");

        Assert.Contains("client_id = dedicated-full", conf);
        Assert.DoesNotContain("client_id = shared-app", conf);
    }

    [Fact]
    public void FamilyRepresentative_ReturnsCanonicalKindPerFamily()
    {
        Assert.Equal(DestinationKind.DropboxAppScoped, RcloneConfigBuilder.FamilyRepresentative(DestinationKind.DropboxAppScoped));
        Assert.Equal(DestinationKind.DropboxAppScoped, RcloneConfigBuilder.FamilyRepresentative(DestinationKind.DropboxFullAccess));
        Assert.Equal(DestinationKind.OneDrivePersonal, RcloneConfigBuilder.FamilyRepresentative(DestinationKind.OneDrivePersonal));
        Assert.Equal(DestinationKind.OneDrivePersonal, RcloneConfigBuilder.FamilyRepresentative(DestinationKind.OneDriveBusiness));
        Assert.Equal(DestinationKind.GoogleDrive,     RcloneConfigBuilder.FamilyRepresentative(DestinationKind.GoogleDrive));
        Assert.Equal(DestinationKind.LocalFolder,     RcloneConfigBuilder.FamilyRepresentative(DestinationKind.LocalFolder));
    }

    [Fact]
    public void Render_EmptyClientId_FallsBackToBakedInClientId()
    {
        // A half-populated app entry (Provider set, ClientId blank)
        // is treated as "no BYO override" and the builder falls back
        // to OAuthAppRegistry's baked-in client_id (when one is
        // registered — see the matching wider test
        // Render_FallsBackToOAuthAppRegistry_WhenNoUserOverride).
        // Refresh-token correctness REQUIRES the same client_id that
        // minted the token to be written to rclone.conf; the prior
        // behaviour ("omit client_id, let rclone pick its pooled
        // default") would silently break refresh on tokens minted by
        // Backspace's in-app OAuth flow.
        var d = new Destination { Id = "x", Kind = DestinationKind.DropboxAppScoped, OAuthTokenRef = "tk" };
        var apps = new[] { new RcloneOAuthApp { Provider = DestinationKind.DropboxAppScoped, ClientId = "" } };

        var conf = RcloneConfigBuilder.Render(new[] { d }, apps, _ => "{}");
        var registry = Backspace.Services.OAuth.OAuthAppRegistry.DropboxClientId;
        if (string.IsNullOrEmpty(registry)
            || registry.StartsWith("REPLACE_", System.StringComparison.Ordinal))
        {
            Assert.DoesNotContain("client_id", conf);
        }
        else
        {
            Assert.Contains($"client_id = {registry}", conf);
        }
    }

    /// <summary>Extract a single section's body from the rendered conf
    /// so per-section assertions don't catch matches from unrelated
    /// sections.</summary>
    private static string SectionFor(string conf, string header)
    {
        var start = conf.IndexOf(header);
        Assert.True(start >= 0, $"Expected section {header} in:\n{conf}");
        var end = conf.IndexOf('[', start + header.Length);
        if (end < 0) end = conf.Length;
        return conf.Substring(start, end - start);
    }

    private static readonly IReadOnlyList<RcloneOAuthApp> NoCustomApps = System.Array.Empty<RcloneOAuthApp>();

    private static string ResolveToken(string secretRef) => $"opaque-blob-for-{secretRef}";

    [Fact]
    public void Render_FallsBackToOAuthAppRegistry_WhenNoUserOverride()
    {
        // Critical refresh-token correctness: a Dropbox token issued
        // via Backspace's in-app PKCE flow with the registry's
        // client_id must be paired with the SAME client_id in
        // rclone.conf — otherwise Dropbox rejects the refresh
        // request with "token doesn't belong to this app" once the
        // 4h access_token expires. This test pins that fallback.
        var d = new Destination { Id = "x", Name = "Box",
            Kind = DestinationKind.DropboxAppScoped, OAuthTokenRef = "tk" };
        var conf = RcloneConfigBuilder.Render(new[] { d }, NoCustomApps, _ => "{}");

        var dropboxClientId = Backspace.Services.OAuth.OAuthAppRegistry.DropboxClientId;
        if (string.IsNullOrEmpty(dropboxClientId)
            || dropboxClientId.StartsWith("REPLACE_", System.StringComparison.Ordinal))
        {
            // No baked-in app key yet — rclone.conf rightly omits
            // client_id so rclone uses its pooled default; refresh
            // works only as long as the pooled key matches what
            // issued the token (it won't for in-app OAuth tokens,
            // but that's a separate diagnostic in the OAuth
            // coordinator itself).
            Assert.DoesNotContain("client_id =", conf);
        }
        else
        {
            Assert.Contains($"client_id = {dropboxClientId}", conf);
        }
    }
}
