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
///   • The interactive acquirer calls the production OAuth
///     coordinator and opens the provider's consent page. It shares
///     the editor's token flow, but does not click or render the
///     destination editor; UI scenarios cover that separate boundary.
/// </summary>
public static class E2EConfig
{
    /// <summary>
    /// When true, every cloud scenario re-runs the interactive token
    /// acquisition path on every test invocation, even if a saved
    /// token already exists in <c>secrets.env</c>. Useful when you
    /// are specifically trying to exercise provider consent again,
    /// without having to manually delete the saved token first.
    ///
    /// Leave false in committed code. Flip locally, run the
    /// scenario, observe, flip back.
    /// </summary>
    public const bool AlwaysShowOAuthUx = false;

    /// <summary>
    /// Interactive acquisition is off unless the operator explicitly sets
    /// <c>BACKSPACE_TEST_INTERACTIVE=1</c>. The separate
    /// <c>BACKSPACE_E2E_NONINTERACTIVE=1</c> switch always overrides that opt-in.
    ///
    /// Interaction runs through the production OAuth coordinator,
    /// which opens the provider's consent screen. The developer
    /// completes the flow once; the loopback callback exchanges the
    /// code, and the test persists the token JSON to secrets.env
    /// for every subsequent run.
    /// </summary>
    public static bool AcquireMissingTokensInteractively => string.Equals(
        System.Environment.GetEnvironmentVariable("BACKSPACE_TEST_INTERACTIVE"), "1", System.StringComparison.Ordinal);
}
