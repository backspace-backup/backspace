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
/// returns. Independent destinations use isolated files; shared-file
/// callers retain unrelated sections. Newer rclone-refreshed tokens
/// are imported into <see cref="SecretsStore"/> before rendering.
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

    public async System.Threading.Tasks.Task EnsureDriveIdentityAsync(Destination destination,
        System.Threading.CancellationToken ct, ConfigStore? config = null)
    {
        OAuth.CloudFolderAccess.RequireDestination(destination);
        if (destination.Kind is not (DestinationKind.OneDrivePersonal or DestinationKind.OneDriveBusiness)
            || (!string.IsNullOrWhiteSpace(destination.OneDriveDriveId) && !string.IsNullOrWhiteSpace(destination.OneDriveDriveType)
                && !string.IsNullOrWhiteSpace(destination.OneDriveAppFolderId))) return;
        if (string.IsNullOrWhiteSpace(destination.OAuthTokenRef))
            throw new InvalidOperationException("OneDrive needs sign-in before its drive can be selected.");
        var drive = await new OAuth.OneDriveDiscovery().DiscoverAsync(_secrets.Get(destination.OAuthTokenRef), ct).ConfigureAwait(false);
        config?.UpdateQuiet(c =>
        {
            var saved = c.Destinations.FirstOrDefault(d => d.Id == destination.Id);
            if (saved is null) return;
            saved.OneDriveDriveId = drive.Id;
            saved.OneDriveDriveType = drive.DriveType;
            saved.OneDriveAppFolderId = drive.AppFolderId;
        });
        destination.OneDriveDriveId = drive.Id;
        destination.OneDriveDriveType = drive.DriveType;
        destination.OneDriveAppFolderId = drive.AppFolderId;
    }

    /// <summary>Build the canonical rclone.conf content for the given
    /// destinations and write it to <paramref name="path"/>. Returns
    /// the path for convenience so callers can chain through to
    /// the env-var assignment in one expression.</summary>
    public string Materialise(
        IEnumerable<Destination> destinations,
        IReadOnlyList<RcloneOAuthApp>? oauthApps = null,
        string? path = null)
    {
        var target = Path.GetFullPath(path ?? DefaultPath);
        var selected = destinations.Where(NeedsRcloneRemote).ToArray();
        foreach (var destination in selected) OAuth.CloudFolderAccess.RequireDestination(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(target.ToUpperInvariant())));
        using var gate = new System.Threading.Mutex(false, "Backspace.RcloneConfig." + key);
        try { gate.WaitOne(); } catch (System.Threading.AbandonedMutexException) { }
        try
        {
            var existing = File.Exists(target) ? File.ReadAllText(target, Encoding.UTF8) : "";
            var sections = ParseSections(existing);
            foreach (var destination in selected)
            {
                var remote = ResticUriResolver.RcloneRemoteNameFor(destination);
                if (sections.TryGetValue(remote, out var previous))
                    ImportRefreshedToken(destination, previous, File.GetLastWriteTimeUtc(target));
            }
            var rendered = ParseSections(Render(selected, oauthApps ?? Array.Empty<RcloneOAuthApp>()));
            foreach (var (name, section) in rendered) sections[name] = section;
            var content = "# Managed by Backspace; rclone may update refreshed tokens.\n\n" +
                string.Join("\n", sections.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value.TrimEnd() + "\n"));
            // Do not rewrite an unchanged remote while its rclone process may
            // be refreshing credentials. Separate remote files isolate A/B.
            if (content == existing) return target;
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var stream = new FileStream(temporary, options))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(content);
                // A sync client, scanner or exiting rclone process can briefly
                // deny replacement on Windows. Keep the previous complete file
                // until the atomic rename succeeds; never truncate it in place.
                for (var attempt = 0; ; attempt++)
                {
                    try { File.Move(temporary, target, overwrite: true); break; }
                    catch (Exception ex) when (OperatingSystem.IsWindows() && attempt < 20
                        && (ex is UnauthorizedAccessException
                            || ex is IOException && (ex.HResult & 0xffff) is 5 or 32 or 33))
                    {
                        System.Threading.Thread.Sleep(100);
                    }
                }
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return target;
        }
        finally { gate.ReleaseMutex(); }
    }

    /// <summary>One file per destination prevents independent rclone writers
    /// from replacing another destination's refreshed token or remote section.</summary>
    public string MaterialiseDestination(Destination destination, IReadOnlyList<RcloneOAuthApp>? oauthApps = null)
    {
        var target = Path.Combine(AppPaths.ConfigRoot, "rclone-remotes", ResticUriResolver.RcloneRemoteNameFor(destination) + ".conf");
        // Import a refreshed token from the old shared file when upgrading.
        if (!File.Exists(target) && File.Exists(DefaultPath))
        {
            var legacy = ParseSections(File.ReadAllText(DefaultPath, Encoding.UTF8));
            if (legacy.TryGetValue(ResticUriResolver.RcloneRemoteNameFor(destination), out var section))
                ImportRefreshedToken(destination, section, File.GetLastWriteTimeUtc(DefaultPath));
        }
        return Materialise(new[] { destination }, oauthApps, target);
    }

    private void ImportRefreshedToken(Destination destination, string section, DateTime writtenUtc)
    {
        if (string.IsNullOrEmpty(destination.OAuthTokenRef)) return;
        // A newly completed sign-in always wins over an older materialized file.
        if (destination.LastTokenValidatedUtc is DateTime validated && validated > writtenUtc) return;
        var tokenLine = section.Split('\n').Select(l => l.Trim()).FirstOrDefault(l =>
            l.IndexOf('=') is var equals && equals > 0 && l[..equals].Trim() == "token");
        if (tokenLine is null) return;
        var token = tokenLine[(tokenLine.IndexOf('=') + 1)..].Trim();
        if (IsNewerToken(token, _secrets.Get(destination.OAuthTokenRef))) _secrets.Set(destination.OAuthTokenRef, token);
    }

    internal static bool IsNewerToken(string candidate, string current)
    {
        try
        {
            using var next = System.Text.Json.JsonDocument.Parse(candidate);
            using var old = System.Text.Json.JsonDocument.Parse(current);
            return next.RootElement.TryGetProperty("access_token", out var access) && !string.IsNullOrWhiteSpace(access.GetString())
                && next.RootElement.TryGetProperty("expiry", out var expires) && DateTimeOffset.TryParse(expires.GetString(), out var newer)
                && (!old.RootElement.TryGetProperty("expiry", out var prior) || !DateTimeOffset.TryParse(prior.GetString(), out var older) || newer > older);
        }
        catch (System.Text.Json.JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    internal static Dictionary<string, string> ParseSections(string content)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        string? name = null;
        var text = new StringBuilder();
        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                if (name is not null) sections[name] = text.ToString();
                name = trimmed[1..^1]; text.Clear();
            }
            if (name is not null) text.AppendLine(line);
        }
        if (name is not null) sections[name] = text.ToString();
        return sections;
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
    /// family. These shipped public IDs must match the registration
    /// that issued the token, including when rclone later refreshes it.
    /// Returns null for an unregistered or unsupported provider.</summary>
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

    internal static RcloneOAuthApp? FindMatchingOAuthApp(
        IReadOnlyList<RcloneOAuthApp> apps, DestinationKind kind)
    {
        // Exact match wins so a user can register separate OAuth apps
        // for OneDrive Personal vs Business if they want; falls back
        // to the family representative so the common case (one app
        // covers AppScoped + FullAccess, one covers Personal + Business)
        // doesn't require duplicate entries.
        for (int i = 0; i < apps.Count; i++)
            if (apps[i].Provider == kind && !string.IsNullOrWhiteSpace(apps[i].ClientId))
                return apps[i];

        var familyRep = FamilyRepresentative(kind);
        if (familyRep != kind)
        {
            for (int i = 0; i < apps.Count; i++)
                if (apps[i].Provider == familyRep && !string.IsNullOrWhiteSpace(apps[i].ClientId))
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
        OAuth.CloudFolderAccess.RequireDestination(d);
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
        if (d.Kind is DestinationKind.OneDrivePersonal or DestinationKind.OneDriveBusiness)
        {
            if (string.IsNullOrWhiteSpace(d.OneDriveDriveId) || string.IsNullOrWhiteSpace(d.OneDriveAppFolderId)
                || d.OneDriveDriveType is not ("personal" or "business" or "documentLibrary"))
                throw new InvalidOperationException("OneDrive's app folder has not been verified. Connect this destination again.");
            yield return ("drive_type", d.OneDriveDriveType);
            yield return ("drive_id", d.OneDriveDriveId);
            yield return ("root_folder_id", d.OneDriveAppFolderId);
            yield return ("access_scopes", OAuth.CloudFolderAccess.OneDriveScope + " offline_access");
        }

        // rclone's default Dropbox "sync" batching holds every upload for a
        // 0.5 s idle window and then commits it in a separate round trip.
        // Restic writes few, large packs plus several tiny files one at a
        // time (lock, index, snapshot), so batching only adds latency:
        // measured in October 2026 it cost about 10 s on a backup with
        // nothing to upload. Large uploads were no slower without it.
        if (d.Kind is DestinationKind.DropboxAppScoped or DestinationKind.DropboxFullAccess)
            yield return ("batch_mode", "off");

        // Google's private app folder cannot use Drive's trash. Restic
        // removes only its own repository objects through this remote.
        if (d.Kind == DestinationKind.GoogleDrive)
        {
            yield return ("scope", "drive.appfolder");
            yield return ("root_folder_id", "appDataFolder");
            yield return ("use_trash", "false");
        }

        // Use the same registration as the Connect flow: the user's
        // override when present, otherwise Backspace's shipped ID.
        // Some desktop registrations also issue a client secret. It
        // identifies the application; it is not a user's access token.
        if (app is not null)
        {
            yield return ("client_id", app.ClientId.Trim());
            if (!string.IsNullOrEmpty(app.ClientSecretRef))
                yield return ("client_secret", resolveSecret(app.ClientSecretRef));
            else if (d.Kind == DestinationKind.GoogleDrive && app.ClientId.Trim() == OAuth.OAuthAppRegistry.GoogleDriveClientId)
                yield return ("client_secret", OAuth.OAuthAppRegistry.GoogleDriveClientSecret);
        }

        if (!string.IsNullOrEmpty(d.OAuthTokenRef))
        {
            yield return ("token", resolveSecret(d.OAuthTokenRef));
        }
    }
}
