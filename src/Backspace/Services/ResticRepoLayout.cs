using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services;

/// <summary>
/// Checks repository layout and, when requested, asks the bundled Restic
/// executable to open it. Opening a repository proves password access,
/// not that it is the same repository previously used by the destination.
/// </summary>
public static class ResticRepoLayout
{
    /// <summary>
    /// Required on-disk entries for a populated restic repository.
    /// <c>config</c> is the encrypted root key descriptor restic
    /// writes at init time; the four directories are the chunked
    /// blob store, keyfile store, snapshot metadata, and index files.
    /// A repo with zero snapshots still has all five.
    /// </summary>
    public static readonly string[] RequiredEntries = { "config", "data", "keys", "snapshots", "index" };

    public enum Verdict
    {
        /// <summary>Every required entry is present at the path AND
        /// (when <see cref="InspectWithDecryptionAsync"/> was used)
        /// restic successfully decrypted the config with the supplied
        /// password. The UI must still ask the user to confirm the path;
        /// this check does not establish the previous repository's identity.</summary>
        ValidRestic,

        /// <summary>Path doesn't exist (folder deleted or wrong drive).
        /// Caller should refuse the swap with a "path not found" error
        /// — the picker shouldn't have been able to navigate here.</summary>
        PathMissing,

        /// <summary>Path exists but contains no restic markers.
        /// Caller can offer "Start fresh? Old backup may still linger
        /// at the previous URI." — the swap is allowed, but the user
        /// must confirm they accept the data-divergence implication.</summary>
        EmptyOrUnrelated,

        /// <summary>Path exists and has SOME restic markers but is
        /// missing others. Could be a half-initialized repo, a partial
        /// copy, or a different on-disk shape. Refuse the swap rather
        /// than risk pointing at corrupted-looking storage; the user
        /// can investigate manually.</summary>
        Partial,

        /// <summary>Path looks like a restic repo on disk (passed the
        /// layout check) but restic could NOT decrypt the config with
        /// the password we hold for this destination. Either it's
        /// somebody else's repository, or the saved password is wrong.
        /// Refuse the swap with a "different repo / wrong password"
        /// dialog rather than silently mis-point at an unreadable
        /// destination. Only produced by
        /// <see cref="InspectWithDecryptionAsync"/>; the cheap
        /// file-presence-only <see cref="Inspect"/> path can't
        /// distinguish this from <see cref="ValidRestic"/>.</summary>
        DifferentRepoOrWrongPassword,

        /// <summary>The engine could not complete verification. Never
        /// substitute a layout-only result for a successful engine check.</summary>
        VerificationUnavailable,
    }

    /// <summary>
    /// Inspect <paramref name="path"/> and report which on-disk shape
    /// it matches. Pure file-system probe — no restic invocation
    /// needed, fast enough to run synchronously on the UI thread.
    /// Cannot establish password access or repository identity; use
    /// <see cref="InspectWithDecryptionAsync"/> to check password access.
    /// </summary>
    public static Verdict Inspect(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return Verdict.PathMissing;

        int present = 0;
        foreach (var entry in RequiredEntries)
        {
            var full = Path.Combine(path, entry);
            // config is a file, the rest are directories. Either
            // shape counts toward "present" — restic writes them
            // exactly this way.
            if (File.Exists(full) || Directory.Exists(full))
                present++;
        }
        if (present == RequiredEntries.Length) return Verdict.ValidRestic;
        if (present == 0) return Verdict.EmptyOrUnrelated;
        return Verdict.Partial;
    }

    /// <summary>
    /// Deeper probe than <see cref="Inspect"/>: first runs the
    /// file-presence check, then if it reported <c>ValidRestic</c>
    /// shells out to <c>restic cat config -r &lt;path&gt;</c> with
    /// the supplied <paramref name="password"/> to confirm restic
    /// can actually open the repo. Distinguishes:
    /// <list type="bullet">
    /// <item><c>ValidRestic</c>: layout matches and password decrypts the config; ask the user to confirm the path.</item>
    /// <item><c>DifferentRepoOrWrongPassword</c>: Restic rejected the password. Refuse the swap.</item>
    /// <item><c>VerificationUnavailable</c>: the check could not complete. Refuse the swap.</item>
    /// <item><c>PathMissing</c> / <c>EmptyOrUnrelated</c> / <c>Partial</c>: same as <see cref="Inspect"/>.</item>
    /// </list>
    /// Restic spawns are cheap and only happen once per "Locate moved
    /// repository" click, so the extra ~1-2 seconds is worth the
    /// confidence boost over the file-presence-only check.
    /// </summary>
    public static async Task<Verdict> InspectWithDecryptionAsync(
        string path, string password, string resticBinaryPath, CancellationToken ct)
    {
        var shallow = Inspect(path);
        if (shallow != Verdict.ValidRestic) return shallow;
        // Layout matches — now confirm decryption. `restic cat config`
        // reads the encrypted config blob and prints the decrypted
        // JSON to stdout. Exit 0 means we have the right password;
        // exit 12 (wrong password) or 10 (repo missing) means refuse.
        if (string.IsNullOrEmpty(resticBinaryPath) || !File.Exists(resticBinaryPath))
            return Verdict.VerificationUnavailable;
        var psi = new ProcessStartInfo(resticBinaryPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-r");
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("--no-cache");
        psi.ArgumentList.Add("cat");
        psi.ArgumentList.Add("config");
        psi.Environment["RESTIC_PASSWORD"] = password ?? "";
        // Unencrypted repos: restic 0.18+ refuses to operate without
        // --insecure-no-password even on read paths. Pass it when the
        // password is empty so a Backspace destination with
        // Encrypted=false still validates correctly.
        if (string.IsNullOrEmpty(password))
            psi.ArgumentList.Add("--insecure-no-password");
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        // 10s ceiling: restic cat config is O(1) on the repo size
        // — should be done in well under a second on a local folder.
        // Longer means something else is wrong (slow network, AV
        // scan). Don't block the UI on it.
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            proc.Start();
            // Drain both pipes while the child runs; error output must not
            // fill a pipe and stall verification.
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            using (deadlineCts.Token.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
                catch { }
            }))
            {
                await proc.WaitForExitAsync(deadlineCts.Token).ConfigureAwait(false);
            }
            await Task.WhenAll(stdout, stderr).WaitAsync(deadlineCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-side cancel (user closed the dialog) — propagate
            // up so the editor flow sees the right exception type.
            throw;
        }
        catch (OperationCanceledException)
        {
            return Verdict.VerificationUnavailable;
        }
        catch
        {
            return Verdict.VerificationUnavailable;
        }
        // Defensive: read the deadline token's state directly —
        // memory-fenced internal state, no need for volatile/
        // Interlocked. If the deadline fired but the OCE catch
        // didn't trip (proc raced to exit microseconds after Kill),
        // refuse to confidently say ValidRestic.
        if (deadlineCts.IsCancellationRequested && !ct.IsCancellationRequested)
            return Verdict.VerificationUnavailable;
        return proc.ExitCode switch
        {
            0 => Verdict.ValidRestic,
            12 => Verdict.DifferentRepoOrWrongPassword,
            _ => Verdict.VerificationUnavailable
        };
    }
}
