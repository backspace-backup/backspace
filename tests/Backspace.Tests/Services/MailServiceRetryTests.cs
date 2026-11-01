using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Pins the retry-cascade classifier inside <see cref="MailService"/>.
/// The retry loop calls <see cref="MailService.IsTransientStatus"/> on
/// the InvalidOperationException's Message to decide whether to retry
/// or fail fast. Getting the boundary wrong has bad failure modes both
/// ways:
///   • False negative (5xx classified as non-retriable): we drop the
///     failure-notification email on the first transient hiccup,
///     defeating the whole point of the retry.
///   • False positive (4xx classified as transient): we burn ~10s of
///     backoff waiting on an auth / payload error that will never
///     succeed, and the user's email status reads "retrying" longer
///     than necessary before the final non-retriable surface.
/// </summary>
public class MailServiceRetryTests
{
    [Theory]
    [InlineData("Resend HTTP 500: server busy")]
    [InlineData("Mailgun HTTP 502: bad gateway")]
    [InlineData("Resend HTTP 503: service unavailable")]
    [InlineData("Mailgun HTTP 504: gateway timeout")]
    [InlineData("Resend HTTP 599: nginx exhausted upstream connections")]
    public void IsTransientStatus_recognizes5xx(string message)
    {
        Assert.True(MailService.IsTransientStatus(message),
            $"Expected '{message}' to be classified as a transient 5xx so the retry cascade fires.");
    }

    [Theory]
    [InlineData("Resend HTTP 400: bad request")]
    [InlineData("Resend HTTP 401: invalid api key")]
    [InlineData("Mailgun HTTP 403: forbidden")]
    [InlineData("Resend HTTP 404: from address not verified")]
    [InlineData("Mailgun HTTP 422: invalid recipient")]
    public void IsTransientStatus_rejects4xx(string message)
    {
        Assert.False(MailService.IsTransientStatus(message),
            $"Expected '{message}' to be classified non-transient so the user sees the actionable error immediately.");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Some other error without HTTP code")]
    public void IsTransientStatus_rejectsEmptyOrUnstructured(string? message)
    {
        // Falls back to "treat as non-transient" — the failure path
        // outside our provider helpers shouldn't loop indefinitely.
        Assert.False(MailService.IsTransientStatus(message!));
    }

    [Fact]
    public void IsTransientStatus_caseSensitiveOnHttpToken()
    {
        // We deliberately match the literal " HTTP 5" prefix because the
        // SendViaResendAsync / SendViaMailgunAsync paths produce that
        // exact format. A case-folded match would catch "http 5xx" but
        // we'd lose the boundary check on " HTTP 5" inside an unrelated
        // English sentence. Pin the behaviour so an over-eager future
        // refactor doesn't make this lenient.
        Assert.False(MailService.IsTransientStatus("the user typed http 500 in their email"));
    }
}
