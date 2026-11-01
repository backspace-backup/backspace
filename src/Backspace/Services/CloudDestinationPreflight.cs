using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>
/// List the configured cloud root's directories before starting cloud work. Rclone's
/// restic server can conceal rejected OAuth credentials behind HTTP 500, causing
/// restic to spend its whole retry window on a sign-in that cannot recover.
/// A direct, nonrecursive listing retains the actual provider explanation.
/// </summary>
internal sealed class CloudDestinationPreflight(SecretsStore secrets)
{
    /// <returns>True only when the metadata request succeeded. False means
    /// normal engine handling must decide the outcome; it is not proof of access.</returns>
    internal async Task<bool> CheckAsync(Destination destination, CancellationToken ct)
    {
        if (!RcloneConfigBuilder.NeedsRcloneRemote(destination)) return false;
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(AppPaths.RcloneBinary))
            throw new EngineBinaryMissingException("Backspace cannot find its bundled cloud connection tool. Repair the installation and try again.", AppPaths.RcloneBinary);

        var builder = new RcloneConfigBuilder(secrets);
        var path = builder.MaterialiseDestination(destination, ServiceLocator.Config?.Current?.RcloneOAuthApps);
        var remote = ResticUriResolver.Resolve(destination, secrets, path).Uri["rclone:".Length..];
        var token = destination.OAuthTokenRef is null ? "" : secrets.Get(destination.OAuthTokenRef);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await StreamingRestoreProcess.RunAsync(BuildStartInfo(AppPaths.RcloneBinary, remote, path),
            null, timeout.Token, "cloud connection check", includeStandardOutput: false).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        // A slow probe must not prevent initialising a new repository.
        // Unknown/transient failures stay with restic's
        // normal retry policy. This probe only rejects explicit auth failures
        // and recognisable network unavailability; it does not replace restic.
        if (result.Cancelled) return false;
        if (result.ExitCode == 0) return true;
        var error = FailureForDiagnostics(RedactCredentials(result.Output, token));
        if (error is not null) throw error;
        return false;
    }

    internal static ProcessStartInfo BuildStartInfo(string binary, string remote, string configPath)
    {
        var start = new ProcessStartInfo(binary);
        var separator = remote.IndexOf(':');
        if (separator <= 0) throw new ArgumentException("The cloud remote has no valid name.", nameof(remote));
        // The remote is already restricted to the app area by its provider
        // configuration. Avoid walking the repository's nested path: OneDrive
        // may need a separate slow request for every component.
        foreach (var arg in new[] { "lsf", remote[..(separator + 1)], "--dirs-only", "--max-depth", "1", "--format", "p",
            "--retries", "1", "--low-level-retries", "1", "--contimeout", "10s", "--timeout", "20s" })
            start.ArgumentList.Add(arg);
        start.Environment["RCLONE_CONFIG"] = configPath;
        return start;
    }

    internal static Exception? FailureForDiagnostics(string output)
    {
        if (EngineErrorClassifier.IsAuthenticationFailure(output)
            || output.Contains("403 Forbidden", StringComparison.OrdinalIgnoreCase)
            || output.Contains("missing_scope/", StringComparison.OrdinalIgnoreCase)
            || output.Contains("insufficient_scope", StringComparison.OrdinalIgnoreCase))
            return new EngineAuthFailedException("The cloud provider rejected this connection. Open the destination and connect your account again.\n" + output.Trim());
        if (EngineErrorClassifier.Classify(output) == EngineErrorClassifier.Verdict.Tolerable)
            return new EngineUnreachableDestinationException(EngineErrorClassifier.ReasonForTolerable(output), output.Trim());
        return null;
    }

    internal static string RedactCredentials(string text, string token)
    {
        try
        {
            using var doc = JsonDocument.Parse(token);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "access_token", "refresh_token", "id_token" })
                    if (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                        && value.GetString() is { Length: > 0 } secret)
                    {
                        text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
                        text = text.Replace(Uri.EscapeDataString(secret), "[REDACTED]", StringComparison.Ordinal);
                    }
            }
        }
        catch (JsonException) { }
        return DropboxAuthProbe.Redact(text, "");
    }
}
