using System;
using System.IO;
using System.Linq;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Pins the destination-kind → (restic-URI, env-vars) mapping. Real
/// restic at the other end of these strings does its own validation;
/// these tests just make sure we're emitting the shapes restic
/// documents and that the rclone remote name stays stable across
/// destination renames.
/// </summary>
public class ResticUriResolverTests
{
    [Fact]
    public void LocalFolder_ReturnsBarePathAndEmptyEnv()
    {
        var d = new Destination
        {
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = "/tmp/local-store/",
        };
        var r = ResticUriResolver.Resolve(d, new SecretsStore(), "/tmp/rclone.conf");

        // Trailing slash stripped — restic + log scanners both prefer
        // the canonical form.
        Assert.Equal("/tmp/local-store", r.Uri);
        Assert.Empty(r.Env);
    }

    [Fact]
    public void S3_EmitsCanonicalUriAndAwsEnv()
    {
        var secrets = new SecretsStore();
        var d = new Destination
        {
            Kind = DestinationKind.S3Compatible,
            S3Endpoint = "https://s3.us-east-1.amazonaws.com",
            S3Region = "us-east-1",
            S3Bucket = "photos",
            S3AccessKeyRef = secrets.PutNew("aws-key", "AKIAEXAMPLE"),
            S3SecretKeyRef = secrets.PutNew("aws-secret", "very-secret-key"),
            PathOrSubpath = "backspace/2026",
        };
        var r = ResticUriResolver.Resolve(d, secrets, "/tmp/rclone.conf");

        // Scheme stripped (restic's default is HTTPS), subpath kept,
        // forward-slash form regardless of input separators.
        Assert.Equal("s3:s3.us-east-1.amazonaws.com/photos/backspace/2026", r.Uri);

        Assert.Contains("AWS_ACCESS_KEY_ID=AKIAEXAMPLE", r.Env);
        Assert.Contains("AWS_SECRET_ACCESS_KEY=very-secret-key", r.Env);
        Assert.Contains("AWS_DEFAULT_REGION=us-east-1", r.Env);
    }

    [Fact]
    public void S3_EmptyEndpointOrBucket_Throws()
    {
        var bare = new Destination { Kind = DestinationKind.S3Compatible };
        Assert.Throws<InvalidOperationException>(() =>
            ResticUriResolver.Resolve(bare, new SecretsStore(), "/tmp/rclone.conf"));
    }

    [Fact]
    public void Dropbox_EmitsRcloneUriAndConfigEnv()
    {
        var d = new Destination
        {
            Id = "abcd1234ef567890",
            Kind = DestinationKind.DropboxAppScoped,
            PathOrSubpath = "backspace-store",
        };
        var r = ResticUriResolver.Resolve(d, new SecretsStore(), "/tmp/our/rclone.conf");

        // rclone:<remote>:<path> -- canonical form. Remote name pins
        // to "dropbox-app-<8-hex-prefix>" so a rename of the destination
        // doesn't fork the config section.
        Assert.StartsWith("rclone:dropbox-app-abcd1234:", r.Uri);
        Assert.EndsWith(":backspace-store", r.Uri);
        Assert.Contains("RCLONE_CONFIG=/tmp/our/rclone.conf", r.Env);
    }

    [Fact]
    public void Dropbox_EmptySubpath_EmitsTrailingColon()
    {
        // rclone:<remote>:  (note the trailing colon) means "the root
        // of this remote". restic understands the form; we preserve it
        // verbatim rather than collapsing to "rclone:<remote>" which
        // would be syntactically invalid.
        var d = new Destination
        {
            Id = "00000000ffff",
            Kind = DestinationKind.GoogleDrive,
            PathOrSubpath = "",
        };
        var r = ResticUriResolver.Resolve(d, new SecretsStore(), "/tmp/rclone.conf");
        Assert.EndsWith(":", r.Uri);
    }

    [Fact]
    public void RcloneRemoteNameFor_IsStableAcrossNameRenames()
    {
        var a = new Destination { Id = "abcdef0123456789", Name = "Dropbox personal", Kind = DestinationKind.DropboxAppScoped };
        var b = new Destination { Id = "abcdef0123456789", Name = "Renamed", Kind = DestinationKind.DropboxAppScoped };

        Assert.Equal(
            ResticUriResolver.RcloneRemoteNameFor(a),
            ResticUriResolver.RcloneRemoteNameFor(b));
    }

    [Fact]
    public void RcloneRemoteNameFor_HasKindTagPrefix()
    {
        Assert.StartsWith("dropbox-app-",       RemoteName(DestinationKind.DropboxAppScoped));
        Assert.StartsWith("dropbox-full-",      RemoteName(DestinationKind.DropboxFullAccess));
        Assert.StartsWith("onedrive-personal-", RemoteName(DestinationKind.OneDrivePersonal));
        Assert.StartsWith("onedrive-business-", RemoteName(DestinationKind.OneDriveBusiness));
        Assert.StartsWith("googledrive-",       RemoteName(DestinationKind.GoogleDrive));
    }

    private static string RemoteName(DestinationKind k) =>
        ResticUriResolver.RcloneRemoteNameFor(new Destination { Id = "abcdef01", Kind = k });
}
