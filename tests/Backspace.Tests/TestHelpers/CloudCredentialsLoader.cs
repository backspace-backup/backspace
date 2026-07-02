using System;
using System.Collections.Generic;
using System.IO;
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
    public sealed record Credential(string TokenJson, string Folder);

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
            // the binary is in bin/Debug/net10.0-windows10.0.19041.0/
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
            return null;
        var folder = Env.TryGetValue(folderKey, out var f) ? f : DefaultFolderFor(provider);
        return new Credential(token, folder);
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
        return new Destination
        {
            Id = $"e2e-{provider}-{Guid.NewGuid():N}".ToLowerInvariant(),
            Name = name,
            Kind = kind,
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
