using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Backspace.Tests.TestHelpers;

/// <summary>
/// Emits a fixed set of "peculiar" files under a given root — Unicode
/// names, NTFS-specific shapes, edge attributes, and a sentinel a
/// scenario can lock open during backup. Every <see cref="FixtureSize"/>
/// fixture includes this set in full so backup + restore tests always
/// exercise the same boundary conditions regardless of overall scale.
///
/// Cross-platform: NTFS-specific shapes (ADS, hidden/system attribute)
/// no-op on non-Windows. Long-path test is also Windows-only because
/// the 260-char MAX_PATH boundary doesn't exist on Linux/macOS.
///
/// Determinism: file CONTENT is RNG-seeded so byte-compare assertions
/// stay reproducible; file NAMES are fixed (not RNG-seeded) so the
/// caller can navigate to a known path for opt-in behavior (e.g. open
/// the locked-sentinel during backup).
/// </summary>
public static class SyntheticFileTreePeculiarities
{
    /// <summary>
    /// Subdirectory name used for every peculiarity file. Lifts the
    /// odd shapes out of the main synthetic tree so a scenario reading
    /// `peculiarities/...` knows it's looking at the boundary set
    /// rather than the bulk fixture content.
    /// </summary>
    public const string SubdirName = "peculiarities";

    /// <summary>
    /// Filename (under <see cref="SubdirName"/>) that scenarios can
    /// open with <c>FileShare.None</c> during backup to exercise the
    /// "user is editing this file" path. Always created; only LOCKED
    /// if the caller opts in by opening it themselves.
    /// </summary>
    public const string LockableSentinelName = "locked-during-backup.bin";

    /// <summary>
    /// Emit every peculiarity file into <paramref name="sourceRoot"/>.
    /// Idempotent: running twice on the same root yields the same
    /// bytes (any pre-existing files are overwritten). Returns the
    /// count of files written so the caller can update its summary.
    /// </summary>
    public static int Emit(string sourceRoot, int seed = 0)
    {
        var root = Path.Combine(sourceRoot, SubdirName);
        Directory.CreateDirectory(root);
        var rng = new Random(seed);
        int count = 0;

        // ---- Unicode names ---------------------------------------
        // Emoji + non-ASCII in the FILE name — restic, rclone and the
        // path canonicalisation layer must all preserve these
        // bit-exact across backup + restore. Bugs we've actually seen
        // upstream: rclone's S3 backend percent-encoding RTL marks,
        // Windows shell APIs mangling combining-form NFD into NFC.
        WriteSmallText(Path.Combine(root, "vacation_🌴_2024.txt"), rng, "emoji-in-name");                  count++;
        WriteSmallText(Path.Combine(root, "العربية_RTL_notes.txt"), rng, "rtl-arabic");                  count++;
        WriteSmallText(Path.Combine(root, "日本語_メモ.txt"), rng, "cjk-japanese");                       count++;
        WriteSmallText(Path.Combine(root, "Приветик.txt"), rng, "cyrillic");                              count++;
        // NFD (decomposed) form: 'e' + U+0301 combining acute. Some
        // call sites silently normalise to NFC on the way through
        // .NET's path APIs, then restore as NFC — a byte-compare
        // against the original NFD filename then mismatches.
        WriteSmallText(Path.Combine(root, "café_combining.txt"), rng, "nfd-combining");             count++;

        // ---- Edge attributes (cross-platform) --------------------
        File.WriteAllBytes(Path.Combine(root, "zero-bytes.bin"), Array.Empty<byte>());                    count++;
        WriteSmallText(Path.Combine(root, "Trailing space .txt"), rng, "name-with-trailing-space");       count++;
        WriteSmallText(Path.Combine(root, " leading-space.txt"), rng, "name-with-leading-space");         count++;
        WriteSmallText(Path.Combine(root, "Has spaces in the name.txt"), rng, "name-with-spaces");        count++;
        // Bracket-in-filename — exercises restic --include's
        // Go-filepath.Match glob parser. On Windows, Go's
        // path/filepath treats \ as a path separator (not an escape)
        // so restic --include can't be told to treat [ or ] as
        // literals. Backspace's RestoreFilesAsync compensates by
        // substituting each [ and ] with ? (Go's single-char
        // wildcard) and post-restore sweeping any over-matched
        // sibling files. The fixture round-trips on every platform
        // and this exercises the fallback path end-to-end.
        WriteSmallText(Path.Combine(root, "[brackets]+plus&amp.txt"), rng, "special-chars-in-name");  count++;
        WriteSmallText(Path.Combine(root, "archive.tar.gz.bz2.xz.lz4"), rng, "long-compound-extension");  count++;
        WriteSmallText(Path.Combine(root, "no-extension"), rng, "no-extension");                          count++;
        WriteSmallText(Path.Combine(root, ".hidden-dotfile"), rng, "leading-dot-dotfile");                count++;

        // Future mtime — verifier mtime comparison must round-trip.
        var futureFile = Path.Combine(root, "future-mtime.txt");
        WriteSmallText(futureFile, rng, "future-mtime-content");
        try { File.SetLastWriteTimeUtc(futureFile, DateTime.UtcNow.AddYears(20)); } catch { /* FS limit */ }
        count++;

        // Past mtime (2 Jan 1970 — safe vs the 0-epoch edge but
        // before most ntfs / ext4 / apfs tooling assumes "real" file).
        var pastFile = Path.Combine(root, "past-mtime.txt");
        WriteSmallText(pastFile, rng, "past-mtime-content");
        try { File.SetLastWriteTimeUtc(pastFile, new DateTime(1970, 1, 2, 0, 0, 0, DateTimeKind.Utc)); } catch { }
        count++;

        // ---- Locked-sentinel (always present, lockable opt-in) ---
        // Scenarios that want to exercise "user is editing this file
        // during backup" open this with FileShare.None. By default
        // it's just a normal file in the fixture so the byte-compare
        // assertions still cover it.
        var lockable = Path.Combine(root, LockableSentinelName);
        WriteSmallBinary(lockable, rng, 4096);
        count++;

        // ---- Read-only attribute (cross-platform) ----------------
        var readonlyFile = Path.Combine(root, "readonly.txt");
        WriteSmallText(readonlyFile, rng, "readonly-content");
        try { File.SetAttributes(readonlyFile, File.GetAttributes(readonlyFile) | FileAttributes.ReadOnly); } catch { }
        count++;

        // ---- Windows-only NTFS shapes ----------------------------
        if (OperatingSystem.IsWindows())
        {
            count += EmitWindowsNtfsShapes(root, rng);
            count += EmitLongPath(root, rng);
        }

        return count;
    }

    private static int EmitWindowsNtfsShapes(string root, Random rng)
    {
        int count = 0;

        // Hidden + System attributes. Restic's default behavior is to
        // back these up as ordinary files; we pin that here so a
        // future "skip hidden" toggle can't ship without a conscious
        // test update.
        var hidden = Path.Combine(root, "hidden-attr.txt");
        WriteSmallText(hidden, rng, "hidden-content");
        try { File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden); } catch { }
        count++;

        var system = Path.Combine(root, "system-attr.txt");
        WriteSmallText(system, rng, "system-content");
        try { File.SetAttributes(system, File.GetAttributes(system) | FileAttributes.System); } catch { }
        count++;

        // Alternate Data Stream — .NET supports the `path:stream`
        // syntax natively on Windows. Stream content is restored
        // bit-exact by restic when --no-strict-os is off.
        var adsHost = Path.Combine(root, "ads-host.txt");
        WriteSmallText(adsHost, rng, "ads-primary-content");
        try
        {
            using var s = File.Create(adsHost + ":hidden-stream");
            var b = Encoding.UTF8.GetBytes("ads-secondary-content\n");
            s.Write(b, 0, b.Length);
        }
        catch { /* not all FSes (FAT32 etc.) support ADS */ }
        count++;

        // Sparse-shaped content: 1 MB file of zeros. Restic's chunker
        // dedupes zero-runs to a single chunk — this exercises the
        // "many zero bytes" path the chunker special-cases. True
        // FS-sparse (FSCTL_SET_SPARSE) requires P/Invoke; the
        // bytes-on-disk shape we test here is what restic actually
        // sees after reading the file, regardless of FS sparseness.
        var sparse = Path.Combine(root, "sparse-zeros.bin");
        File.WriteAllBytes(sparse, new byte[1_048_576]);
        count++;

        return count;
    }

    private static int EmitLongPath(string root, Random rng)
    {
        // Build a path that's well under the 260-char MAX_PATH but
        // long enough to exercise canonicalisation: 8 nested dirs of
        // 24 chars each (~200 chars total before filename). Going
        // beyond MAX_PATH requires the `\\?\` prefix and that's a
        // separate test surface — pin the in-bounds case first.
        var segment = new string('a', 24);
        var cur = root;
        for (int i = 0; i < 8; i++)
        {
            cur = Path.Combine(cur, segment + i.ToString("D2"));
            Directory.CreateDirectory(cur);
        }
        var f = Path.Combine(cur, "deep-but-legal.txt");
        WriteSmallText(f, rng, "deep-path-content");
        return 1;
    }

    private static void WriteSmallText(string path, Random rng, string tag)
    {
        // 80–240 bytes of deterministic content plus a tag so an
        // assertion failure surfaces WHICH peculiarity broke.
        var sb = new StringBuilder();
        sb.AppendLine($"# tag:{tag}");
        var lines = rng.Next(2, 6);
        for (int i = 0; i < lines; i++)
        {
            sb.Append("line ").Append(i).Append(": ");
            var words = rng.Next(4, 12);
            for (int w = 0; w < words; w++) { sb.Append("word"); sb.Append(rng.Next(100)); sb.Append(' '); }
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static void WriteSmallBinary(string path, Random rng, int size)
    {
        var buf = new byte[size];
        rng.NextBytes(buf);
        File.WriteAllBytes(path, buf);
    }
}
