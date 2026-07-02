using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Guards the Dropbox OAuth refresh-token redaction. If the engine binary
/// echoes the token in an error message, neither the user-visible probe
/// status nor the structured log line should ever contain it verbatim.
/// </summary>
public class DropboxAuthProbeRedactionTests
{
    [Fact]
    public void Redact_replacesExactSecret()
    {
        const string secret = "sl.AbcdEfghIjklMnopQrstUvwxyz1234567890";
        var input = $"Authentication failed for token={secret} (HTTP 401)";
        var redacted = DropboxAuthProbe.Redact(input, secret);
        Assert.DoesNotContain(secret, redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public void Redact_handlesTokenShapesEvenWithoutExactSecret()
    {
        // Sometimes restic emits a derivative or differently-cased
        // chunk of the token. The pattern fallback should still scrub it.
        var input = "Bearer ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789xyz refused";
        var redacted = DropboxAuthProbe.Redact(input, secret: "");
        Assert.Contains("[REDACTED]", redacted);
        Assert.DoesNotContain("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789xyz", redacted);
    }

    [Fact]
    public void Redact_scrubsSlPrefixedDropboxRefreshTokens()
    {
        var input = "init failed: token sl.X9Y8Z7W6V5U4T3S2R1Q0P9N8M7L6K5 invalid";
        var redacted = DropboxAuthProbe.Redact(input, secret: "");
        Assert.DoesNotContain("sl.X9Y8Z7W6V5U4T3S2R1Q0P9N8M7L6K5", redacted);
    }

    [Fact]
    public void Redact_leavesSafeTextAlone()
    {
        var input = "init failed: 401 Unauthorized";
        var redacted = DropboxAuthProbe.Redact(input, secret: "any-secret-123456789");
        Assert.Equal(input, redacted);
    }

    [Fact]
    public void Redact_withShortSecretIgnoresSecretArg()
    {
        // < 8 chars is too short to safely string-replace (would clobber
        // legitimate substrings). Pattern fallback still applies.
        var input = "harmless line with the word abc in it";
        var redacted = DropboxAuthProbe.Redact(input, secret: "abc");
        Assert.Equal(input, redacted);
    }

    [Fact]
    public void BuildProbeConfig_HasDropboxTypeAndTokenLine()
    {
        // The probe rclone.conf must be the smallest valid Dropbox
        // section rclone will accept; anything missing here trips
        // "Failed to load config" before the token ever gets tested.
        var cfg = DropboxAuthProbe.BuildProbeConfig("{\"access_token\":\"AAA\",\"refresh_token\":\"BBB\"}");
        Assert.Contains($"[{DropboxAuthProbe.ProbeRemoteName}]", cfg);
        Assert.Contains("type = dropbox", cfg);
        Assert.Contains("token = {\"access_token\":\"AAA\"", cfg);
    }

    [Fact]
    public async Task HealthCheckAsync_EmptyToken_FailsFast()
    {
        var probe = new DropboxAuthProbe(new SecretsStore());
        var result = await probe.HealthCheckAsync("", CancellationToken.None);
        Assert.False(result.Healthy);
        Assert.Contains("Paste", result.Message);
    }

    [Fact]
    public async Task HealthCheckAsync_FailureSurfacesHelpfulError()
    {
        // Two environments: CI (no bundled rclone binary, install hint
        // surfaces "rclone …") and dev (binary present, rclone runs
        // against a bogus access token → "ERROR : error listing remote …"
        // from rclone itself). Either path is a legitimate "user can't
        // proceed; here's why" signal; we assert the broader contract
        // — Healthy=false with a non-empty message — rather than the
        // CI-only literal "rclone" substring.
        var probe = new DropboxAuthProbe(new SecretsStore());
        var result = await probe.HealthCheckAsync("{\"access_token\":\"AAA\"}", CancellationToken.None);
        Assert.False(result.Healthy);
        Assert.False(string.IsNullOrWhiteSpace(result.Message),
            $"Expected a non-empty failure message; got: '{result.Message}'");
    }

    [Fact]
    public void SummarizeError_PrefersErrorTaggedLine()
    {
        const string output =
            "2026-05-18 INFO: Loading config\n" +
            "2026-05-18 ERROR : Failed to authenticate: oauth2: cannot fetch token: 401\n" +
            "2026-05-18 INFO: Cleanup\n";

        var summary = DropboxAuthProbe.SummarizeError(output, secret: "", "fallback");
        Assert.Contains("oauth2", summary);
        Assert.DoesNotContain("fallback", summary);
    }
}
