using System;
using System.Collections.Generic;
using System.Linq;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>
/// Maps a Backspace <see cref="Destination"/> to the (URI, env) pair the
/// Restic engine needs in order to talk to that storage.
///
/// Restic understands native protocols for local paths and S3:
///
///   <c>/path/to/repo</c>                                — filesystem
///   <c>s3:s3.amazonaws.com/&lt;bucket&gt;/&lt;path&gt;</c> — S3 + S3-compatible
///
/// For Dropbox / OneDrive / Google Drive the wire format is rclone:
///
///   <c>rclone:&lt;remote-name&gt;:&lt;path&gt;</c>
///
/// where <c>&lt;remote-name&gt;</c> is the section header in an
/// rclone.conf file we materialise via
/// <see cref="RcloneConfigBuilder"/>. The <c>RCLONE_CONFIG</c> env var
/// points restic / rclone at our config so they don't read the user's
/// global <c>~/.config/rclone/rclone.conf</c>.
///
/// Credentials returned in <see cref="ResticEnvironment.Env"/> are the
/// process-environment shape both restic and rclone expect (e.g.
/// <c>AWS_ACCESS_KEY_ID=...</c>). Backrest accepts them verbatim on
/// <c>Repo.env</c>.
/// </summary>
public static class ResticUriResolver
{
    public static ResticEnvironment Resolve(Destination destination, SecretsStore secrets, string rcloneConfigPath)
    {
        return destination.Kind switch
        {
            DestinationKind.LocalFolder
            or DestinationKind.ExternalDrive
            or DestinationKind.NetworkShare
                => ResolveLocal(destination),

            DestinationKind.S3Compatible
                => ResolveS3(destination, secrets),

            DestinationKind.DropboxAppScoped
            or DestinationKind.DropboxFullAccess
            or DestinationKind.OneDrivePersonal
            or DestinationKind.OneDriveBusiness
            or DestinationKind.GoogleDrive
                => ResolveRclone(destination, rcloneConfigPath),

            _ => throw new NotSupportedException($"Unknown destination kind: {destination.Kind}"),
        };
    }

    private static ResticEnvironment ResolveLocal(Destination d)
    {
        // Restic accepts a bare filesystem path as a repo URI. Trim a
        // trailing slash so we don't accidentally produce double-slash
        // paths when callers concat further; restic tolerates the
        // double but tools that scan log output don't.
        var path = d.PathOrSubpath ?? "";
        if (d.Kind == DestinationKind.ExternalDrive && OperatingSystem.IsWindows()
            && string.IsNullOrWhiteSpace(d.ExpectedDriveLetter) && !string.IsNullOrWhiteSpace(d.ExpectedVolumeLabel))
        {
            var mounted = VolumeDetector.Check(null, d.ExpectedVolumeLabel);
            if (mounted.Verdict != VolumeDetector.Verdict.Mounted || mounted.MountedRoot is null)
                throw new System.IO.IOException("Expected external drive is not mounted: " + d.ExpectedVolumeLabel);
            path = RebaseDrivePath(path, mounted.MountedRoot);
        }
        // Preserve a drive root: C:\ is absolute, whereas C: is relative
        // to the process's current directory on that drive.
        if (!(path.Length == 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is ('\\' or '/')))
            path = path.TrimEnd('/', '\\');
        return new ResticEnvironment(Uri: path, Env: Array.Empty<string>());
    }

    internal static string RebaseDrivePath(string original, string mountedRoot)
    {
        if (original.Length < 3 || !char.IsAsciiLetter(original[0]) || original[1] != ':' || original[2] is not ('\\' or '/'))
            throw new InvalidOperationException("A label-matched external destination needs an absolute drive path.");
        return mountedRoot.TrimEnd('\\', '/') + "\\" + original[3..].TrimStart('\\', '/');
    }

    private static ResticEnvironment ResolveS3(Destination d, SecretsStore secrets)
    {
        var endpoint = (d.S3Endpoint ?? "").Trim();
        var bucket = (d.S3Bucket ?? "").Trim();
        var subpath = NormalizeSubpath(d.PathOrSubpath);
        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(bucket))
            throw new InvalidOperationException("S3 destination is missing endpoint or bucket.");

        // Restic's S3 backend accepts either http(s):// or a bare host;
        // a bare host implies https. We emit the bare-host form so
        // restic's defaults pick up TLS verification.
        var host = StripScheme(endpoint);
        var uri = string.IsNullOrEmpty(subpath)
            ? $"s3:{host}/{bucket}"
            : $"s3:{host}/{bucket}/{subpath}";

        var env = new List<string>(capacity: 3);
        if (!string.IsNullOrEmpty(d.S3AccessKeyRef))
            env.Add($"AWS_ACCESS_KEY_ID={secrets.Get(d.S3AccessKeyRef)}");
        if (!string.IsNullOrEmpty(d.S3SecretKeyRef))
            env.Add($"AWS_SECRET_ACCESS_KEY={secrets.Get(d.S3SecretKeyRef)}");
        if (!string.IsNullOrEmpty(d.S3Region))
            env.Add($"AWS_DEFAULT_REGION={d.S3Region}");

        return new ResticEnvironment(Uri: uri, Env: env);
    }

    private static ResticEnvironment ResolveRclone(Destination d, string rcloneConfigPath)
    {
        var remote = RcloneRemoteNameFor(d);
        var subpath = NormalizeSubpath(d.PathOrSubpath);
        var uri = string.IsNullOrEmpty(subpath)
            ? $"rclone:{remote}:"
            : $"rclone:{remote}:{subpath}";

        // RCLONE_CONFIG points restic's bundled rclone driver at the
        // config file we manage. Without it rclone falls back to the
        // user's global config, which doesn't carry Backspace's
        // destinations.
        var env = new List<string>(capacity: 1)
        {
            $"RCLONE_CONFIG={rcloneConfigPath}",
        };
        return new ResticEnvironment(Uri: uri, Env: env);
    }

    /// <summary>Deterministic rclone remote name for a destination.
    /// The remote name becomes a section header in rclone.conf, so it
    /// must be stable (renaming the destination must NOT change the
    /// remote section) and a valid identifier (alphanumerics, dashes,
    /// underscores).</summary>
    public static string RcloneRemoteNameFor(Destination d)
    {
        // <kind-tag>-<8-char id prefix> packs both readability and
        // uniqueness into a single token. The id prefix is 8 hex
        // chars of the destination id (already a guid hex), so
        // collision-free across any realistic config.
        var tag = d.Kind switch
        {
            DestinationKind.DropboxAppScoped => "dropbox-app",
            DestinationKind.DropboxFullAccess => "dropbox-full",
            DestinationKind.OneDrivePersonal => "onedrive-personal",
            DestinationKind.OneDriveBusiness => "onedrive-business",
            DestinationKind.GoogleDrive => "googledrive",
            _ => "remote",
        };
        var idPrefix = string.IsNullOrEmpty(d.Id)
            ? "00000000"
            : new string(d.Id.Where(char.IsLetterOrDigit).Take(8).ToArray()).ToLowerInvariant();
        return $"{tag}-{idPrefix}";
    }

    private static string NormalizeSubpath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        return raw.Replace('\\', '/').Trim('/');
    }

    private static string StripScheme(string endpoint)
    {
        if (endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return endpoint.Substring("https://".Length);
        if (endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return endpoint.Substring("http://".Length);
        return endpoint;
    }
}

/// <summary>The restic URI for a destination + the environment variables
/// that need to be set on the process invoking restic. Backrest's
/// <see cref="BackrestRepo.Env"/> takes the <see cref="Env"/> list
/// verbatim.</summary>
public sealed record ResticEnvironment(string Uri, IReadOnlyList<string> Env);
