using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Backspace.Services.OAuth;
using Xunit;

namespace Backspace.Tests.Services.OAuth;

/// <summary>
/// Pin the PKCE primitives down — a regression in the verifier or
/// challenge generation would manifest as a generic "invalid_grant"
/// from the provider, which is a miserable failure to debug. These
/// tests fail loud at build time instead.
/// </summary>
public class PkceTests
{
    [Fact]
    public void CodeVerifier_IsCryptographicallyRandom_AndUrlSafe()
    {
        // 64 random bytes → base64url → 86 chars (no padding, no
        // URL-unsafe chars). RFC 7636 § 4.1 requires the verifier to
        // be 43-128 chars from the unreserved set [A-Z a-z 0-9 - . _ ~].
        // Our base64url output sticks to [A-Z a-z 0-9 - _] which is a
        // strict subset.
        var v1 = OAuthFlowCoordinator.GenerateCodeVerifier();
        var v2 = OAuthFlowCoordinator.GenerateCodeVerifier();

        Assert.NotEqual(v1, v2); // overwhelming odds with 64 bytes of entropy
        Assert.InRange(v1.Length, 43, 128);
        Assert.Matches(@"^[A-Za-z0-9\-_]+$", v1);
        Assert.DoesNotContain('=', v1);
        Assert.DoesNotContain('+', v1);
        Assert.DoesNotContain('/', v1);
    }

    [Fact]
    public void CodeChallenge_S256_MatchesRFC7636AppendixB()
    {
        // RFC 7636 Appendix B test vector:
        //   verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"
        //   challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"
        // If we ever change the algorithm (e.g. accidentally swap
        // sha384 in), this test breaks before we ship.
        const string verifier  = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        const string expected  = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        Assert.Equal(expected, OAuthFlowCoordinator.ComputeCodeChallenge(verifier));
    }

    [Fact]
    public void CodeChallenge_RoundTripsThroughSha256()
    {
        // Cross-check that the implementation matches the spec via a
        // different code path — recompute the hash + base64url
        // independently and compare. Catches a future "optimisation"
        // that drifts away from S256.
        var verifier = OAuthFlowCoordinator.GenerateCodeVerifier();
        var observed = OAuthFlowCoordinator.ComputeCodeChallenge(verifier);

        var expected = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        Assert.Equal(expected, observed);
    }

    [Fact]
    public void RandomToken_GeneratesDistinctValuesOfExpectedShape()
    {
        // State + verifier-source quality matter: a predictable
        // state lets a malicious site forge a callback; a
        // predictable verifier neutralises PKCE.
        var a = OAuthFlowCoordinator.GenerateRandomToken(24);
        var b = OAuthFlowCoordinator.GenerateRandomToken(24);
        Assert.NotEqual(a, b);
        Assert.Matches(@"^[A-Za-z0-9\-_]+$", a);
        // 24 bytes → base64url → 32 chars.
        Assert.Equal(32, a.Length);
    }

    [Fact]
    public void Base64UrlEncode_StripsPaddingAndTranslatesUrlUnsafeChars()
    {
        // Build inputs that exercise EVERY base64-alphabet
        // translation rule:
        //   • [0xFB, 0xEF, 0xFF] standard-encodes to "++//" — must
        //     become "--__" (both '+' and '/' translated).
        //   • [0x00] standard-encodes to "AA==" — must lose its
        //     padding ("AA").
        var plusAndSlash = OAuthFlowCoordinator.Base64UrlEncode(new byte[] { 0xFB, 0xEF, 0xFF });
        Assert.DoesNotContain('+', plusAndSlash);
        Assert.DoesNotContain('/', plusAndSlash);
        Assert.DoesNotContain('=', plusAndSlash);
        Assert.Contains('-', plusAndSlash);
        Assert.Contains('_', plusAndSlash);

        var withPad = OAuthFlowCoordinator.Base64UrlEncode(new byte[] { 0x00 });
        Assert.DoesNotContain('=', withPad);
        Assert.Equal("AA", withPad);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
