using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>
/// Materialises an <c>rclone.conf</c> file from the user's cloud
/// destinations. Restic's rclone backend reads this file (via
/// <c>RCLONE_CONFIG</c>, set by <see cref="ResticUriResolver"/>) when
/// it shells out to rclone for a Dropbox / OneDrive / Google Drive
/// repo URI.
///
/// One <c>[section]</c> per cloud destination, keyed by the same
/// remote name <see cref="ResticUriResolver.RcloneRemoteNameFor"/>
/// returns. Existing files at the target path are overwritten —
/// this builder owns the file, treat the OAuth tokens stored in
/// <see cref="SecretsStore"/> as the source of truth.
///
/// The output is plain INI with one key=value per line, which is the
/// canonical rclone.conf format. We don't use rclone's
/// <c>config_encryption</c> feature; the OAuth refresh tokens carried
/// here are already DPAPI-protected in SecretsStore until the moment
/// we materialise this file, and the file lives under our config
/// root (which has user-private permissions on every supported
/// platform).
/// </summary>
public sealed class RcloneConfigBuilder
{
    private readonly SecretsStore _secrets;

    public RcloneConfigBuilder(SecretsStore secrets) => _secrets = secrets;

    /// <summary>Default location for the materialised config.</summary>
    public static string DefaultPath => Path.Combine(AppPaths.ConfigRoot, "rclone.conf");

    /// <summary>Build the canonical rclone.conf content for the given
    /// destinations and write it to <paramref name="path"/>. Returns
    /// the path for convenience so callers can chain through to
    /// the env-var assignment in one expression.</summary>
    public string Materialise(
        IEnumerable<Destination> destinations,
        IReadOnlyList<RcloneOAuthApp>? oauthApps = null,
        string? path = null)
    {
        var target = path ?? DefaultPath;
        // Path.GetDirectoryName returns null for a bare filename
        // ("rclone.conf") or for path-roots ("C:\\"). Both are weird
        // inputs for this method but a null-forgive-and-pass to
        // CreateDirectory would NRE on input we could just sanely
        // ignore (the file lives in the current working directory).
        var targetDir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(targetDir))
            Directory.CreateDirectory(targetDir);

        var content = Render(destinations, oauthApps ?? Array.Empty<RcloneOAuthApp>());
        // Atomic write (tmp + Replace/Move) so a crash mid-write or
        // a concurrent backup run never leaves rclone.conf truncated
        // mid-INI-section — restic would shell out to rclone next
        // and fail with a cryptic "unexpected EOF" the user can't
        // act on.
        //
        // On Unix the file holds OAuth refresh tokens + optional
        // client_secret values; create it with 0600 atomically (via
        // UnixCreateMode) so the bits are owner-only from the moment
        // they hit disk. Without this, the default umask (typically
        // 022 → 0644) leaves the secrets briefly world-readable.
        var tmp = target + ".tmp";
        // Pre-delete any stale .tmp from a previous crashed write.
        // FileMode.CreateNew (vs Create) requires the file NOT exist,
        // which is what guarantees UnixCreateMode actually applies —
        // FileMode.Create on an existing file truncates but PRESERVES
        // the prior mode bits, so an inherited 0644 from a previous
        // build (or any external dropped file at that path) would
        // leak OAuth refresh tokens at world-readable perms for the
        // brief window before File.Replace renames over the target.
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort; CreateNew will throw below if it survives */ }
        var fileOpts = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            fileOpts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(tmp, fileOpts))
        using (var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            sw.Write(content);
        // File.Replace preserves the DESTINATION's mode bits, not the
        // source's, so on Unix the rename can re-introduce a 0644
        // mode inherited from a prior pre-fix build. Force the target
        // mode after the rename lands.
        try
        {
            if (File.Exists(target)) File.Replace(tmp, target, destinationBackupFileName: null);
            else File.Move(tmp, target);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
        return target;
    }

    /// <summary>Pure rendering — accepts destinations + the per-provider
    /// OAuth-app overrides + a token-lookup callback, returns the file
    /// content. Split out so unit tests can verify the exact INI shape
    /// without touching the filesystem or a real SecretsStore.</summary>
    public string Render(IEnumerable<Destination> destinations, IReadOnlyList<RcloneOAuthApp> oauthApps)
        => Render(destinations, oauthApps, ref_ => _secrets.Get(ref_));

    internal static string Render(
        IEnumerable<Destination> destinations,
        IReadOnlyList<RcloneOAuthApp> oauthApps,
        Func<string, string> resolveSecret)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Generated by Backspace. Do not edit by hand — your changes will be overwritten");
        sb.AppendLine("# on the next backup. Remove a destination from the Backspace GUI instead.");
        sb.AppendLine();

        foreach (var d in destinations.Where(NeedsRcloneRemote).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            var remote = ResticUriResolver.RcloneRemoteNameFor(d);
            sb.AppendLine($"[{remote}]");
            // Per-user override wins; otherwise fall back to
            // Backspace's official app key from OAuthAppRegistry so
            // tokens minted by the in-app OAuth flow can be
            // refreshed by rclone later (refresh requests must
            // carry the same client_id that issued the token, or
            // the provider rejects with "token doesn't belong to
            // this app").
            var app = FindMatchingOAuthApp(oauthApps, d.Kind)
                      ?? DefaultOAuthAppFromRegistry(d.Kind);
            foreach (var (k, v) in BuildSectionEntries(d, app, resolveSecret))
                sb.AppendLine($"{k} = {v}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Synthesise an <see cref="RcloneOAuthApp"/> from
    /// <see cref="OAuth.OAuthAppRegistry"/> for the destination
    /// family. Returns null when the registry's slot is still a
    /// placeholder (e.g. OneDrive / GoogleDrive before their apps
    /// are registered) — in that case rclone falls back to its
    /// built-in default client_id and tokens minted by the
    /// in-app flow can't refresh. The OAuth flow itself refuses to
    /// run with a placeholder client_id (see
    /// <see cref="OAuth.OAuthFlowCoordinator.RunAsync"/>), so this
    /// only kicks in when the user pasted a token by hand.</summary>
    private static RcloneOAuthApp? DefaultOAuthAppFromRegistry(DestinationKind kind)
    {
        var family = FamilyRepresentative(kind);
        var clientId = family switch
        {
            DestinationKind.DropboxAppScoped  => OAuth.OAuthAppRegistry.DropboxClientId,
            DestinationKind.OneDrivePersonal  => OAuth.OAuthAppRegistry.OneDriveClientId,
            DestinationKind.GoogleDrive       => OAuth.OAuthAppRegistry.GoogleDriveClientId,
            _ => "",
        };
        if (string.IsNullOrEmpty(clientId)
            || clientId.StartsWith("REPLACE_", StringComparison.Ordinal))
            return null;
        return new RcloneOAuthApp { Provider = family, ClientId = clientId };
    }

    private static RcloneOAuthApp? FindMatchingOAuthApp(
        IReadOnlyList<RcloneOAuthApp> apps, DestinationKind kind)
    {
        // Exact match wins so a user can register separate OAuth apps
        // for OneDrive Personal vs Business if they want; falls back
        // to the family representative so the common case (one app
        // covers AppScoped + FullAccess, one covers Personal + Business)
        // doesn't require duplicate entries.
        for (int i = 0; i < apps.Count; i++)
            if (apps[i].Provider == kind && !string.IsNullOrEmpty(apps[i].ClientId))
                return apps[i];

        var familyRep = FamilyRepresentative(kind);
        if (familyRep != kind)
        {
            for (int i = 0; i < apps.Count; i++)
                if (apps[i].Provider == familyRep && !string.IsNullOrEmpty(apps[i].ClientId))
                    return apps[i];
        }
        return null;
    }

    /// <summary>The "canonical" DestinationKind for a provider family.
    /// Settings UI writes one entry per family; lookups fall back to
    /// the representative when a sibling-kind doesn't have its own
    /// override.</summary>
    public static DestinationKind FamilyRepresentative(DestinationKind kind) => kind switch
    {
        DestinationKind.DropboxAppScoped or DestinationKind.DropboxFullAccess
            => DestinationKind.DropboxAppScoped,
        DestinationKind.OneDrivePersonal or DestinationKind.OneDriveBusiness
            => DestinationKind.OneDrivePersonal,
        _ => kind,
    };

    /// <summary>Which destination kinds the rclone driver handles. S3
    /// is excluded: restic talks to S3 directly, no rclone hop.</summary>
    public static bool NeedsRcloneRemote(Destination d) => d.Kind is
        DestinationKind.DropboxAppScoped
        or DestinationKind.DropboxFullAccess
        or DestinationKind.OneDrivePersonal
        or DestinationKind.OneDriveBusiness
        or DestinationKind.GoogleDrive;

    private static IEnumerable<(string Key, string Value)> BuildSectionEntries(
        Destination d, RcloneOAuthApp? app, Func<string, string> resolveSecret)
    {
        // rclone expects a `type = <provider>` plus a `token = <JSON>`
        // with the OAuth payload. We use the SAME JSON-blob shape
        // rclone itself writes during interactive `rclone config`, so
        // the user's existing tokens drop in unchanged. The token JSON
        // is opaque to Backspace — we treat it as a single string we
        // shuttle from SecretsStore into the config and back.
        yield return ("type", d.Kind switch
        {
            DestinationKind.DropboxAppScoped => "dropbox",
            DestinationKind.DropboxFullAccess => "dropbox",
            DestinationKind.OneDrivePersonal => "onedrive",
            DestinationKind.OneDriveBusiness => "onedrive",
            DestinationKind.GoogleDrive => "drive",
            _ => throw new InvalidOperationException($"NeedsRcloneRemote should have filtered {d.Kind}"),
        });

        // OneDrive Business needs a `drive_type` discriminator; without
        // it rclone defaults to personal and the user sees a
        // /personal/Documents URL where /sites/... was expected.
        if (d.Kind == DestinationKind.OneDriveBusiness)
        {
            yield return ("drive_type", "business");
        }

        // Google Drive's `drive` provider accepts a scope hint; the
        // user's app-folder vs full-drive choice doesn't have a
        // representation in our schema yet — default to the broader
        // `drive` scope which matches what the OAuth flow grants
        // (drive.file scope would only see files the app itself
        // created, which is wrong for restoring a user's snapshots
        // back to their existing folders).
        if (d.Kind == DestinationKind.GoogleDrive)
        {
            yield return ("scope", "drive");
        }

        // Custom OAuth app credentials win over rclone's built-in
        // shared defaults. Emit client_id only when the user has
        // registered their own app for this provider; otherwise rclone
        // falls back to its baked-in client which works but shares a
        // quota pool with every other rclone user. client_secret is
        // optional — public/installed-app OAuth registrations don't
        // mint one.
        if (app is not null)
        {
            yield return ("client_id", app.ClientId);
            if (!string.IsNullOrEmpty(app.ClientSecretRef))
                yield return ("client_secret", resolveSecret(app.ClientSecretRef));
        }

        if (!string.IsNullOrEmpty(d.OAuthTokenRef))
        {
            yield return ("token", resolveSecret(d.OAuthTokenRef));
        }
    }
}
