using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backspace.Models;
using Backspace.Services;

namespace Backspace.Tests.TestHelpers;

/// <summary>
/// Reads cloud-OAuth test credentials from the gitignored
/// <c>tests/Backspace.Tests/secrets.env</c> file so E2E scenarios that
/// drive real cloud destinations can find their tokens without
/// committing anything sensitive.
///
/// File format is the dotenv shape: <c>KEY=VALUE</c> per line,
/// <c>#</c> for comments. Values may contain '=' (OAuth tokens
/// carry base64 padding); the parser splits on the FIRST '=' only.
///
/// Expected keys per provider:
///   <code>
///   DROPBOX_TEST_TOKEN=&lt;token-from-helper-page&gt;
///   DROPBOX_TEST_FOLDER=/backspace-e2e-tests
///   ONEDRIVE_TEST_TOKEN=&lt;token-from-helper-page&gt;
///   ONEDRIVE_TEST_FOLDER=/backspace-e2e-tests
///   GOOGLEDRIVE_TEST_TOKEN=&lt;token-from-helper-page&gt;
///   GOOGLEDRIVE_TEST_FOLDER=backspace-e2e-tests
///   </code>
///
/// Tokens are minted through Backspace's production OAuth helper
/// (the same page the in-app "Connect" button opens — see
/// <c>DestinationEditorViewModel.OAuthHelperUrlFor</c>). The test
/// suite's <c>InteractiveTokenAcquirer</c> drives the same flow and
/// writes the result here, so contributors don't have to populate
/// this file by hand.
///
/// The folder is the path inside the user's cloud account where each
/// test scenario writes — every scenario cleans up after itself on
/// success so the folder doesn't accumulate noise.
///
/// Missing key = test skips with a logged reason (see <see cref="TryGet"/>).
/// Empty/whitespace value behaves the same way.
/// </summary>
public static class CloudCredentialsLoader
{
    /// <summary>A usable test credential. <see cref="Template"/>,
    /// <see cref="VaultPath"/> and <see cref="TokenRef"/> are set when it
    /// came from a saved review vault (see <see cref="VaultRoot"/>).</summary>
    public sealed record Credential(string TokenJson, string Folder,
        Destination? Template = null, string? VaultPath = null, string? TokenRef = null);

    /// <summary>Provider identifier the loader maps to env-var keys.
    /// One entry per family so the test surface stays small; the
    /// engine's family-fallback in RcloneConfigBuilder handles the
    /// AppScoped+FullAccess and Personal+Business expansions.</summary>
    public enum Provider { Dropbox, OneDrive, GoogleDrive }

    private static readonly object _envLock = new();
    private static IReadOnlyDictionary<string, string>? _env;

    private static IReadOnlyDictionary<string, string> Env
    {
        get
        {
            // Cache the parsed dotenv but allow a forced re-read after
            // the InteractiveTokenAcquirer writes a fresh entry —
            // otherwise the second cloud scenario in a run would still
            // see the stale "missing" state from the first scenario's
            // initial read.
            lock (_envLock)
            {
                return _env ??= LoadFromFile(DefaultPath);
            }
        }
    }

    /// <summary>Drop the cached secrets.env. The interactive acquirer
    /// calls this after writing so the next <see cref="TryGet"/> sees
    /// the freshly-saved token without restarting the test process.</summary>
    internal static void InvalidateCache()
    {
        lock (_envLock) _env = null;
    }

    /// <summary>Default secrets-file location. Lives next to the test
    /// project; gitignored by the repo's .gitignore so accidentally
    /// committed tokens never reach a push.</summary>
    public static string DefaultPath
    {
        get
        {
            // Walk up from the test assembly's directory to find
            // tests/Backspace.Tests/secrets.env so this works whether
            // the binary is in bin/Debug/net10.0-windows/
            // or some other CI layout.
            //
            // First pass: any directory along the walk-up chain that
            // ALREADY contains a secrets.env wins. Lets a developer
            // place the file at the repo root or in tests/ and have
            // it picked up.
            //
            // Second pass: if nothing exists yet, return a path
            // *inside* the Backspace.Tests project dir so the
            // InteractiveTokenAcquirer's first write lands somewhere
            // durable (the .csproj root) rather than in bin/Debug/
            // which gets wiped on `dotnet clean`.
            var assemblyDir = Path.GetDirectoryName(typeof(CloudCredentialsLoader).Assembly.Location) ?? "";
            var probe = new DirectoryInfo(assemblyDir);
            DirectoryInfo? projectRoot = null;
            while (probe is not null)
            {
                var candidate = Path.Combine(probe.FullName, "secrets.env");
                if (File.Exists(candidate)) return candidate;
                if (probe.Name == "Backspace.Tests")
                {
                    projectRoot = probe;
                    break;
                }
                probe = probe.Parent;
            }
            // No existing file found. If the walk hit the test
            // project root, write there; otherwise fall back to
            // the assembly directory.
            return Path.Combine(
                projectRoot?.FullName ?? assemblyDir,
                "secrets.env");
        }
    }

    /// <summary>Resolve a provider's credential, or null when the
    /// token entry is missing / blank. Tests handle null with
    /// <see cref="SkipReason"/> and an early return so the suite stays
    /// green on a machine with no tokens configured.</summary>
    public static Credential? TryGet(Provider provider)
    {
        var (tokenKey, folderKey) = KeysFor(provider);
        if (!Env.TryGetValue(tokenKey, out var token) || string.IsNullOrWhiteSpace(token))
            return TryGetFromVault(provider);
        var folder = Env.TryGetValue(folderKey, out var f) ? f : DefaultFolderFor(provider);
        return new Credential(token, folder);
    }

    /// <summary>Folder of saved review configurations, one Backspace config
    /// root per provider, each signed in to Backspace's own app folder.
    /// <c>BACKSPACE_CLOUD_TEST_VAULTS</c> overrides the default
    /// <c>audit-dashboard/private/temp/BackspaceReviewTests</c>.</summary>
    public static string? VaultRoot
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("BACKSPACE_CLOUD_TEST_VAULTS");
            if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
            for (var probe = new DirectoryInfo(Path.GetDirectoryName(DefaultPath)!); probe is not null; probe = probe.Parent)
            {
                var candidate = Path.Combine(probe.FullName, "audit-dashboard", "private", "temp", "BackspaceReviewTests");
                if (Directory.Exists(candidate)) return candidate;
            }
            return null;
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions ConfigJson = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Find the first saved review configuration with a destination
    /// for this provider whose token is present in that configuration's
    /// vault. The vault is only read here.</summary>
    private static Credential? TryGetFromVault(Provider provider)
    {
        if (!OperatingSystem.IsWindows() || VaultRoot is not { } root) return null;
        var kinds = provider switch
        {
            Provider.Dropbox     => new[] { DestinationKind.DropboxAppScoped },
            Provider.OneDrive    => new[] { DestinationKind.OneDrivePersonal },
            Provider.GoogleDrive => new[] { DestinationKind.GoogleDrive },
            _ => Array.Empty<DestinationKind>(),
        };
        foreach (var configFile in Directory.EnumerateFiles(root, "config.json", SearchOption.AllDirectories))
        {
            var configRoot = Path.GetDirectoryName(configFile)!;
            var vault = Path.Combine(configRoot, "secrets.bin");
            if (!File.Exists(vault)) continue;
            AppConfig? saved;
            try { saved = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configFile), ConfigJson); }
            catch (System.Text.Json.JsonException) { continue; }
            foreach (var template in saved?.Destinations ?? new())
            {
                if (!kinds.Contains(template.Kind) || string.IsNullOrEmpty(template.OAuthTokenRef)) continue;
                var store = new SecretsStore(new Backspace.Services.Platform.Windows.DpapiSecretsEncryption(), vault);
                if (!store.TryGet(template.OAuthTokenRef, out var token) || string.IsNullOrWhiteSpace(token)) continue;
                // A fresh subfolder beside the saved review repository, inside Backspace's app folder.
                var folder = $"backspace-review/e2e/{DateTime.UtcNow:yyyyMMdd}/{Guid.NewGuid():N}";
                return new Credential(token, folder, template, vault, template.OAuthTokenRef);
            }
        }
        return null;
    }

    /// <summary>Write a token the engine refreshed during the test back to
    /// the review vault it came from, so the saved sign-in stays current.</summary>
    public static void SaveRefreshedToken(Credential cred, Destination dest, SecretsStore testSecrets)
    {
        if (!OperatingSystem.IsWindows() || cred.VaultPath is null || cred.TokenRef is null || dest.OAuthTokenRef is null) return;
        if (!testSecrets.TryGet(dest.OAuthTokenRef, out var latest) || string.IsNullOrWhiteSpace(latest) || latest == cred.TokenJson) return;
        new SecretsStore(new Backspace.Services.Platform.Windows.DpapiSecretsEncryption(), cred.VaultPath).Set(cred.TokenRef, latest);
    }

    /// <summary>Human-readable reason a test should log when it
    /// can't run. The xUnit output makes the gap obvious without
    /// failing the run.</summary>
    public static string SkipReason(Provider provider)
    {
        var (tokenKey, _) = KeysFor(provider);
        return $"Skipped: {tokenKey} not present in {DefaultPath}. " +
               $"Run the cloud scenario from a terminal to trigger the interactive " +
               $"OAuth-helper flow (same UX as Backspace's in-app 'Connect' button), " +
               $"or set BACKSPACE_E2E_NONINTERACTIVE=1 to keep skipping.";
    }

    /// <summary>Map a cloud credential onto a Backspace
    /// <see cref="Destination"/> ready to drop into config. The test
    /// caller still owns adding the destination to ConfigStore and
    /// linking it to a backup — this just constructs the object.</summary>
    public static Destination BuildDestination(Provider provider, Credential cred, SecretsStore secrets)
    {
        var name = provider switch
        {
            Provider.Dropbox     => "e2e-dropbox",
            Provider.OneDrive    => "e2e-onedrive",
            Provider.GoogleDrive => "e2e-googledrive",
            _ => "e2e-cloud",
        };
        var kind = provider switch
        {
            Provider.Dropbox     => DestinationKind.DropboxAppScoped,
            Provider.OneDrive    => DestinationKind.OneDrivePersonal,
            Provider.GoogleDrive => DestinationKind.GoogleDrive,
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
        // App-folder sign-ins need the saved destination's scope and, for
        // OneDrive, its drive and app-folder identity.
        var template = cred.Template;
        return new Destination
        {
            Id = $"e2e-{provider}-{Guid.NewGuid():N}".ToLowerInvariant(),
            Name = name,
            Kind = template?.Kind ?? kind,
            CloudAppFolderOnly = template?.CloudAppFolderOnly ?? false,
            OneDriveDriveId = template?.OneDriveDriveId,
            OneDriveDriveType = template?.OneDriveDriveType,
            OneDriveAppFolderId = template?.OneDriveAppFolderId,
            PathOrSubpath = cred.Folder,
            OAuthTokenRef = secrets.PutNew($"e2e-{provider}", cred.TokenJson),
            Encrypted = true,
            StoragePasswordRef = secrets.PutNew($"e2e-{provider}-pw", $"e2e-{Guid.NewGuid():N}"),
        };
    }

    private static (string TokenKey, string FolderKey) KeysFor(Provider provider) => provider switch
    {
        Provider.Dropbox     => ("DROPBOX_TEST_TOKEN",     "DROPBOX_TEST_FOLDER"),
        Provider.OneDrive    => ("ONEDRIVE_TEST_TOKEN",    "ONEDRIVE_TEST_FOLDER"),
        Provider.GoogleDrive => ("GOOGLEDRIVE_TEST_TOKEN", "GOOGLEDRIVE_TEST_FOLDER"),
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    private static string DefaultFolderFor(Provider provider) => provider switch
    {
        Provider.Dropbox     => "/backspace-e2e-tests",
        Provider.OneDrive    => "/backspace-e2e-tests",
        Provider.GoogleDrive => "backspace-e2e-tests",
        _ => "backspace-e2e-tests",
    };

    /// <summary>Parse a dotenv-style file. Tolerant of UTF-8 BOM,
    /// trailing whitespace, surrounding quotes on values, and
    /// comments. Returns an empty dictionary when the file doesn't
    /// exist — the loader is a soft dependency.</summary>
    internal static IReadOnlyDictionary<string, string> LoadFromFile(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line.Substring(0, eq).Trim();
            var value = line.Substring(eq + 1).Trim();

            // Strip a single set of surrounding double-quotes — common
            // when a token JSON is wrapped to make it tool-friendly.
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value.Substring(1, value.Length - 2);

            result[key] = value;
        }
        return result;
    }
}
