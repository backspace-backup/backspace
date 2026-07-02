using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;

namespace Backspace.Tests.TestHelpers;

/// <summary>
/// Standard fixture sizes the data-shape-sensitive E2E scenarios sweep
/// over. Each size differs ONLY in scale (file count, total bytes); every
/// size always includes the full <see cref="SyntheticFileTreePeculiarities"/>
/// set so backup + restore tests exercise the same boundary conditions
/// regardless of overall scale.
///
/// Sizes chosen per the user's spec:
///   • <see cref="Smoke"/>   — 1 source, ~30 files,  &lt;20 MB. Fast
///     first-line check; runs before the larger fixtures so a trivial
///     regression surfaces seconds (not minutes) into a sweep.
///   • <see cref="Many"/>    — 3 sources, ~500 files, &lt;20 MB. Catches
///     per-file scaling bugs that the small fixture's flat shape misses
///     (rclone listing pagination, restic index churn, progress
///     accounting).
///   • <see cref="Large"/>   — 3 sources, ~50 files, ~200 MB total with
///     one ~180 MB blob. Pins large-file streaming behavior (chunker
///     window crossings, progress reporting through long stalls, restore
///     buffer sizing).
///   • <see cref="Huge"/>    — 3 sources, ~8000 files total, ~2 GB.
///     Pins scaling behavior that the smaller sizes' file counts can't
///     trigger — notably Windows' ~32 KB CreateProcess command-line
///     ceiling (each --include flag is ~40 bytes; ~800 files is enough
///     to crash). NOT run by default — opt-in via env var (see below)
///     to keep iteration loops fast.
///
/// **Scale toggle.** Set the env var <c>BACKSPACE_E2E_SCALE=huge</c>
/// (case-insensitive) before running tests to force every scenario
/// that takes a <see cref="FixtureSize"/> to use
/// <see cref="FixtureSize.Huge"/> regardless of what the test
/// attribute requests. Intended for pre-push validation — catches the
/// class of scale bugs (32 KB command line, listing pagination,
/// progress overflow) without paying their runtime cost on every
/// local iteration. Use <see cref="ResolveSize"/> from the scenarios
/// so they honour the override.
///
/// The Large blob is session-cached so a sweep that fans 8+ scenarios
/// across all sizes doesn't regenerate 180 MB worth of pseudo-random
/// bytes 8 times.
/// </summary>
public enum FixtureSize
{
    Smoke,
    Many,
    Large,
    Huge,
}

public sealed record FixtureSpec(
    FixtureSize Size,
    int SourceCount,
    int TargetFiles,
    long TargetTotalBytes,
    long LargeBlobBytes)
{
    public static FixtureSpec For(FixtureSize size) => size switch
    {
        FixtureSize.Smoke => new(size, SourceCount: 1, TargetFiles: 30,   TargetTotalBytes: 18 * 1024L * 1024L,   LargeBlobBytes: 0),
        FixtureSize.Many  => new(size, SourceCount: 3, TargetFiles: 500,  TargetTotalBytes: 18 * 1024L * 1024L,   LargeBlobBytes: 0),
        FixtureSize.Large => new(size, SourceCount: 3, TargetFiles: 50,   TargetTotalBytes: 200 * 1024L * 1024L,  LargeBlobBytes: 180 * 1024L * 1024L),
        // Huge: spread ~8000 files across 3 sources so a single source
        // crosses the ~800-file CreateProcess threshold by ~3x. ~2 GB
        // total, half of it in one large blob.
        FixtureSize.Huge  => new(size, SourceCount: 3, TargetFiles: 8000, TargetTotalBytes: 2L * 1024 * 1024 * 1024, LargeBlobBytes: 1024L * 1024 * 1024),
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };
}

/// <summary>
/// Apply the <c>BACKSPACE_E2E_SCALE</c> override if set. Scenarios that
/// take a <see cref="FixtureSize"/> from a <c>[Theory]</c> attribute
/// should funnel through this so the env var can lift every test to
/// <see cref="FixtureSize.Huge"/> for a pre-push sweep.
/// </summary>
public static class FixtureSizeResolver
{
    public const string ScaleEnvVar = "BACKSPACE_E2E_SCALE";

    public static FixtureSize ResolveSize(FixtureSize requested)
    {
        var raw = Environment.GetEnvironmentVariable(ScaleEnvVar);
        if (string.IsNullOrWhiteSpace(raw)) return requested;
        if (string.Equals(raw, "huge", StringComparison.OrdinalIgnoreCase))
            return FixtureSize.Huge;
        // Unknown override values are ignored rather than thrown so a
        // stray env var doesn't break local iteration. Surfaces via the
        // returned size matching the request.
        return requested;
    }
}

/// <summary>
/// Generates a multi-source synthetic fixture matching a
/// <see cref="FixtureSpec"/>. Returns the list of created source paths
/// (one per <see cref="FixtureSpec.SourceCount"/>) — callers pass each
/// directly into <c>backup.SourcePaths</c>.
///
/// Session cache: the Large blob is materialised ONCE per process under
/// <see cref="LargeBlobCachePath"/> and hardlink-copied into each
/// fixture's first source. A sweep over 10 scenarios × Large goes from
/// ~12 min (regenerate 10×) to ~75 s (regenerate 1×).
/// </summary>
public static class FixtureFactory
{
    /// <summary>
    /// Generate a fixture and return the per-source absolute paths.
    /// Sources are created under <paramref name="rootDir"/> named
    /// <c>source-1</c>, <c>source-2</c>, etc. The peculiarity set
    /// always lands in <c>source-1/peculiarities/</c>.
    /// </summary>
    public static string[] Generate(string rootDir, FixtureSize size, int seed)
    {
        var spec = FixtureSpec.For(size);
        var sources = new string[spec.SourceCount];

        // Per-source budget: split the total roughly evenly. Large
        // gets special handling because the 180 MB blob is the
        // dominant chunk — give the first source the blob's full
        // weight and split the remaining smaller budget across the
        // others.
        long perSourceBudget;
        long firstSourceBlobBudget = 0;
        if (spec.LargeBlobBytes > 0)
        {
            firstSourceBlobBudget = spec.LargeBlobBytes;
            var nonBlobBudget = spec.TargetTotalBytes - spec.LargeBlobBytes;
            perSourceBudget = Math.Max(1_000_000, nonBlobBudget / spec.SourceCount);
        }
        else
        {
            perSourceBudget = spec.TargetTotalBytes / spec.SourceCount;
        }

        // File-count distribution: hand-tune the per-dir count and
        // depth so the realised fixture lands near the spec's
        // TargetFiles. SyntheticFileTree's recursive Generate fills
        // the budget first; controlling the file COUNT (rather than
        // raw bytes) means tuning filesPerDir + subdirsPerDir +
        // maxDepth so the recursion produces ~TargetFiles/sources
        // files per source before exhausting the byte budget. The
        // formulas below were validated empirically against the
        // synthetic generator's emit shape.
        int filesPerDir, subdirsPerDir, maxDepth;
        switch (size)
        {
            case FixtureSize.Smoke:
                filesPerDir = 10; subdirsPerDir = 2; maxDepth = 2;
                break;
            case FixtureSize.Many:
                filesPerDir = 14; subdirsPerDir = 3; maxDepth = 4;
                break;
            case FixtureSize.Large:
                filesPerDir = 8;  subdirsPerDir = 2; maxDepth = 2;
                break;
            case FixtureSize.Huge:
                // Wide-and-shallow shape: subdirsPerDir × maxDepth keeps
                // the tree from exploding into millions of leaves while
                // filesPerDir lifts the per-source file count past the
                // 800-file CreateProcess threshold.
                filesPerDir = 40; subdirsPerDir = 3; maxDepth = 4;
                break;
            default:
                filesPerDir = 8;  subdirsPerDir = 3; maxDepth = 4;
                break;
        }

        for (int i = 0; i < spec.SourceCount; i++)
        {
            var src = Path.Combine(rootDir, $"source-{i + 1}");
            Directory.CreateDirectory(src);
            sources[i] = src;

            SyntheticFileTree.Generate(
                rootPath: src,
                seed: seed + i,
                targetBytes: perSourceBudget,
                maxDepth: maxDepth,
                filesPerDir: filesPerDir,
                subdirsPerDir: subdirsPerDir);

            // Peculiarity set lands in source-1 only — duplicating
            // across every source would inflate counts and add no
            // coverage (the same shapes either round-trip or don't).
            // Bisect knob: BACKSPACE_FIXTURE_PECULIARITIES env var
            // can disable peculiarity emission temporarily (used
            // during the F-4 root-cause bisect to isolate which
            // peculiarity blows up restic restore on Windows).
            if (i == 0
                && !string.Equals(
                    Environment.GetEnvironmentVariable("BACKSPACE_FIXTURE_PECULIARITIES"),
                    "0", StringComparison.Ordinal))
            {
                SyntheticFileTreePeculiarities.Emit(src, seed: seed + 1000);
            }
        }

        // Materialise the Large blob into source-1 (separate from
        // the recursive synthetic tree above so the blob can be
        // hardlink-shared with the session cache instead of being
        // regenerated per fixture).
        if (firstSourceBlobBudget > 0)
        {
            var blobPath = Path.Combine(sources[0], "large-blob.bin");
            CopyOrLinkLargeBlob(blobPath, firstSourceBlobBudget, seed);
        }

        return sources;
    }

    private static readonly object _largeBlobLock = new();
    private static string? _cachedBlobPath;
    private static long _cachedBlobSize;
    private static int _cachedBlobSeed;

    private static void CopyOrLinkLargeBlob(string destPath, long bytes, int seed)
    {
        string source;
        lock (_largeBlobLock)
        {
            // Reuse the session-cached blob if size + seed match;
            // otherwise regenerate. (A seed mismatch would mean
            // determinism between scenarios depends on the same
            // seed, which it does — the caller seeds the whole
            // fixture from one int.)
            if (_cachedBlobPath is null || !File.Exists(_cachedBlobPath)
                || _cachedBlobSize != bytes || _cachedBlobSeed != seed)
            {
                var cacheDir = Path.Combine(Path.GetTempPath(), "backspace-tests-largeblob");
                Directory.CreateDirectory(cacheDir);
                _cachedBlobPath = Path.Combine(cacheDir, $"blob-{bytes}-{seed}.bin");
                _cachedBlobSize = bytes;
                _cachedBlobSeed = seed;
                if (!File.Exists(_cachedBlobPath) || new FileInfo(_cachedBlobPath).Length != bytes)
                {
                    WriteLargeBlob(_cachedBlobPath, bytes, seed);
                }
            }
            source = _cachedBlobPath;
        }

        // Plain copy (no hardlink/symlink): symlinks on Windows
        // require elevation or developer mode, hardlinks require
        // P/Invoke against CreateHardLinkW + same-volume layout
        // that the temp/test workspace pair doesn't reliably share.
        // At ~500 MB/s SSD writes the 180 MB copy is ~360 ms per
        // scenario — under 8 s total over a 21-scenario matrix
        // sweep, which is well below the noise floor of the
        // restic-spawn portion of each scenario.
        File.Copy(source, destPath, overwrite: true);
    }

    private static void WriteLargeBlob(string path, long bytes, int seed)
    {
        // RNG-filled (not zero) so restic's chunker doesn't reduce
        // the whole blob to a single dedup-of-zero chunk and skip
        // exercising large-write streaming.
        const int bufSize = 1024 * 1024;
        var buf = new byte[bufSize];
        var rng = new Random(seed);
        using var stream = File.Create(path);
        long remaining = bytes;
        while (remaining > 0)
        {
            rng.NextBytes(buf);
            var chunk = (int)Math.Min(remaining, bufSize);
            stream.Write(buf, 0, chunk);
            remaining -= chunk;
        }
    }

    /// <summary>For tests / debugging that want to clear the cache.</summary>
    public static void ResetLargeBlobCache()
    {
        lock (_largeBlobLock)
        {
            _cachedBlobPath = null;
        }
    }
}
