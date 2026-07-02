namespace Backspace.Tests;

/// <summary>
/// Developer-tunable settings for the end-to-end cloud test suite.
/// Lives at the test project root (not under TestHelpers/) so it's
/// the first file a contributor lands on when they ask "how do I
/// make the cloud tests run on my box?".
///
/// All values are <c>const</c> on purpose. They're meant to be
/// edited locally (then reverted before pushing) — like a tweakable
/// header file rather than runtime configuration. If you find
/// yourself wanting to flip these from the command line, prefer an
/// env var (e.g. <c>BACKSPACE_E2E_SKIP_REAL</c>) so the change
/// doesn't show up in <c>git status</c> and accidentally get
/// committed.
///
/// Token storage shape:
///   • Persisted tokens live in <c>tests/Backspace.Tests/secrets.env</c>
///     (gitignored). Format is documented on
///     <see cref="TestHelpers.CloudCredentialsLoader"/>.
///   • The interactive acquirer uses <c>rclone config create</c>
///     under the hood, which is exactly what a real Backspace user
///     does when they set up a cloud destination. Testing the
///     acquisition flow IS testing the production onboarding UX.
/// </summary>
public static class E2EConfig
{
    /// <summary>
    /// When true, every cloud scenario re-runs the interactive token
    /// acquisition path on every test invocation, even if a saved
    /// token already exists in <c>secrets.env</c>. Useful when you
    /// are specifically trying to exercise the auth screens — e.g.
    /// "did we just break the Dropbox connect button styling?" —
    /// without having to manually delete the saved token first.
    ///
    /// Leave false in committed code. Flip locally, run the
    /// scenario, observe, flip back.
    /// </summary>
    public const bool AlwaysShowOAuthUx = false;

    /// <summary>
    /// When true (default), the cloud scenarios will attempt to
    /// acquire a missing token interactively rather than skipping.
    /// Set to false (or set the <c>BACKSPACE_E2E_NONINTERACTIVE=1</c>
    /// env var) on CI hosts or any context where no human is around
    /// to complete the rclone OAuth browser dance.
    ///
    /// Interaction runs through <c>rclone config create</c>, which
    /// opens a browser to the provider's consent screen. The
    /// developer completes the flow once, rclone writes the remote,
    /// the test reads the JSON back and persists it to secrets.env
    /// for every subsequent run.
    /// </summary>
    public const bool AcquireMissingTokensInteractively = true;
}
