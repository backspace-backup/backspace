using System;
using System.Text.Json;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.OAuth;
using Xunit;

namespace Backspace.Tests.Services.OAuth;

public sealed class CloudFolderAccessTests
{
    [Theory]
    [InlineData(DestinationKind.GoogleDrive, "https://www.googleapis.com/auth/drive.file")]
    [InlineData(DestinationKind.GoogleDrive, "https://www.googleapis.com/auth/drive.appdata https://www.googleapis.com/auth/drive")]
    [InlineData(DestinationKind.OneDrivePersonal, "Files.ReadWrite.All offline_access")]
    [InlineData(DestinationKind.OneDriveBusiness, "Files.ReadWrite.AppFolder Files.Read.All offline_access")]
    [InlineData(DestinationKind.OneDrivePersonal, "")]
    public void New_sign_in_rejects_broad_or_missing_grants(DestinationKind kind, string scope)
    {
        IOAuthProvider provider = kind == DestinationKind.GoogleDrive ? new GoogleDriveOAuthProvider("test")
            : new OneDriveOAuthProvider(kind, "test");
        using var response = JsonDocument.Parse(JsonSerializer.Serialize(new {
            access_token = "private-access", refresh_token = "private-refresh", expires_in = 3600, scope }));
        var failure = Assert.Throws<InvalidOperationException>(() => provider.FormatTokenForRclone(response.RootElement));
        Assert.Contains("own folder", failure.Message);
        Assert.DoesNotContain("private-access", failure.Message);
        Assert.DoesNotContain("private-refresh", failure.Message);
    }

    [Fact]
    public void Microsoft_resource_qualified_app_folder_scope_is_accepted()
    {
        using var response = JsonDocument.Parse("""{"scope":"https://graph.microsoft.com/Files.ReadWrite.AppFolder offline_access"}""");
        Assert.Contains("AppFolder", CloudFolderAccess.RequireScope(response.RootElement, DestinationKind.OneDrivePersonal));
    }

    [Theory]
    [InlineData(DestinationKind.GoogleDrive)]
    [InlineData(DestinationKind.OneDrivePersonal)]
    [InlineData(DestinationKind.OneDriveBusiness)]
    public void Legacy_destinations_cannot_silently_switch_repository_root(DestinationKind kind)
    {
        // A real older config omits the new field; deserialization must leave it false.
        var destination = JsonSerializer.Deserialize<Destination>("""{"OAuthTokenRef":"old-token","PathOrSubpath":"my-existing-repository"}""")!;
        destination.Kind = kind;
        var resolvedSecret = false;
        var failure = Assert.Throws<InvalidOperationException>(() => RcloneConfigBuilder.Render(
            new[] { destination }, Array.Empty<RcloneOAuthApp>(), _ => { resolvedSecret = true; return "private-token"; }));
        Assert.Contains("not been moved or deleted", failure.Message);
        Assert.False(resolvedSecret);
        Assert.Equal("my-existing-repository", destination.PathOrSubpath);
        Assert.False(destination.CloudAppFolderOnly);
    }

    [Theory]
    [InlineData(null, "folder")]
    [InlineData("drive", null)]
    public void OneDrive_cannot_fall_back_to_the_drive_root(string? driveId, string? folderId)
    {
        var destination = new Destination { Kind = DestinationKind.OneDrivePersonal, CloudAppFolderOnly = true,
            OneDriveDriveId = driveId, OneDriveDriveType = "personal", OneDriveAppFolderId = folderId };
        Assert.Throws<InvalidOperationException>(() => RcloneConfigBuilder.Render(
            new[] { destination }, Array.Empty<RcloneOAuthApp>(), _ => "{}"));
    }
}
