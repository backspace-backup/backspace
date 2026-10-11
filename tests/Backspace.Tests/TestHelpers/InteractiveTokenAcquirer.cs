using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.OAuth;
using Xunit;

namespace Backspace.Tests.TestHelpers;

/// <summary>
/// Mints a fresh cloud token by driving Backspace's production
/// OAuth flow (see <see cref="OAuthFlowCoordinator"/>), then
/// persists the result to <c>secrets.env</c> so subsequent test
/// runs reuse it.
///
/// The acquirer is intentionally a thin wrapper around the same
/// coordinator <see cref="ViewModels.DestinationEditorViewModel"/>'s
/// "Connect" button drives, so the test path exercises the SAME
/// code a real user does. Differences from the production button:
///   • A test running in xUnit doesn't have a window to attach a
///     status banner to; we log progress through
///     <see cref="ITestOutputHelper"/> instead.
///   • Persistence: the production flow drops the token into
///     <see cref="Destination.OAuthTokenRef"/> via SecretsStore;
///     the test stores it in plaintext in secrets.env so it
///     survives process restarts and isn't gated by DPAPI.
///
/// Skipping rules:
///   • CI hosts (<c>BACKSPACE_E2E_NONINTERACTIVE=1</c>) skip the
///     acquisition path. No browser, no callback.
///   • <see cref="E2EConfig.AcquireMissingTokensInteractively"/>
///     can be flipped to false to opt out the same way.
///   • If <see cref="OAuthAppRegistry.DropboxClientId"/> hasn't
///     been filled in with a real Dropbox App key, the
///     coordinator returns a failure that we surface as a skip
///     reason ("upgrade to a build that ships a client_id").
/// </summary>
internal static class InteractiveTokenAcquirer
{
    /// <summary>True iff the host can plausibly complete the OAuth
    /// browser flow — the env-var opt-out isn't set and the
    /// developer didn't flip
    /// <see cref="E2EConfig.AcquireMissingTokensInteractively"/>
    /// to false.</summary>
    public static bool CanAcquireInteractively
    {
        get
        {
#pragma warning disable CS0162 // const-folded; see InteractiveTokenAcquirer review
            if (!E2EConfig.AcquireMissingTokensInteractively) return false;
#pragma warning restore CS0162
            if (string.Equals(
                    Environment.GetEnvironmentVariable("BACKSPACE_E2E_NONINTERACTIVE"),
                    "1", StringComparison.Ordinal))
                return false;
            return true;
        }
    }

    /// <summary>
    /// Run Backspace's production OAuth flow for
    /// <paramref name="provider"/> and persist the resulting token
    /// to <c>secrets.env</c>. The user's default browser opens to
    /// the provider's consent screen; on success the OAuth
    /// coordinator's loopback callback receives the code, exchanges
    /// it for a token, and we save the rclone-format JSON.
    /// </summary>
    public static async Task<CloudCredentialsLoader.Credential> AcquireAsync(
        CloudCredentialsLoader.Provider provider,
        ITestOutputHelper log,
        CancellationToken ct = default)
    {
        if (!CanAcquireInteractively)
            throw new InvalidOperationException(
                "Interactive token acquisition is disabled — either " +
                "BACKSPACE_E2E_NONINTERACTIVE is set or " +
                "E2EConfig.AcquireMissingTokensInteractively is false.");

        var kind = ProductionKindFor(provider);
        var oauthProvider = OAuthProviderFactory.For(kind, ServiceLocator.Config.Current)
            ?? throw new InvalidOperationException(
                $"No OAuth provider wired for {kind} yet. Add one in OAuthProviderFactory " +
                $"or paste a token manually into secrets.env.");

        // Mirror to both Console (live in the developer's terminal
        // — xUnit's ITestOutputHelper output is buffered until the
        // test finishes, which is no good when we need the user to
        // act NOW) and the test log (so the prompt is also in the
        // saved test report).
        var banner = new[]
        {
            "",
            "─────────────────────────────────────────────────────────────",
            $" Acquiring an OAuth token for {provider}.",
            $" Your default browser will open to {oauthProvider.AuthorizationEndpoint}.",
            " Sign in to the provider — Backspace's loopback callback",
            " receives the code automatically; you don't need to paste",
            " anything back here.",
            "─────────────────────────────────────────────────────────────",
            "",
        };
        foreach (var line in banner) { Console.WriteLine(line); log.WriteLine(line); }

        var result = await ServiceLocator.OAuthFlow.RunAsync(oauthProvider, ct).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"OAuth flow for {provider} didn't complete: {result.Message}");

        var folder = DefaultFolderFor(provider);
        var tokenKey = TokenKeyFor(provider);
        PersistKey(CloudCredentialsLoader.DefaultPath, tokenKey, result.TokenJson!);
        log.WriteLine($"[token-acquire] Saved {tokenKey} to {CloudCredentialsLoader.DefaultPath}.");

        var folderKey = FolderKeyFor(provider);
        if (!HasKey(CloudCredentialsLoader.DefaultPath, folderKey))
            PersistKey(CloudCredentialsLoader.DefaultPath, folderKey, folder);

        CloudCredentialsLoader.InvalidateCache();
        return new CloudCredentialsLoader.Credential(result.TokenJson!, folder);
    }

    private static DestinationKind ProductionKindFor(CloudCredentialsLoader.Provider p) => p switch
    {
        CloudCredentialsLoader.Provider.Dropbox     => DestinationKind.DropboxAppScoped,
        CloudCredentialsLoader.Provider.OneDrive    => DestinationKind.OneDrivePersonal,
        CloudCredentialsLoader.Provider.GoogleDrive => DestinationKind.GoogleDrive,
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    private static string TokenKeyFor(CloudCredentialsLoader.Provider p) => p switch
    {
        CloudCredentialsLoader.Provider.Dropbox     => "DROPBOX_TEST_TOKEN",
        CloudCredentialsLoader.Provider.OneDrive    => "ONEDRIVE_TEST_TOKEN",
        CloudCredentialsLoader.Provider.GoogleDrive => "GOOGLEDRIVE_TEST_TOKEN",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };
    private static string FolderKeyFor(CloudCredentialsLoader.Provider p) => p switch
    {
        CloudCredentialsLoader.Provider.Dropbox     => "DROPBOX_TEST_FOLDER",
        CloudCredentialsLoader.Provider.OneDrive    => "ONEDRIVE_TEST_FOLDER",
        CloudCredentialsLoader.Provider.GoogleDrive => "GOOGLEDRIVE_TEST_FOLDER",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };
    private static string DefaultFolderFor(CloudCredentialsLoader.Provider p) => p switch
    {
        CloudCredentialsLoader.Provider.Dropbox     => "/backspace-e2e-tests",
        CloudCredentialsLoader.Provider.OneDrive    => "/backspace-e2e-tests",
        CloudCredentialsLoader.Provider.GoogleDrive => "backspace-e2e-tests",
        _ => "backspace-e2e-tests",
    };

    private static bool HasKey(string path, string key)
    {
        if (!File.Exists(path)) return false;
        var pattern = new Regex("^" + Regex.Escape(key) + @"\s*=", RegexOptions.Multiline);
        return pattern.IsMatch(File.ReadAllText(path));
    }

    private static void PersistKey(string path, string key, string value)
    {
        // dotenv: KEY=VALUE on its own line. Existing line for the
        // same key gets replaced in place; otherwise we append.
        // Other entries are never touched, so concurrent providers'
        // re-runs are non-destructive.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = File.Exists(path)
            ? new List<string>(File.ReadAllLines(path))
            : new List<string>();

        bool replaced = false;
        for (int i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(key + "=", StringComparison.Ordinal))
            {
                lines[i] = $"{key}={value}";
                replaced = true;
                break;
            }
        }
        if (!replaced)
        {
            if (lines.Count > 0 && !string.IsNullOrEmpty(lines[^1]))
                lines.Add("");
            lines.Add($"# Acquired by InteractiveTokenAcquirer on {DateTime.UtcNow:O}");
            lines.Add($"{key}={value}");
        }
        File.WriteAllLines(path, lines);
    }
}
