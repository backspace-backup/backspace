using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Backspace.Services.Platform;

namespace Backspace.Services;

/// <summary>
/// Single source of truth for where we store things on disk.
///
/// Backspace is portable: everything the app creates lives under
/// <c>&lt;exe-dir&gt;/Backspace.config/</c> so a user can back up that single
/// folder (or drop it onto a new machine alongside the exe) and be back in
/// business. The only machine-bound files are the secrets vault
/// (<c>secrets.bin</c>) and, on Unix, its keyfile — neither can travel
/// between machines without the user re-entering destination secrets.
/// Backup <em>definitions</em> (config.json) travel losslessly.
///
/// <para>
/// Dev-time layout: when running under <c>dotnet run</c>,
/// <see cref="ExeDirectory"/> resolves to <c>bin/Debug/&lt;tfm&gt;/</c> — config
/// sits there during development, which keeps local-user state out of
/// the user's home directory but means a <c>dotnet clean</c> would wipe
/// it. Published single-file binaries produce a clean layout.
/// </para>
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "Backspace.config";

    /// <summary>
    /// Directory containing the app executable. Uses Environment.ProcessPath
    /// so this resolves correctly for both a JIT'd DLL under `dotnet run` and
    /// a published single-file binary.
    /// </summary>
    public static string ExeDirectory
    {
        get
        {
            var procPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(procPath))
            {
                var dir = Path.GetDirectoryName(procPath);
                if (!string.IsNullOrEmpty(dir)) return dir;
            }
            // Fallback (design-time): the entry assembly location.
            var asmLoc = Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(asmLoc))
            {
                var dir = Path.GetDirectoryName(asmLoc);
                if (!string.IsNullOrEmpty(dir)) return dir;
            }
            return AppContext.BaseDirectory;
        }
    }

    /// <summary>
    /// Overridable at process start via the BACKSPACE_CONFIG_ROOT environment
    /// variable. Production paths through <see cref="ExeDirectory"/>; the
    /// override exists so the test harness can give each test its own isolated
    /// config directory (under TMPDIR) without poking the user's real state.
    /// </summary>
    public static string ConfigRoot
    {
        get
        {
            var overrideRoot = Environment.GetEnvironmentVariable("BACKSPACE_CONFIG_ROOT");
            if (!string.IsNullOrWhiteSpace(overrideRoot)) return overrideRoot;
            return Path.Combine(ExeDirectory, AppFolderName);
        }
    }

    /// <summary>
    /// Everything lives under ConfigRoot now (portable). Kept as a distinct
    /// property name so the logs/repos callers don't need to care that we
    /// collapsed the old %APPDATA% / %LOCALAPPDATA% split.
    /// </summary>
    public static string LocalRoot => ConfigRoot;

    public static string ConfigFile  => Path.Combine(ConfigRoot, "config.json");
    public static string SecretsFile => Path.Combine(ConfigRoot, "secrets.bin");

    /// <summary>
    /// Paths to the Backrest sidecar binaries. Match the names the
    /// FetchBackrest.targets script produces under src/Backspace/Assets/
    /// at build time. The sibling-to-exe layout is what
    /// <see cref="BackrestSidecar"/> uses at runtime.
    /// </summary>
    public static string BackrestBinary => Path.Combine(ExeDirectory, "backrest" + (PlatformInfo.IsWindows ? ".exe" : ""));
    public static string ResticBinary => Path.Combine(ExeDirectory, "restic" + (PlatformInfo.IsWindows ? ".exe" : ""));
    public static string RcloneBinary => Path.Combine(ExeDirectory, "rclone" + (PlatformInfo.IsWindows ? ".exe" : ""));

    /// <summary>Per-installation data directory the Backrest daemon
    /// uses for its sqlite oplog and per-operation log store. Lives
    /// inside ConfigRoot so portable installs carry the daemon's state
    /// with them.</summary>
    public static string BackrestDataDir => Path.Combine(ConfigRoot, "backrest");

    public static string LogsRoot  => Path.Combine(ConfigRoot, "logs");
    public static string ReposRoot => Path.Combine(ConfigRoot, "repos");

    /// <summary>
    /// Application-level logs (Serilog — structured, daily-rolling, the
    /// human-scale diagnostic trail). Distinct from per-backup-run logs
    /// under <see cref="LogDirForBackup"/>, which capture raw engine
    /// stdout (machine-scale, can be hundreds of MB per run).
    /// </summary>
    public static string AppLogsDir => Path.Combine(LogsRoot, "app");

    /// <summary>Root of raw per-backup-run transcripts emitted by the
    /// engine (restic via the Backrest sidecar). One folder per
    /// backup, one file per run.</summary>
    public static string EngineLogsRoot => Path.Combine(LogsRoot, "engine");

    public static string LogDirForBackup(string backupName) =>
        Path.Combine(EngineLogsRoot, Sanitize(backupName));

    /// <summary>
    /// Per-backup repo root. Each source path of the backup gets its own
    /// child under here (see <see cref="RepoDirForBackupSource"/>) — we
    /// keep them grouped so deleting a backup cleans up all its sources
    /// in one rmdir. Single-source backups still get a single subdir
    /// named for the source slug; there's no special-case "flat" layout.
    /// </summary>
    public static string RepoDirForBackup(string backupName) =>
        Path.Combine(ReposRoot, Sanitize(backupName));

    /// <summary>
    /// Per-(backup, source) repo dir. <paramref name="sourcePath"/> is
    /// the absolute source path; we turn it into a filesystem-safe slug
    /// so the repo path is stable (backup runs won't move because the
    /// slug is derived deterministically from the source path).
    /// </summary>
    public static string RepoDirForBackupSource(string backupName, string sourcePath) =>
        Path.Combine(RepoDirForBackup(backupName), SourceSlug(sourcePath));

    /// <summary>
    /// Deterministic filesystem-safe slug for a source path. Keeps a
    /// best-effort representation of the path tail so users browsing
    /// <c>&lt;config-root&gt;/repos/</c> can tell which source is which
    /// without decoding a hash. Falls back to a short SHA-derived suffix
    /// if the sanitized form collapses to an empty string (e.g. unnamed
    /// UNC shares, or a Unix path that consisted only of slashes).
    /// </summary>
    public static string SourceSlug(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return "_";
        var trimmed = sourcePath.TrimEnd('\\', '/');
        // Drive-letter colon (Windows: "C:") and Unix root-marker — both
        // collapse to underscore so the slug stays filename-safe.
        var withoutColon = trimmed.Replace(":", "_");
        var sanitized = Sanitize(withoutColon.Replace('\\', '_').Replace('/', '_'));
        if (string.IsNullOrWhiteSpace(sanitized) || sanitized == "_")
        {
            return "src_" + ShortHash(sourcePath);
        }

        // Deep paths can blow past Windows MAX_PATH when combined with
        // the repos\<name>\<slug>\.duplicacy\ prefix. Cap at ~50 chars.
        // Naive truncation collides when two sources share a long common
        // prefix (think two subfolders under
        // `C:\Users\me\AppData\Local\Temp\backspace-…-{guid}\`) — so we
        // always append a short content hash to disambiguate, even when
        // the sanitized form fits inside the cap. Costs 9 characters;
        // pays for itself the first time a user has two `Documents` in
        // different branches.
        const int MaxHeadLen = 44;
        var head = sanitized.Length <= MaxHeadLen
            ? sanitized
            : sanitized.Substring(sanitized.Length - MaxHeadLen); // prefer the *tail* — leaf folder names are more informative
        return head + "_" + ShortHash(sourcePath);
    }

    private static string ShortHash(string s)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).Substring(0, 8).ToLowerInvariant();
    }

    public static void EnsureAll()
    {
        Directory.CreateDirectory(ConfigRoot);
        Directory.CreateDirectory(LogsRoot);
        Directory.CreateDirectory(ReposRoot);
        Directory.CreateDirectory(AppLogsDir);
        Directory.CreateDirectory(EngineLogsRoot);
        // Owner-only on Unix. ConfigRoot wraps secrets.bin,
        // secrets-key.bin, rclone.conf (with OAuth refresh tokens),
        // and Backrest's data dir (which stores the restic master
        // password in plaintext per Backrest's own design). All
        // those files are individually chmod 0600 — tightening the
        // directory to 0700 closes the "world-executable parent dir
        // lets other local users cd in and stat the contents" gap.
        // Best-effort: log + continue on permission denied so a
        // first-launch on an exotic FS doesn't crash.
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(ConfigRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch { /* best-effort */ }

            // MSBuild's Content CopyToOutputDirectory doesn't preserve
            // the executable bit on Linux/macOS — a Backspace shipped
            // as a tarball with `dotnet publish` output has backrest /
            // restic / rclone at the default umask mode (typically
            // 0644). Process.Start then fails with "permission denied"
            // because the kernel refuses to exec a non-executable file.
            // First-launch +x repair: chmod each engine binary to 0755
            // if it exists. Best-effort, doesn't crash on a permissions
            // failure (the binary would already be unusable in that
            // case and the next start attempt surfaces the cause).
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                     | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                     | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            foreach (var bin in new[] { BackrestBinary, ResticBinary, RcloneBinary })
            {
                if (!File.Exists(bin)) continue;
                try { File.SetUnixFileMode(bin, mode); }
                catch { /* best-effort */ }
            }
        }
    }

    /// <summary>Windows reserved device names (case-insensitive,
    /// optionally with any extension). A directory or file with one
    /// of these names silently fails to create on Windows — see
    /// https://learn.microsoft.com/windows/win32/fileio/naming-a-file
    /// — so we replace bare reserved names with a safe suffix on the
    /// way through Sanitize. Only relevant on Windows but the check
    /// is cheap so we apply it everywhere for consistency.</summary>
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    internal static string Sanitize(string name)
    {
        if (string.IsNullOrEmpty(name)) return "_";
        var invalid = Path.GetInvalidFileNameChars();
        Span<char> buf = stackalloc char[name.Length];
        for (int i = 0; i < name.Length; i++)
            buf[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
        var sanitized = new string(buf);

        // Windows reserved names: a backup or path slug like "CON",
        // "PRN.log", or "NUL" silently fails to create on Windows
        // ("the system cannot find the path specified"). Compare by
        // basename (before the first dot) per Windows' rules, and
        // append an underscore to disambiguate. Leaves names with
        // an embedded reserved word alone (e.g. "CONFIG", "PRINTER")
        // — only the bare reserved tokens are remapped.
        var dotIdx = sanitized.IndexOf('.');
        var basename = dotIdx >= 0 ? sanitized.Substring(0, dotIdx) : sanitized;
        if (WindowsReservedNames.Contains(basename))
            sanitized = sanitized.Insert(basename.Length, "_");

        // Trailing dots and spaces are stripped by Windows on path
        // resolution, leading to "name" and "name." colliding. Strip
        // them up-front so the sanitized form is stable.
        sanitized = sanitized.TrimEnd('.', ' ');
        return sanitized.Length == 0 ? "_" : sanitized;
    }
}
