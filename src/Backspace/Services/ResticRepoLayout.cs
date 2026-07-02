using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services;

/// <summary>
/// Cheap "does this folder LOOK like a restic repository?" probe. We
/// can't verify it's OUR repo without decrypting the encrypted config
/// (would need scrypt-from-password + master-key derivation in pure
/// C#), but we can verify it's A repo by checking for the on-disk
/// layout restic 0.17+ produces: an unencrypted <c>config</c> file
/// alongside the standard <c>data/</c>, <c>keys/</c>, <c>snapshots/</c>,
/// <c>index/</c> directories. Used by the "Locate moved repository"
/// editor flow to give the user a confidence signal before swapping
/// the destination's path.
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
        /// password. Safe to swap the destination URI without further
        /// confirmation: the chosen folder IS a real restic repo and
        /// we have the credentials to open it.</summary>
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
        /// somebody else's repo with the same layout, or it's a
        /// Duplicacy/Kopia/older-restic dir that happens to share the
        /// `config + data/ + keys/ + snapshots/ + index/` shape.
        /// Refuse the swap with a "different repo / wrong password"
        /// dialog rather than silently mis-point at an unreadable
        /// destination. Only produced by
        /// <see cref="InspectWithDecryptionAsync"/>; the cheap
        /// file-presence-only <see cref="Inspect"/> path can't
        /// distinguish this from <see cref="ValidRestic"/>.</summary>
        DifferentRepoOrWrongPassword,
    }

    /// <summary>
    /// Inspect <paramref name="path"/> and report which on-disk shape
    /// it matches. Pure file-system probe — no restic invocation
    /// needed, fast enough to run synchronously on the UI thread.
    /// Cannot distinguish a real restic repo from a Duplicacy/Kopia/
    /// older-restic folder that happens to share the layout; for that
    /// stronger check use <see cref="InspectWithDecryptionAsync"/>.
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
    /// <item><c>ValidRestic</c>: layout matches AND password decrypts the config → safe to swap silently.</item>
    /// <item><c>DifferentRepoOrWrongPassword</c>: layout matches but decryption fails → likely a Duplicacy/Kopia dir with the same shape, or someone else's restic repo. Refuse the swap.</item>
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
            return Verdict.ValidRestic; // missing tool → fall back to shallow verdict
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
            using (deadlineCts.Token.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
                catch { }
            }))
            {
                await proc.WaitForExitAsync(deadlineCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-side cancel (user closed the dialog) — propagate
            // up so the editor flow sees the right exception type.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Deadline fired (10s elapsed) — restic took too long to
            // produce an exit code. We did NOT confirm decryption, so
            // routing to ValidRestic would be a confidence lie. Treat
            // the timeout the same as a wrong-password verdict — the
            // user is sent to the "different repo or wrong password"
            // branch and has to pick again, which is worse UX than
            // ValidRestic but strictly more honest.
            return Verdict.DifferentRepoOrWrongPassword;
        }
        catch
        {
            // Spawn / kill / handle issue (restic binary missing on
            // disk despite the File.Exists check above winning the
            // race, OS denying us a child process, etc.). Fall back
            // to the shallow verdict so the user can still proceed —
            // the alternative would be refusing every "Locate moved"
            // click on a broken install.
            return Verdict.ValidRestic;
        }
        // Defensive: read the deadline token's state directly —
        // memory-fenced internal state, no need for volatile/
        // Interlocked. If the deadline fired but the OCE catch
        // didn't trip (proc raced to exit microseconds after Kill),
        // refuse to confidently say ValidRestic.
        if (deadlineCts.IsCancellationRequested && !ct.IsCancellationRequested)
            return Verdict.DifferentRepoOrWrongPassword;
        return proc.ExitCode == 0 ? Verdict.ValidRestic : Verdict.DifferentRepoOrWrongPassword;
    }
}
