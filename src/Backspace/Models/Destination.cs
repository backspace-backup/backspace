using System;
using System.Text.Json.Serialization;

namespace Backspace.Models;

/// <summary>
/// A storage target. Translated into an rclone remote URI at run
/// time by <see cref="Services.ResticUriResolver"/>; OAuth tokens
/// are stored in <see cref="Services.SecretsStore"/> and emitted
/// into the generated rclone.conf by
/// <see cref="Services.RcloneConfigBuilder"/>.
/// </summary>
public sealed class Destination
{
    /// <summary>Stable internal id (guid) so renames don't break references.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>User-facing name, e.g. "My Passport" or "Dropbox (work)".</summary>
    public string Name { get; set; } = "";

    public DestinationKind Kind { get; set; }

    /// <summary>
    /// For local / external / network: the folder path.
    /// For cloud: the sub-path within the cloud account (e.g. "local_c").
    /// </summary>
    public string PathOrSubpath { get; set; } = "";

    // ---- volume matching (local + external) ----

    /// <summary>Expected drive letter, e.g. "M:". Null = don't check.</summary>
    public string? ExpectedDriveLetter { get; set; }

    /// <summary>Expected volume label, e.g. "My Passport". Null = don't check.</summary>
    public string? ExpectedVolumeLabel { get; set; }

    // ---- network share credentials (optional) ----

    public string? NetworkUsername { get; set; }

    /// <summary>DPAPI-encrypted if present. Stored only in secrets file, not config.json.</summary>
    public string? NetworkPasswordRef { get; set; }

    // ---- S3 / S3-compatible ----

    public string? S3Endpoint { get; set; }     // e.g. s3.us-east-1.amazonaws.com, <account>.r2.cloudflarestorage.com
    public string? S3Region { get; set; }       // e.g. us-east-1, auto
    public string? S3Bucket { get; set; }
    public string? S3AccessKeyRef { get; set; } // ref into secrets file
    public string? S3SecretKeyRef { get; set; } // ref into secrets file

    // ---- cloud OAuth state (dropbox/onedrive/gdrive) ----

    /// <summary>
    /// SecretsStore ref for the rclone-format token JSON minted by
    /// the in-app OAuth flow
    /// (<see cref="Services.OAuth.OAuthFlowCoordinator"/>) — exactly
    /// the shape rclone's config writer emits: <c>{"access_token":…,
    /// "token_type":"bearer","refresh_token":…,"expiry":…}</c>.
    /// Written verbatim to the generated rclone.conf's <c>token=</c>
    /// field.
    /// </summary>
    public string? OAuthTokenRef { get; set; }

    /// <summary>Canonical Microsoft Graph drive identity discovered during sign-in.</summary>
    public string? OneDriveDriveId { get; set; }
    public string? OneDriveDriveType { get; set; }

    /// <summary>Folder returned by Microsoft's /me/drive/special/approot endpoint.</summary>
    public string? OneDriveAppFolderId { get; set; }

    /// <summary>True only after connecting with the app-folder-only grant.
    /// Missing on older configurations, which must not silently switch repository roots.</summary>
    public bool CloudAppFolderOnly { get; set; }

    /// <summary>
    /// When the OAuth token was last validated (we probe with a list call).
    /// Helps the UI surface a "re-authenticate" prompt proactively.
    /// </summary>
    public DateTime? LastTokenValidatedUtc { get; set; }

    // ---- encryption ----

    /// <summary>
    /// SecretsStore ref for the restic repository password
    /// (used for restic's repo encryption — fed to the engine via
    /// the <c>RESTIC_PASSWORD</c> env var by ResticBackrestEngine).
    /// Generated if the user leaves it blank; we show it once for
    /// them to back up.
    /// </summary>
    public string? StoragePasswordRef { get; set; }

    /// <summary>
    /// Default OFF — most users back up to local drives where the device
    /// is already in their physical possession, and a forgotten password
    /// is the easiest way to lose access to their backups. Cloud
    /// destinations get a "Recommended" badge in the editor so privacy-
    /// conscious users still get nudged to enable it there.
    /// </summary>
    public bool Encrypted { get; set; } = false;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedUtc { get; set; }

    /// <summary>
    /// Build the legacy storage URL for this destination. Kept only
    /// for the ConfigMigrator's import path (Duplicacy-style URLs in
    /// <c>.duplicacy/preferences</c>); production engine call paths
    /// go through <see cref="Services.ResticUriResolver"/> instead.
    /// </summary>
    public string BuildStorageUrl()
    {
        return Kind switch
        {
            DestinationKind.LocalFolder      => PathOrSubpath,
            DestinationKind.ExternalDrive    => PathOrSubpath,
            DestinationKind.NetworkShare     => PathOrSubpath,
            DestinationKind.DropboxAppScoped => $"dropbox://{PathOrSubpath.TrimStart('/')}",
            DestinationKind.DropboxFullAccess=> $"dropbox://{PathOrSubpath.TrimStart('/')}",
            DestinationKind.OneDrivePersonal => $"one://{PathOrSubpath.TrimStart('/')}",
            DestinationKind.OneDriveBusiness => $"odb://{PathOrSubpath.TrimStart('/')}",
            DestinationKind.GoogleDrive      => $"gcd://{PathOrSubpath.TrimStart('/')}",
            DestinationKind.S3Compatible     => $"s3://{S3Region}@{S3Endpoint}/{S3Bucket}/{PathOrSubpath.TrimStart('/')}",
            _ => throw new InvalidOperationException($"Unknown DestinationKind: {Kind}")
        };
    }

    [JsonIgnore]
    public bool NeedsOAuth => Kind is DestinationKind.DropboxAppScoped
        or DestinationKind.DropboxFullAccess
        or DestinationKind.OneDrivePersonal
        or DestinationKind.OneDriveBusiness
        or DestinationKind.GoogleDrive;

    [JsonIgnore]
    public bool IsLocalLike => Kind is DestinationKind.LocalFolder
        or DestinationKind.ExternalDrive
        or DestinationKind.NetworkShare;
}
