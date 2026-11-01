using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Pins the switch-case discipline of <see cref="EngineErrorClassifier"/>:
/// whitelist of known TOLERABLE signatures first, whitelist of known
/// INTOLERABLE signatures second, default to INTOLERABLE for anything
/// unrecognised. Each row of <see cref="ClassifyTheoryData"/> is a real
/// error string we've observed in restic / rclone / Win32 stderr; if a
/// future restic version changes the wording, this is the file that
/// has to be edited in lockstep.
/// </summary>
public class EngineErrorClassifierTests
{
    public static TheoryData<string, EngineErrorClassifier.Verdict> ClassifyTheoryData =>
        new()
        {
            // -------- TOLERABLE --------
            // Win32 / SMB layer (NetworkShare unreachable)
            { "CreateFile \\\\nas\\backups\\config: The network path was not found.", EngineErrorClassifier.Verdict.Tolerable },
            { "Stat returned error: The network name cannot be found.", EngineErrorClassifier.Verdict.Tolerable },
            { "open \\\\10.0.0.1\\share: The network is unreachable", EngineErrorClassifier.Verdict.Tolerable },
            { "Win32 error: ERROR_BAD_NETPATH", EngineErrorClassifier.Verdict.Tolerable },
            { "ERROR_NETWORK_UNREACHABLE was returned by the network stack", EngineErrorClassifier.Verdict.Tolerable },

            // rclone / Go net errors (Cloud destination unreachable)
            { "dial tcp api.dropboxapi.com:443: lookup api.dropboxapi.com: no such host", EngineErrorClassifier.Verdict.Tolerable },
            { "Get \"https://www.googleapis.com\": dial tcp: connect: no route to host", EngineErrorClassifier.Verdict.Tolerable },
            { "dial tcp 192.0.2.1:443: connect: connection refused", EngineErrorClassifier.Verdict.Tolerable },
            { "lookup graph.microsoft.com: Temporary failure in name resolution", EngineErrorClassifier.Verdict.Tolerable },
            { "Get \"https://example.com\": context deadline exceeded (Client.Timeout exceeded)", EngineErrorClassifier.Verdict.Tolerable },
            { "Post \"https://api.dropboxapi.com\": net/http: TLS handshake timeout: i/o timeout", EngineErrorClassifier.Verdict.Tolerable },

            // -------- INTOLERABLE --------
            // restic missing repo (LocalFolder moved / NetworkShare path not present)
            { "Fatal: repository does not exist", EngineErrorClassifier.Verdict.Intolerable },
            { "command failed: exit status 10", EngineErrorClassifier.Verdict.Intolerable },

            // restic wrong password
            { "command failed: exit status 12", EngineErrorClassifier.Verdict.Intolerable },
            { "wrong password or no key found", EngineErrorClassifier.Verdict.Intolerable },

            // restic locked after our retry
            { "command failed: exit status 11", EngineErrorClassifier.Verdict.Intolerable },

            // OAuth expired / revoked (cloud)
            { "401 Unauthorized: token has been expired or revoked", EngineErrorClassifier.Verdict.Intolerable },
            { "error: invalid_grant. The provided authorization grant is expired", EngineErrorClassifier.Verdict.Intolerable },

            // Cloud bucket / subfolder missing
            { "S3 returned 404 Not Found: NoSuchKey", EngineErrorClassifier.Verdict.Intolerable },
            { "AWS API error: NoSuchBucket: The specified bucket does not exist", EngineErrorClassifier.Verdict.Intolerable },
            { "Dropbox API error: directory not found at /Backspace/MyBackups", EngineErrorClassifier.Verdict.Intolerable },

            // Capacity (permanent until user frees space)
            { "write to repo: no space left on device", EngineErrorClassifier.Verdict.Intolerable },
            { "Win32 error: ERROR_DISK_FULL", EngineErrorClassifier.Verdict.Intolerable },
            // 403 stays Intolerable — server-side won't change in a minute
            { "S3 returned 403 Forbidden", EngineErrorClassifier.Verdict.Intolerable },

            // -------- TRANSIENT RETRIABLE --------
            // Defender / AV file lock surfaces as "Access is denied"
            // and is often gone by the time the cell loop retries.
            // Permission denied on Unix can be a momentary race.
            // Cell loop gives both a 60s total budget; after that,
            // the next failure escalates to Failed with the original
            // error.
            { "Access is denied. (0x80070005)", EngineErrorClassifier.Verdict.TransientRetriable },
            { "Restic failed: permission denied", EngineErrorClassifier.Verdict.TransientRetriable },
            // Reviewer's adversarial case: real restic stderr often
            // pairs the transient signal with an exit code on the
            // same line. Specific signal must beat the generic
            // exit-code regex, or the whole TransientRetriable
            // verdict is dead code in practice.
            { "Fatal: open D:/Docs/file.txt: Access is denied (exit status 1)",
              EngineErrorClassifier.Verdict.TransientRetriable },
            { "Fatal: write \\\\nas\\share\\pack: permission denied. exit status 1",
              EngineErrorClassifier.Verdict.TransientRetriable },
            // Round-4 reviewer caught: exit codes 10/11/12 are
            // documented restic failures that REQUIRE user action,
            // and must beat a transient-sounding substring on the
            // same line. Real restic stderr: "Fatal: unable to open
            // config file: Access is denied (exit status 12)" — that
            // exit 12 = wrong password, not a transient AV lock.
            { "Fatal: unable to open config file: Access is denied (exit status 12)",
              EngineErrorClassifier.Verdict.Intolerable },
            { "Fatal: open D:/repo/config: Access is denied (exit status 10)",
              EngineErrorClassifier.Verdict.Intolerable },
            { "Fatal: lock failure: exit status 11. permission denied",
              EngineErrorClassifier.Verdict.Intolerable },
            // Round-5 reviewer's hypothetical: outer wrong-password +
            // inner tolerable-sounding deadline. Reverted that fix
            // because in real engine behaviour the offline-NAS case
            // ("repo doesn't exist" + "network path not found") matters
            // more — see ORDERING NOTE in EngineErrorClassifier. The
            // hypothetical does NOT actually occur in restic stderr.
            // If it ever does, the corresponding test row goes here.

            // -------- DEFAULTS --------
            // Empty / null → Intolerable
            { "", EngineErrorClassifier.Verdict.Intolerable },
            // Wholly unrecognised text → Intolerable (safe default)
            { "Restic exploded for an entirely new reason we've never seen", EngineErrorClassifier.Verdict.Intolerable },

            // -------- REGEX ANCHORING DEFENCE --------
            // Code-review caught a footgun: substring matches on
            // "exit status 10" / "wrong password" would fire on any
            // error message containing those phrases inside a user
            // file path or unrelated text. Exit-code matches now go
            // through an anchored regex; the cases below pin the
            // anchoring. Free-form English matches stay loose (we
            // can't reliably anchor "wrong password") but at least
            // require the literal phrase.
            // 'exit status 10' inside a user file path — when this
            // case ALSO contains "permission denied", round-4 makes a
            // tradeoff: exit codes 10/11/12 are unambiguous restic
            // failure signals (real cases: repo missing, locked,
            // wrong password) that REQUIRE user action regardless of
            // other transient-sounding substrings. The cost: an
            // extremely rare degenerate case where the user has a
            // file literally named "exit status 10 from build.txt"
            // would be classified Intolerable when the underlying
            // failure was actually a transient AV lock. Acceptable
            // trade-off — incorrect Intolerable surfaces an error
            // text the user can see; incorrect Transient swallows
            // a real wrong-password case for 60s of pointless retry.
            { "Fatal: open \"D:\\notes\\exit status 10 from last build.txt\": permission denied",
              EngineErrorClassifier.Verdict.Intolerable },
            // 'exit status' WITHOUT a number after — shouldn't match
            { "Engine returned with exit status uncategorised",
              EngineErrorClassifier.Verdict.Intolerable }, // default path
            // 'exit code 11' is the alternative wording restic also uses
            { "Fatal: <op>: command failed: exit code 11",
              EngineErrorClassifier.Verdict.Intolerable },
            // exit-code 99 (anything non-{0,3}) → default Intolerable
            { "Fatal: oh no: command failed: exit status 99",
              EngineErrorClassifier.Verdict.Intolerable },
        };

    [Theory]
    [MemberData(nameof(ClassifyTheoryData))]
    public void Classify_ByErrorText_VerdictMatchesExpected(string error, EngineErrorClassifier.Verdict expected)
    {
        Assert.Equal(expected, EngineErrorClassifier.Classify(error));
    }

    [Fact]
    public void Classify_ITolerable_Exception_ShortCircuitsToTolerable()
    {
        // Even if the exception's Message wouldn't otherwise match a
        // tolerable pattern, an ITolerable marker takes precedence —
        // call sites that already pre-classified shouldn't get a
        // re-decision from the text matcher.
        var ex = new EngineUnreachableDestinationException(
            reason: "Drive offline",
            message: "Something totally unrelated to any tolerable pattern");
        Assert.Equal(EngineErrorClassifier.Verdict.Tolerable, EngineErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_NestedException_WalksInnerChain()
    {
        // The engine often wraps the raw restic stderr a few layers
        // deep before the cell loop sees it. Classification has to
        // walk the whole InnerException chain so a tolerable signal
        // doesn't get masked by a generic outer wrapper.
        var inner = new System.Exception(
            "Fatal: repository does not exist: " +
            "CreateFile \\\\nas\\backups\\config: The network path was not found.");
        var outer = new System.Exception("/v1.Backrest/Backup failed: unknown", inner);
        Assert.Equal(EngineErrorClassifier.Verdict.Tolerable, EngineErrorClassifier.Classify(outer));
    }

    [Theory]
    [InlineData("The network path was not found", "Network share unreachable")]
    [InlineData("ERROR_BAD_NETPATH", "Network share unreachable")]
    [InlineData("dial tcp: no such host", "Cloud destination unreachable (DNS)")]
    [InlineData("dial tcp 1.2.3.4:443: connect: connection refused", "Connection refused by destination")]
    [InlineData("Get \"\": context deadline exceeded", "Destination connection timed out")]
    [InlineData("totally unrecognised", "Destination unreachable")]
    public void ReasonForTolerable_PicksHumanShortLabel(string error, string expectedReason)
    {
        Assert.Equal(expectedReason, EngineErrorClassifier.ReasonForTolerable(error));
    }
}
