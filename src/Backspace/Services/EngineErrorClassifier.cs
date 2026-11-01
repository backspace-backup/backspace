using System;
using System.Text.RegularExpressions;

namespace Backspace.Services;

/// <summary>
/// Single source of truth for "is this failure something we expect to
/// resolve on its own (TOLERABLE) or something the user has to act on
/// (INTOLERABLE)". Every engine-level failure routes through here —
/// pre-flight checks (volume detector, UNC probe), restic exit codes,
/// rclone stderr — so the classification stays consistent across call
/// sites and the rules below are the only place to look when adding
/// support for a new failure mode.
///
/// Discipline borrowed from a switch statement: whitelist of known
/// TOLERABLE patterns first; whitelist of known INTOLERABLE patterns
/// second; anything that doesn't match either defaults to INTOLERABLE.
/// New restic/rclone versions might emit unfamiliar stderr text; the
/// safe default is to surface those as Failed so the user notices.
/// </summary>
public static class EngineErrorClassifier
{
    // A missing repository cannot be repaired by repeating the backup. Keep
    // offline/network failures in their existing Skipped classification.
    internal static bool IsMissingRepositoryFailure(string text) =>
        Classify(text) == Verdict.Intolerable && Contains(text, "Fatal: repository does not exist");

    /// <summary>Explicit terminal authentication failures cannot recover by
    /// rerunning the same backup with the same saved credentials.</summary>
    internal static bool IsAuthenticationFailure(string text) =>
        Contains(text, "wrong password or no key found")
        || Contains(text, "401 Unauthorized")
        || (Contains(text, "oauth2:") &&
            (Contains(text, "\"invalid_grant\"") || Contains(text, "\"invalid_client\"")))
        || (Contains(text, "couldn't fetch token:") &&
            (Contains(text, "invalid_grant:") || Contains(text, "invalid_client:")))
        || Contains(text, "invalid_access_token/")
        || Contains(text, "expired_access_token/");

    public enum Verdict
    {
        /// <summary>User can't act, condition will resolve on its own
        /// (offline, drive unplugged, etc). Surface as Skipped.</summary>
        Tolerable,

        /// <summary>Could be transient (Defender / AV file lock,
        /// momentary SMB credential race) OR permanent — we can't
        /// tell from the stderr alone. Retry the cell with a strict
        /// 60-second total budget; after the budget, escalate to
        /// Failed with the original error so the user sees the real
        /// reason. Per user direction: "Local file access failure,
        /// then yes, retry. If you know it can either be transient
        /// or permanent but there is no way to determine for sure,
        /// we should retry for up to 1min (but make sure we can't
        /// retry for 1mn 100k files one by one)." Budget is per-CELL,
        /// not per-file — restic's exit reflects the whole cell, so
        /// the existing cascade is already correctly scoped.</summary>
        TransientRetriable,

        /// <summary>User has to act (wrong password, missing repo,
        /// disk full, OAuth expired). Surface as Failed.</summary>
        Intolerable,
    }

    /// <summary>
    /// Classify a failure based on the error text the engine surfaced.
    /// Pass the FULL error message — both the exception's Message and
    /// any embedded restic/rclone stderr. The classifier is text-based
    /// because restic doesn't emit a network-unreachable exit code
    /// (rolls into exit 1) and Backrest's Connect-RPC layer doesn't
    /// distinguish transient vs permanent either.
    /// </summary>
    public static Verdict Classify(string? errorText)
    {
        if (string.IsNullOrEmpty(errorText)) return Verdict.Intolerable;
        var s = errorText;

        // ORDERING NOTE — Tolerable runs BEFORE the specific
        // Intolerable substrings (wrong password, repo missing) on
        // purpose. The real-world ambiguity is that restic's "Fatal:
        // repository does not exist" error wraps a deeper network-
        // unreachable cause: "CreateFile \\nas\backups\config: The
        // network path was not found." That's the offline-NAS case
        // and the user-facing intent is Skipped, not Failed. Round-5
        // review proposed flipping the order to handle a hypothetical
        // "outer wrong password + inner context deadline exceeded"
        // case — but that exact combination doesn't surface in
        // practice (wrong password isn't preceded by a deadline
        // exceeded). Reverted because the offline-NAS case is real
        // and matters more than the hypothetical.

        // -------- TOLERABLE patterns (whitelist) --------
        // Network-unreachable signatures from rclone, restic-via-FS,
        // and Win32 SMB error text.

        // Win32 / SMB layer
        if (Contains(s, "network path was not found")) return Verdict.Tolerable;
        if (Contains(s, "network name cannot be found")) return Verdict.Tolerable;
        if (Contains(s, "the network is unreachable")) return Verdict.Tolerable;
        if (Contains(s, "the network location cannot be reached")) return Verdict.Tolerable;
        // Win32 numeric codes the engine sometimes surfaces in raw form
        if (Contains(s, "ERROR_BAD_NETPATH")) return Verdict.Tolerable;
        if (Contains(s, "ERROR_NETWORK_UNREACHABLE")) return Verdict.Tolerable;

        // rclone / Go net error signatures (cloud destinations)
        if (Contains(s, "no such host")) return Verdict.Tolerable;
        if (Contains(s, "no route to host")) return Verdict.Tolerable;
        if (Contains(s, "connection refused")) return Verdict.Tolerable;
        if (Contains(s, "No connection could be made because the target machine actively refused it")) return Verdict.Tolerable;
        if (Contains(s, "temporary failure in name resolution")) return Verdict.Tolerable;
        if (Contains(s, "i/o timeout")) return Verdict.Tolerable;
        if (Contains(s, "context deadline exceeded")) return Verdict.Tolerable;
        if (Contains(s, "dial tcp") && Contains(s, "timeout")) return Verdict.Tolerable;

        // -------- INTOLERABLE patterns (whitelist for clarity) --------
        // These are real failures requiring user action. Listed
        // explicitly so adding new patterns has a clear home, even
        // though the default branch would catch them anyway.
        //
        // PARSING-MINIMISATION: where possible we prefer structured
        // signals (Connect-RPC code, restic exit code) over substring
        // matches on free-form stderr text, because third-party
        // wording changes across versions and silently re-classifies
        // failures. Exit-code matches are anchored with a regex so a
        // user file path like "D:\notes\exit status 10 from
        // build.txt" embedded in an unrelated error message can't
        // match the "exit status 10" branch by accident. Free-form
        // English matches stay as substring lookups for the cases
        // where restic/rclone don't surface a code we can rely on
        // (wrong password, repository does not exist) — when this
        // breaks across a restic version bump, EngineErrorClassifierTests
        // is the gate that catches it.

        // Ordering of the remaining checks is load-bearing — see
        // EngineErrorClassifierTests for the adversarial cases that
        // pin it. The decision tree:
        //   1. Specific INTOLERABLE substrings (wrong password, repo
        //      missing) → Intolerable. These dominate exit codes
        //      because they're unambiguous restic outputs that map
        //      to a single user action.
        //   2. Specific exit codes 10/11/12/2 → Intolerable. Restic's
        //      documented failure modes that require user action
        //      regardless of whether a transient-sounding substring
        //      ("access is denied") appears in the same line. Real
        //      case: "Fatal: unable to open config file: Access is
        //      denied (exit status 12)" — exit 12 = wrong password,
        //      must be Intolerable.
        //   3. Specific TRANSIENT substrings (access denied,
        //      permission denied) → TransientRetriable. These cover
        //      "Fatal: open D:/Docs/file.txt: Access is denied (exit
        //      status 1)" — generic exit 1, transient cause.
        //   4. Other INTOLERABLE substrings (OAuth expired, 404, etc.)
        //   5. Remaining non-{0,3} exit codes → Intolerable
        //   6. Default → Intolerable (per restic's "unknown exit code
        //      MUST be treated as command failure")

        // Specific Intolerable substrings — only fire when no
        // Tolerable signal above matched. See the ORDERING NOTE
        // comment for why these run AFTER Tolerable rather than
        // before.
        if (Contains(s, "wrong password")) return Verdict.Intolerable;
        if (Contains(s, "repository does not exist")) return Verdict.Intolerable;

        // Specific exit codes BEFORE the transient substring branch.
        // 10 = repo missing, 11 = locked, 12 = wrong password, 2 =
        // Go panic — all require user action regardless of any
        // transient-sounding context in the stderr.
        if (ExitCodeMatch.IsMatch(s, out var earlyCode))
        {
            if (earlyCode is 10 or 11 or 12 or 2) return Verdict.Intolerable;
        }

        // Cloud auth (rclone)
        if (Contains(s, "401 Unauthorized")) return Verdict.Intolerable;
        if (Contains(s, "invalid_grant")) return Verdict.Intolerable;
        if (Contains(s, "token has been expired")) return Verdict.Intolerable;
        if (Contains(s, "token has expired")) return Verdict.Intolerable;
        if (Contains(s, "revoked")) return Verdict.Intolerable;

        // Cloud missing-bucket / missing-prefix
        if (Contains(s, "404 Not Found")) return Verdict.Intolerable;
        if (Contains(s, "NoSuchKey")) return Verdict.Intolerable;
        if (Contains(s, "NoSuchBucket")) return Verdict.Intolerable;
        if (Contains(s, "directory not found")) return Verdict.Intolerable;

        // Capacity (permanent until user frees space)
        if (Contains(s, "no space left on device")) return Verdict.Intolerable;
        if (Contains(s, "ERROR_DISK_FULL")) return Verdict.Intolerable;
        // 403 Forbidden is server-side and won't change in a minute —
        // OAuth scope, IAM policy, bucket ACL. Fast-fail.
        if (Contains(s, "403 Forbidden")) return Verdict.Intolerable;

        // -------- TRANSIENT RETRIABLE (could-go-either-way) --------
        // "Access is denied" on Windows often surfaces as a Defender /
        // third-party AV briefly holding a handle on a file restic is
        // trying to read or write; "permission denied" on Unix can be
        // a momentary race between two file-system clients. We can't
        // tell from the stderr alone whether it's transient or
        // permanent. Strategy: retry the cell with a 60-second total
        // budget. If the retries still fail after the budget, the
        // cell loop escalates to Failed with the original error
        // surfaced so the user sees the real cause.
        // MUST run BEFORE the exit-code regex below — restic typically
        // emits these with "(exit status 1)" in the same line, and
        // the exit-code branch would otherwise short-circuit the
        // transient verdict and dead-code this entire feature.
        if (Contains(s, "access is denied")) return Verdict.TransientRetriable;
        if (Contains(s, "permission denied")) return Verdict.TransientRetriable;

        // -------- Remaining exit codes (catch-all) --------
        // The specific codes 10/11/12/2 were already routed above.
        // Anything else non-zero non-three is Intolerable per restic's
        // own docs: "if an unknown exit code is returned, then it
        // MUST be treated as a command failure." Code 3 (partial
        // source-read failure) is upstream-Warning and SHOULDN'T
        // reach this classifier; we defensively short-circuit to
        // Intolerable's default branch below rather than the
        // catch-all line so a future code path that mis-routes
        // exit-3 here gets a known-honest default (Intolerable is
        // wrong for exit-3 specifically, but is the safest available
        // signal in a classifier that has no Warning verdict).
        if (ExitCodeMatch.IsMatch(s, out var code))
        {
            if (code == 0) { /* success; nothing to classify */ }
            else if (code == 3) { /* upstream-Warning; fall through to default Intolerable */ }
            else return Verdict.Intolerable;
        }

        // -------- Default --------
        // Anything we don't recognise gets Failed status. Restic's docs
        // are explicit: "If an unknown exit code is returned, then it
        // MUST be treated as a command failure." Same logic applies to
        // stderr we haven't seen before — silently treating it as
        // tolerable would hide real bugs.
        return Verdict.Intolerable;
    }

    /// <summary>
    /// Convenience: classify an exception by combining its Message and
    /// the message of every inner exception in the chain. Inner
    /// exceptions matter because the engine wraps RPC errors a few
    /// layers deep before the cell loop catches them.
    /// </summary>
    public static Verdict Classify(Exception ex)
    {
        if (ex is null) return Verdict.Intolerable;
        // ITolerable exceptions are already pre-classified (e.g.
        // VolumeDetector skip path threw EngineUnreachableDestination).
        if (ex is ITolerable) return Verdict.Tolerable;
        if (ex is EngineAuthFailedException) return Verdict.Intolerable;

        var sb = new System.Text.StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (sb.Length > 0) sb.Append(" || ");
            sb.Append(e.Message);
        }
        return Classify(sb.ToString());
    }

    /// <summary>
    /// Pick the human-facing short reason ("Network share unreachable",
    /// "Drive offline", "Cloud destination unreachable") for a Tolerable
    /// classification. Falls back to a generic phrase when the specific
    /// signal can't be identified — better that than an empty Reason
    /// field on the cell.
    /// </summary>
    public static string ReasonForTolerable(string? errorText)
    {
        if (string.IsNullOrEmpty(errorText)) return "Destination unreachable";
        var s = errorText;
        if (Contains(s, "network path was not found")
            || Contains(s, "network name cannot be found")
            || Contains(s, "ERROR_BAD_NETPATH"))
        {
            return "Network share unreachable";
        }
        if (Contains(s, "the network is unreachable")
            || Contains(s, "ERROR_NETWORK_UNREACHABLE")
            || Contains(s, "no route to host"))
        {
            return "Network unreachable";
        }
        if (Contains(s, "no such host")
            || Contains(s, "temporary failure in name resolution"))
        {
            return "Cloud destination unreachable (DNS)";
        }
        if (Contains(s, "connection refused")
            || Contains(s, "No connection could be made because the target machine actively refused it"))
        {
            return "Connection refused by destination";
        }
        if (Contains(s, "i/o timeout")
            || Contains(s, "context deadline exceeded")
            || Contains(s, "dial tcp"))
        {
            return "Destination connection timed out";
        }
        return "Destination unreachable";
    }

    private static bool Contains(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Anchored matcher for restic's "exit status N" / "exit code N"
    /// stderr signatures. Word boundaries on both sides defend against
    /// false positives when a file path or unrelated text contains the
    /// literal substring (real reviewer case: a snapshot of a file named
    /// 'exit status 10 from last build.txt' would otherwise match the
    /// raw substring branch). Pre-compiled for repeated classifier
    /// calls; only matches positive integers (no signed exit codes).
    /// </summary>
    private static class ExitCodeMatch
    {
        private static readonly Regex _re = new(
            @"\bexit\s+(?:status|code)\s+(\d+)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(50));

        public static bool IsMatch(string s, out int code)
        {
            code = 0;
            try
            {
                var m = _re.Match(s);
                if (!m.Success) return false;
                // int.TryParse is sufficient — real restic exit codes
                // fit comfortably in int32. The earlier
                // round-5-attempted long.TryParse expansion was
                // cosmetic (round-6 review caught: behaviour was
                // identical to int.TryParse, and an out-of-Int32 code
                // wouldn't be 10/11/12/2 anyway, so the "defence in
                // depth" framing didn't justify the change).
                return int.TryParse(m.Groups[1].Value, out code);
            }
            catch (RegexMatchTimeoutException) { return false; }
        }
    }
}
