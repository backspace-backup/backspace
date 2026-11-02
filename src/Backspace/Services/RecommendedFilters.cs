using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Backspace.Services;

/// <summary>
/// Curated filter rules for Windows backups. Three groups:
///
///   • <see cref="WindowsSystemPatterns"/> — files Microsoft itself
///     documents as "do not back up while running" (live registry hives,
///     pagefile, hiberfil), system-reserved directories that are either
///     regenerable or owned by Windows Setup, and the Recycle Bin.
///     These are safe and recommended for EVERY Windows source.
///
///   • <see cref="CachesAndJunkPatterns"/> — caches, temp folders, and
///     known-noisy app directories that are routinely regenerable. Safe
///     for the vast majority of users; the few who actually want their
///     browser cache backed up can deselect.
///
///   • <see cref="DetectCloudSyncFolders"/> — runtime probe for
///     Dropbox / OneDrive / Google Drive sync roots. Each detected folder
///     is presented as an opt-in exclusion ("don't back up Dropbox — it's
///     already in the cloud"), with the actual path shown so the user
///     knows what they're skipping.
///
/// Patterns are restic-native globs — one per line, no Backspace-specific
/// prefixes. The exact same file content can be handed to vanilla restic
/// via <c>--exclude-file</c> (case-sensitive) or <c>--iexclude-file</c>
/// (case-insensitive); the file format does not encode case sensitivity.
/// Leading <c>/</c> anchors to the source root; trailing <c>/</c> matches
/// directories only; <c>*</c> matches anything except a slash; <c>**</c>
/// matches across slashes.
///
/// Case sensitivity: <see cref="FilterTranslator"/> routes bare-glob
/// lines through restic's <c>--iexclude</c> on Windows (the filesystem
/// is case-insensitive, so a case-sensitive pattern would silently miss
/// files whose stored case differs) and through <c>--exclude</c> on
/// Linux / macOS. Both buckets are restic-standard; the choice is
/// platform-default so a user's hand-edited file works without
/// per-line markers.
///
/// Backwards compatibility: prior versions emitted Duplicacy-style
/// <c>e:&lt;regex&gt;</c> patterns and Backspace-specific <c>-i:</c>
/// prefixes; <see cref="FilterTranslator"/> still parses both so a
/// migrated config keeps working. Re-clicking "Apply recommended
/// exclusions" rewrites the auto-block in pure restic-native form.
///
/// Sources:
///   - Microsoft KB on volume-shadow-copy-friendly backup ("files that are
///     locked or in use by the OS"): hiberfil/pagefile/swapfile, system
///     volume information.
///   - Duplicacy's own filter examples on GitHub (gilbertchen/duplicacy
///     wiki § Include/exclude patterns).
///   - Restic's "Backing up Windows" recipe (recommended exclude list).
///   - Common-knowledge consolidation from the maintainer's own multi-year
///     duplicacy-util filter file.
/// </summary>
public static class RecommendedFilters
{
    /// <summary>
    /// Marker comment used to bracket the auto-applied block inside the
    /// user's filter text. We re-find it on a second "Apply" so we can
    /// replace the block in place rather than appending duplicates.
    /// </summary>
    public const string AutoBlockHeader   = "# === Backspace recommended exclusions — start ===";
    public const string AutoBlockFooter   = "# === Backspace recommended exclusions — end ===";
    public const string CustomBlockHeader = "# === Backspace custom exclusions — start ===";
    public const string CustomBlockFooter = "# === Backspace custom exclusions — end ===";

    public const string EssentialBlockHeader = "# === Backspace essential exclusions (do not remove) ===";
    public const string EssentialBlockFooter = "# === Backspace essential exclusions — end ===";

    // ---------------------------------------------------------------
    // Pattern groups
    // ---------------------------------------------------------------

    /// <summary>
    /// Patterns we ALWAYS emit, regardless of which "Recommended exclusions"
    /// checkbox the user did or didn't tick. These are folders/files that
    /// Windows literally cannot back up safely (locked / regenerable / would
    /// restore as garbage onto a different machine), or system metadata that
    /// the source-tree picker hides anyway. Baking them into the filter
    /// list itself means a user who later changes their Sources to a drive
    /// root still gets these out-of-the-box — they don't have to remember
    /// to re-tick any toggle.
    /// </summary>
    public static readonly IReadOnlyList<string> EssentialPatterns = new[]
    {
        // Live OS memory snapshots — huge, locked, and useless restored.
        // Anchored at the source root with a leading `/` because they
        // only legitimately exist there on a Windows install; an
        // unanchored pattern would also strike user-named files.
        "/pagefile.sys",
        "/hiberfil.sys",
        "/swapfile.sys",
        "/DumpStack.log.tmp",

        // Per-drive system metadata and Windows-setup folders. They exist
        // only at the top of a drive, so they are anchored there:
        // unanchored, "Recovery/" also skipped a folder of that name in
        // Documents (U055, 31 October 2026).
        "/System Volume Information/",
        "/$RECYCLE.BIN/",
        "/Recovery/",
        "/Config.Msi/",
        "/$WinREAgent/",
        "/$GetCurrent/",
        "/$SysReset/",
        "/$Windows.~BT/",
        "/$Windows.~WS/",
        "/Windows10Upgrade/",
        "/Windows.old/",

        // Boot files — immutable; restoring would clobber a fresh
        // install's bootloader. Anchored: only the root-level copy is
        // a boot file; a user-named `bootmgr` shouldn't be skipped.
        "/bootmgr",
        "/BOOTNXT",
        "/BOOTSECT.BAK",

        // Live registry hives — locked at backup time and worse if
        // you restore them onto a different user/machine. One glob
        // covers the whole family (ntuser.dat, ntuser.dat.LOG,
        // ntuser.dat.LOG1, ntuser.dat.LOG2, plus the .blf/.regtrans-ms
        // siblings) since FilterTranslator routes bare globs through
        // restic's --iexclude on Windows.
        "ntuser.dat*",
        // The per-user classes hive, in AppData/Local/Microsoft/Windows.
        "UsrClass.dat*",

        // Cloud-sync / installer transients that always regenerate.
        "OneDriveTemp/",
        "/PerfLogs/",
    };

    /// <summary>
    /// Toggleable "Windows system files" recommendations on top of the
    /// always-on essentials. Ticking this in the editor ALSO excludes
    /// the broader system surface (the entire Windows directory, Office
    /// installer cache, thumbnail/icon caches, IE temp). These are
    /// useful but not always-required; some users back up only their
    /// own folders, where these patterns wouldn't match anything.
    /// </summary>
    public static readonly IReadOnlyList<string> WindowsSystemPatterns = new[]
    {
        // The Windows directory itself — restore from a fresh install,
        // not from a snapshot of a live system. Anchored at source
        // root.
        "/Windows/",

        // Office update cache.
        "/MSOCache/",

        // Windows Explorer thumbnail/icon caches — regenerable.
        "thumbs.db",
        "IconCache.db",

        // Old IE/Edge per-user temp dirs.
        "Temporary Internet Files/",

        // Windows' own data under ProgramData, rebuilt by Windows: the search
        // index (large, always in use), error reports and Defender's files.
        // Installer caches only serve repairs on this PC.
        "/ProgramData/Microsoft/Search/",
        "/ProgramData/Microsoft/Windows/WER/",
        "/ProgramData/Microsoft/Windows Defender/",
        "/ProgramData/Package Cache/",
        // Written by Windows setup only to show the folders' translated names
        // ("LocalizedResourceName=@%SystemRoot%\system32\shell32.dll,…").
        // Elsewhere desktop.ini may hold a person's own folder settings.
        "/Program Files/desktop.ini",
        "/Program Files (x86)/desktop.ini",

        // The template for new accounts, and the folders driver installers
        // unpack into at the top of the drive. Taken from the maintainer's
        // long-used Duplicacy list (U055, 31 October 2026).
        "/Users/Default/",
        "/Drivers/",
        "/SWSetup/",
        "/NVIDIA/",
        "/AMD/",
        "/Intel/",
    };

    /// <summary>Caches, temp folders, and known-noisy app dirs.
    /// Universally safe to skip; nothing here loses real user data.</summary>
    public static readonly IReadOnlyList<string> CachesAndJunkPatterns = new[]
    {
        // Generic temp folders, anywhere.
        "temp/",
        "tmp/",
        // `.tmp` / `.temp` file extensions, anywhere.
        "*.tmp",
        "*.temp",

        // Crash dumps / interrupted downloads.
        "*.crdownload",
        "*.dmp",
        "*.dump",
        "Logs/CrashReporter/",

        // Browser / Chromium / Electron app caches.
        "ShaderCache/",
        "GPUCache/",
        "InternalCache/",
        "icon_cache/",

        // Cloud-sync caches (the providers' own scratch dirs, not the
        // sync folder itself — that's an opt-in exclusion below).
        ".dropbox.cache/",
        ".tmp.drivedownload/",

        // Dev: node_modules, npm cache, Python packages — all
        // restorable from package manifests.
        "node_modules/",
        "npm-cache/",
        "compile-cache/",
        "__pycache__/",
        "Users/*/.nuget/packages/",
        "Users/*/.gradle/caches/",
        "Users/*/AppData/Local/pip/cache/",
        "Users/*/AppData/Local/go-build/",

        // macOS / cross-platform metadata noise (when backing up a
        // mixed-OS share).
        ".DS_Store",

        // App caches anywhere in AppData (`**` spans folders): browsers
        // and Electron apps keep them several levels down, such as
        // Chrome/User Data/Default/Cache. Logs only directly in an app
        // folder, where they are the app's own diagnostics.
        "Users/*/AppData/**/Cache/",
        "Users/*/AppData/**/Caches/",
        "Users/*/AppData/**/Code Cache/",
        "Users/*/AppData/**/GrShaderCache/",
        "Users/*/AppData/**/DawnCache/",
        "Users/*/AppData/**/DawnGraphiteCache/",
        "Users/*/AppData/**/DawnWebGPUCache/",
        "Users/*/AppData/**/Service Worker/CacheStorage/",
        "Users/*/AppData/**/Service Worker/ScriptCache/",
        "Users/*/AppData/*/Log/",
        "Users/*/AppData/*/Logs/",
        "Users/*/AppData/Local/CrashDumps/",
        "Users/*/AppData/Local/D3DSCache/",
        "Users/*/AppData/Local/Microsoft/Windows/INetCache/",
        // Thumbnail and icon caches, error reports, installer caches, the old
        // Edge's backups and Telegram's media cache, all rebuilt or not needed.
        "Users/*/AppData/Local/Microsoft/Windows/Explorer/",
        "Users/*/AppData/Local/Microsoft/Windows/WER/",
        "Users/*/AppData/Local/Package Cache/",
        "Users/*/MicrosoftEdgeBackups/",
        "tdata/user_data/media_cache/",

        // Store apps keep settings and data in Packages (Sticky Notes, for
        // example); only their caches are skipped. Skipping all of Packages
        // lost that data (U055).
        "Users/*/AppData/Local/Packages/*/AC/",
        "Users/*/AppData/Local/Packages/*/LocalCache/",
        "Users/*/AppData/Local/Packages/*/TempState/",
        "Users/*/AppData/*/Microsoft/Windows/Recent/",
        "Users/*/AppData/*/Microsoft/Windows/Notifications/",
    };

    // ---------------------------------------------------------------
    // Cloud-sync detection
    // ---------------------------------------------------------------

    /// <summary>
    /// Returns sensible recommendation defaults for a given source path.
    /// Drive roots get the full Windows-system block (they contain the
    /// page file, recycle bin, etc.). Subfolders typically only need
    /// caches/junk patterns — backing up `C:\Users\me\Documents` doesn't
    /// need rules to skip `C:\Windows\`. Cloud sync folders are still
    /// suggested regardless of source root.
    /// </summary>
    public static SuggestionDefaults SuggestForSources(IEnumerable<string> sourcePaths)
    {
        var anyDriveRoot = false;
        foreach (var raw in sourcePaths ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var trimmed = raw.TrimEnd('\\', '/');
            // Drive root: "C:" or "C:\".
            if (trimmed.Length == 2 && trimmed[1] == ':') { anyDriveRoot = true; continue; }
            // (Future: classify other source kinds here — Documents-only,
            // network shares, etc. — to drive different default mixes.)
        }

        return new SuggestionDefaults(
            // System patterns only matter when a drive root is in scope.
            ExcludeWindowsSystem: anyDriveRoot,
            // Caches/junk are useful for any source (always-applicable).
            ExcludeCachesAndJunk: true);
    }

    /// <param name="ExcludeWindowsSystem">Default state for the
    /// "Windows system files" recommendation toggle in the editor.</param>
    /// <param name="ExcludeCachesAndJunk">Default state for the
    /// "App caches and junk" recommendation toggle.</param>
    public readonly record struct SuggestionDefaults(
        bool ExcludeWindowsSystem,
        bool ExcludeCachesAndJunk);

    public sealed record CloudSyncFolder(CloudSyncProvider Provider, string Label, string Path);

    public enum CloudSyncProvider { Dropbox, OneDrive, GoogleDrive }

    /// <summary>
    /// Best-effort enumeration of cloud-sync roots present on this
    /// machine. Each entry is something the user might reasonably want
    /// to exclude from a "back up my whole computer" job because the
    /// data is already replicated in the provider's cloud.
    ///
    /// We probe each provider's official location:
    ///   - Dropbox: <c>%APPDATA%\Dropbox\info.json</c> (documented JSON
    ///     containing personal/business root paths).
    ///   - OneDrive: <c>%OneDrive%</c>, <c>%OneDriveCommercial%</c>,
    ///     <c>%OneDriveConsumer%</c> environment variables that the
    ///     OneDrive client maintains.
    ///   - Google Drive: <c>HKCU\Software\Google\DriveFS\Share</c>
    ///     registry key for the new Drive for desktop client; falls
    ///     back to the legacy <c>%LOCALAPPDATA%\Google\Drive\</c>
    ///     sync_config marker for the older Backup &amp; Sync client.
    ///
    /// Anything we can't deterministically locate is omitted (we'd
    /// rather under-suggest than recommend excluding the wrong path).
    /// </summary>
    public static IReadOnlyList<CloudSyncFolder> DetectCloudSyncFolders()
    {
        var found = new List<CloudSyncFolder>();
        try { found.AddRange(DetectDropbox()); } catch { /* best-effort */ }
        try { found.AddRange(DetectOneDrive()); } catch { /* best-effort */ }
        try { found.AddRange(DetectGoogleDrive()); } catch { /* best-effort */ }
        return found;
    }

    private static IEnumerable<CloudSyncFolder> DetectDropbox()
    {
        var infoPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Dropbox", "info.json");
        if (!File.Exists(infoPath)) yield break;

        Dictionary<string, JsonElement>? root;
        try
        {
            using var stream = File.OpenRead(infoPath);
            root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(stream);
        }
        catch { yield break; }
        if (root is null) yield break;

        foreach (var kind in new[] { "personal", "business" })
        {
            if (!root.TryGetValue(kind, out var account)) continue;
            if (account.ValueKind != JsonValueKind.Object) continue;
            if (!account.TryGetProperty("path", out var pathElem)) continue;
            var path = pathElem.GetString();
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) continue;
            yield return new CloudSyncFolder(
                CloudSyncProvider.Dropbox,
                kind == "personal" ? "Dropbox" : "Dropbox (Business)",
                path);
        }
    }

    private static IEnumerable<CloudSyncFolder> DetectOneDrive()
    {
        // OneDrive client sets these env vars while signed in. They're
        // the canonical way Microsoft documents for finding the root.
        var personal   = Environment.GetEnvironmentVariable("OneDriveConsumer")
                      ?? Environment.GetEnvironmentVariable("OneDrive");
        var commercial = Environment.GetEnvironmentVariable("OneDriveCommercial");

        if (!string.IsNullOrWhiteSpace(personal) && Directory.Exists(personal))
            yield return new CloudSyncFolder(CloudSyncProvider.OneDrive, "OneDrive", personal!);

        if (!string.IsNullOrWhiteSpace(commercial)
            && !string.Equals(commercial, personal, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(commercial))
            yield return new CloudSyncFolder(CloudSyncProvider.OneDrive, "OneDrive for Business", commercial!);
    }

    private static IEnumerable<CloudSyncFolder> DetectGoogleDrive()
    {
        // "Drive for desktop" 2.x mounts a virtual letter (often G:),
        // configurable. The mount letter is in the registry under
        // HKCU\Software\Google\DriveFS, but the value name varies
        // across versions. Scanning all top-level drives for the
        // sentinel folder name is more reliable.
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            // The mounted letter contains "My Drive" and (optionally)
            // "Shared drives" at its root.
            var myDrive = Path.Combine(drive.RootDirectory.FullName, "My Drive");
            if (Directory.Exists(myDrive))
                yield return new CloudSyncFolder(CloudSyncProvider.GoogleDrive,
                    $"Google Drive ({drive.Name.TrimEnd('\\')})", myDrive);
        }

        // Legacy "Backup and Sync" client uses %USERPROFILE%\Google Drive
        var legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Google Drive");
        if (Directory.Exists(legacy))
            yield return new CloudSyncFolder(CloudSyncProvider.GoogleDrive, "Google Drive (legacy)", legacy);
    }

    // ---------------------------------------------------------------
    // Filter-text manipulation
    // ---------------------------------------------------------------

    /// <summary>
    /// Renders the chosen recommendation groups into a single text block
    /// bracketed by <see cref="AutoBlockHeader"/> / <see cref="AutoBlockFooter"/>.
    /// Cloud-folder exclusions are emitted as exact-path regexes
    /// (Duplicacy walks paths relative to the source root, so we drop
    /// the drive letter and forward-slash the result).
    /// </summary>
    public static string RenderBlock(
        bool includeWindowsSystem,
        bool includeCachesAndJunk,
        IReadOnlyCollection<CloudSyncFolder> cloudExcludes)
    {
        var sb = new StringBuilder();
        sb.AppendLine(AutoBlockHeader);
        sb.AppendLine("# Generated by Backspace. Edit if you like: this whole block");
        sb.AppendLine("# will be replaced (preserving your edits outside the markers)");
        sb.AppendLine("# the next time you click \"Apply recommended exclusions\".");
        sb.AppendLine();

        // Always-on essentials. Emitted regardless of the toggles below
        // because these are folders Windows can't back up safely AND
        // are noise even on user-only sources. Keeps the user covered
        // even if they later expand Sources to include a drive root.
        sb.AppendLine("# Essentials: always excluded; safe for any source.");
        foreach (var p in EssentialPatterns) sb.AppendLine(p);
        sb.AppendLine();

        if (includeWindowsSystem)
        {
            sb.AppendLine("# Windows system files: locked, regenerable, or restore-unsafe.");
            foreach (var p in WindowsSystemPatterns) sb.AppendLine(p);
            sb.AppendLine();
        }

        if (includeCachesAndJunk)
        {
            sb.AppendLine("# App caches, temp files, dev junk: universally safe to skip.");
            foreach (var p in CachesAndJunkPatterns) sb.AppendLine(p);
            sb.AppendLine();
        }

        if (cloudExcludes.Count > 0)
        {
            sb.AppendLine("# Cloud-synced folders: already replicated in their providers' clouds.");
            foreach (var c in cloudExcludes)
            {
                var rel = ToSourceRelativePath(c.Path);
                if (rel is null) continue;
                sb.AppendLine($"# {c.Label} at {c.Path}");
                // `/rel/` is source-root-anchored so we only match the
                // specific cloud-sync folder, not a same-named subdir
                // somewhere else in the tree. Bare glob — restic-native.
                sb.AppendLine($"/{rel}/");
            }
            sb.AppendLine();
        }

        sb.AppendLine(AutoBlockFooter);
        return sb.ToString();
    }

    /// <summary>
    /// Renders just the essential exclusions wrapped in their own marker
    /// pair. Used to seed a brand-new backup's filter text so the
    /// always-on protections (recycle bin, $WinREAgent, pagefile, etc.)
    /// are present from the moment the backup is created — even before
    /// the user clicks "Apply recommended exclusions". The marker pair
    /// distinguishes this block from the larger toggleable
    /// recommendations block, so a later Apply can lay the bigger block
    /// alongside without colliding.
    /// </summary>
    public static string RenderEssentialBlock()
    {
        var sb = new StringBuilder();
        sb.AppendLine(EssentialBlockHeader);
        sb.AppendLine("# These exclusions apply to every backup Backspace runs.");
        sb.AppendLine("# They cover folders Windows can't back up safely (locked /");
        sb.AppendLine("# regenerable / would restore as garbage). Editing or removing");
        sb.AppendLine("# this block is not recommended; re-clicking Apply restores it.");
        sb.AppendLine();
        foreach (var p in EssentialPatterns) sb.AppendLine(p);
        sb.AppendLine();
        sb.AppendLine(EssentialBlockFooter);
        return sb.ToString();
    }

    // ---------------------------------------------------------------
    // Imported filters and later list changes (U056, U057)
    // ---------------------------------------------------------------

    private const string EssentialsHeading = "# Essentials: always excluded; safe for any source.";
    private const string WindowsHeading = "# Windows system files: locked, regenerable, or restore-unsafe.";
    private const string CachesHeading = "# App caches, temp files, dev junk: universally safe to skip.";
    private const string CloudHeading = "# Cloud-synced folders: already replicated in their providers' clouds.";

    private static readonly (string Heading, IReadOnlyList<string> Patterns)[] Groups =
    {
        (EssentialsHeading, EssentialPatterns),
        (WindowsHeading, WindowsSystemPatterns),
        (CachesHeading, CachesAndJunkPatterns),
    };

    /// <summary>Patterns earlier versions recommended and these lists no longer
    /// contain. Only these are offered for removal; any other rule in the block
    /// is the person's own. Add a pattern here whenever one is dropped above.</summary>
    internal static readonly IReadOnlyList<string> RetiredPatterns = new[]
    {
        // Anchored at the top of the drive on 31 October 2026 (U055).
        "DumpStack.log.tmp", "System Volume Information/", "$RECYCLE.BIN/", "Recovery/", "Config.Msi/",
        "$WinREAgent/", "$GetCurrent/", "$SysReset/", "$Windows.~BT/", "$Windows.~WS/",
        "Windows10Upgrade/", "Windows.old/", "PerfLogs/",
        // Widened to every depth of AppData, and Packages narrowed to caches (U055).
        "Users/*/AppData/*/Cache/", "Users/*/AppData/*/Caches/", "Users/*/AppData/Local/Packages/",
        // The whole Edge profile, bookmarks and passwords included; its caches
        // are covered by the cache rules, as for Chrome (U065, 1 November 2026).
        "Users/*/AppData/*/Microsoft/Edge/",
    };

    /// <summary>The recommended block holding exactly these patterns, under
    /// their group headings. Used for imported filters, which keep only the
    /// recommended rules the person already had.</summary>
    internal static string RenderBlock(IReadOnlyCollection<string> patterns, IReadOnlyList<string>? cloudLines = null)
    {
        var chosen = new HashSet<string>(patterns, StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        sb.AppendLine(AutoBlockHeader);
        sb.AppendLine("# Generated by Backspace. Edit if you like: this whole block");
        sb.AppendLine("# will be replaced (preserving your edits outside the markers)");
        sb.AppendLine("# the next time you click \"Apply recommended exclusions\".");
        sb.AppendLine();
        foreach (var (heading, list) in Groups)
        {
            var present = list.Where(chosen.Contains).ToList();
            if (present.Count == 0) continue;
            sb.AppendLine(heading);
            foreach (var p in present) sb.AppendLine(p);
            sb.AppendLine();
        }
        if (cloudLines is { Count: > 0 })
        {
            sb.AppendLine(CloudHeading);
            foreach (var line in cloudLines) sb.AppendLine(line);
            sb.AppendLine();
        }
        sb.AppendLine(AutoBlockFooter);
        return sb.ToString();
    }

    /// <summary>What today's recommended lists would add to or remove from a
    /// backup's saved recommended block. Saved filters never change by
    /// themselves; the editor offers this when the person edits the backup.</summary>
    public sealed record ListUpdate(IReadOnlyList<string> Add, IReadOnlyList<string> Remove)
    {
        public bool IsEmpty => Add.Count == 0 && Remove.Count == 0;
    }

    private sealed record SavedBlock(int Start, int End, List<(string? Heading, string Line)> Lines, bool EssentialsOnly);

    private static SavedBlock? FindBlock(string[] lines)
    {
        foreach (var (header, footer, essentialsOnly) in new[] { (AutoBlockHeader, AutoBlockFooter, false), (EssentialBlockHeader, EssentialBlockFooter, true) })
        {
            var start = Array.FindIndex(lines, l => l.Trim() == header);
            if (start < 0) continue;
            var end = Array.FindIndex(lines, start + 1, l => l.Trim() == footer);
            if (end < 0) continue;
            var parsed = new List<(string?, string)>();
            string? heading = essentialsOnly ? EssentialsHeading : null;
            for (var i = start + 1; i < end; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith('#'))
                {
                    if (Groups.Any(g => g.Heading == line) || line == CloudHeading) heading = line;
                    parsed.Add((heading, line));
                    continue;
                }
                parsed.Add((heading, line));
            }
            return new(start, end, parsed, essentialsOnly);
        }
        return null;
    }

    /// <summary>Patterns to add (missing from a group the block uses) and to
    /// remove (no longer in any recommended list), minus ignored ones.</summary>
    public static ListUpdate CheckForUpdates(string? filters, IReadOnlyCollection<string>? ignored = null)
    {
        var lines = (filters ?? "").Replace("\r\n", "\n").Split('\n');
        var block = FindBlock(lines);
        if (block is null) return new(Array.Empty<string>(), Array.Empty<string>());
        var skip = new HashSet<string>(ignored ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var patterns = block.Lines.Where(l => !l.Line.StartsWith('#') && l.Heading != CloudHeading).Select(l => l.Line).ToList();
        var present = new HashSet<string>(patterns, StringComparer.OrdinalIgnoreCase);
        var retired = new HashSet<string>(RetiredPatterns, StringComparer.OrdinalIgnoreCase);
        var used = Groups.Where(g => g.Heading == EssentialsHeading
            || block.Lines.Any(l => l.Heading == g.Heading)).ToList();
        var add = used.SelectMany(g => g.Patterns).Where(p => !present.Contains(p) && !skip.Contains(p)).Distinct().ToList();
        var remove = patterns.Where(p => retired.Contains(p) && !skip.Contains(p)).Distinct().ToList();
        return new(add, remove);
    }

    /// <summary>Applies <see cref="CheckForUpdates"/> to the saved block,
    /// keeping its cloud-folder lines and every rule outside it.</summary>
    public static string ApplyUpdates(string filters, ListUpdate update)
    {
        var lines = filters.Replace("\r\n", "\n").Split('\n');
        var block = FindBlock(lines);
        if (block is null || update.IsEmpty) return filters;
        var removed = new HashSet<string>(update.Remove, StringComparer.OrdinalIgnoreCase);
        var kept = block.Lines.Where(l => !l.Line.StartsWith('#') && l.Heading != CloudHeading && !removed.Contains(l.Line)).Select(l => l.Line);
        var cloud = block.Lines.Where(l => l.Heading == CloudHeading && l.Line != CloudHeading).Select(l => l.Line).ToList();
        var patterns = kept.Concat(update.Add).ToList();
        // Rules the person added inside the block that are in no list stay too.
        var known = new HashSet<string>(Groups.SelectMany(g => g.Patterns), StringComparer.OrdinalIgnoreCase);
        var own = patterns.Where(p => !known.Contains(p)).ToList();
        // The fixed essentials block fits only when the result is exactly the
        // essentials list: the person may have declined some additions (U057).
        var essentialBlock = block.EssentialsOnly && cloud.Count == 0
            && new HashSet<string>(patterns.Where(known.Contains), StringComparer.OrdinalIgnoreCase).SetEquals(EssentialPatterns);
        var rendered = (essentialBlock ? RenderEssentialBlock() : RenderBlock(patterns, cloud))
            .TrimEnd('\r', '\n').Replace("\r\n", "\n").Split('\n').ToList();
        if (own.Count > 0) rendered.InsertRange(rendered.Count - 1, new[] { "# Your own rules kept from this block." }.Concat(own).Append(""));
        var result = lines.Take(block.Start).Concat(rendered).Concat(lines.Skip(block.End + 1));
        return string.Join("\n", result);
    }

    /// <summary>
    /// Splits imported Duplicacy filters into the rules that are exactly
    /// one of Backspace's recommended patterns (counted as recommended) and
    /// the rest, kept as the person's own rules (U056). A line counts only
    /// when every pattern it translates to is a recommended one with the
    /// same anchor; anything else, including includes, stays as written.
    /// </summary>
    internal static (string Custom, IReadOnlyList<string> Recommended) RecognizeImported(string duplicacyFilters)
    {
        var byKey = Groups.SelectMany(g => g.Patterns)
            .GroupBy(Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var recognized = new List<string>();
        var custom = new List<string>();
        foreach (var raw in duplicacyFilters.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("e:(?i)", StringComparison.Ordinal)
                && DuplicacyFilterTranslator.ToGlobs(line[6..]) is { Count: > 0 } globs
                && globs.All(g => byKey.ContainsKey(Key(g.Glob))))
            {
                foreach (var g in globs) if (!recognized.Contains(byKey[Key(g.Glob)])) recognized.Add(byKey[Key(g.Glob)]);
                continue;
            }
            custom.Add(raw.TrimEnd('\r'));
        }
        return (string.Join("\n", custom).Trim('\n'), recognized);
    }

    /// <summary>A folder rule and the same rule for its contents mean the
    /// same thing here; letter case does not matter on Windows.</summary>
    private static string Key(string glob)
    {
        var g = glob.Replace('\\', '/');
        if (g.EndsWith("/*", StringComparison.Ordinal)) g = g[..^2];
        return g.TrimEnd('/');
    }

    /// <summary>
    /// Reshapes <paramref name="existingFilters"/> so that the user's
    /// hand-edit zone sits ABOVE the Backspace-generated recommended
    /// block — the natural layout: people edit at the top, generated
    /// noise stays parked below. Specifically:
    ///
    ///   * A pair of <see cref="CustomBlockHeader"/> /
    ///     <see cref="CustomBlockFooter"/> marker lines is emitted at the
    ///     top. Any existing custom-block content is preserved between
    ///     them; any "loose" rules the user wrote outside both marker
    ///     pairs are migrated INTO the custom block on first re-apply.
    ///   * The recommended block (passed in via
    ///     <paramref name="newBlock"/>) is appended after the custom
    ///     block, replacing any previous recommended-block content.
    ///
    /// The merge is idempotent: re-applying the same arguments leaves
    /// the result unchanged.
    /// </summary>
    public static string MergeIntoFilters(string? existingFilters, string newBlock)
    {
        existingFilters ??= "";

        // 1. Extract any existing recommended-block content so we can
        //    drop it (newBlock supersedes it).
        var withoutRecommended = StripBlock(existingFilters, AutoBlockHeader, AutoBlockFooter);

        // Strip only standalone custom marker comments. Keep all rules in
        // their original order, including copied, nested or incomplete blocks.
        // Extracting just the first pair used to nest later pairs on reapply.
        var combinedCustomBody = string.Join("\n", withoutRecommended.Replace("\r\n", "\n").Split('\n')
            .Where(line => line.Trim() != CustomBlockHeader && line.Trim() != CustomBlockFooter))
            .Trim('\r', '\n');
        if (combinedCustomBody.Length == 0)
            combinedCustomBody = "# Add your own rules here.";

        var sb = new StringBuilder();
        sb.AppendLine(CustomBlockHeader);
        sb.AppendLine(combinedCustomBody);
        sb.AppendLine(CustomBlockFooter);
        sb.AppendLine();
        sb.Append(newBlock);
        return sb.ToString();
    }

    private static string StripBlock(string text, string header, string footer)
    {
        var retained = new List<string>();
        List<string>? candidate = null;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Trim() == header)
            {
                // An unmatched older header must not consume the next valid
                // block's footer and erase user rules between the two blocks.
                if (candidate is not null) retained.AddRange(candidate);
                candidate = new List<string> { line };
            }
            else if (line.Trim() == footer && candidate is not null)
                candidate = null; // Only a complete generated block is replaced.
            else if (candidate is not null)
                candidate.Add(line);
            else
                retained.Add(line);
        }
        if (candidate is not null) retained.AddRange(candidate);
        return string.Join("\n", retained);
    }

    /// <summary>
    /// Converts an absolute path like <c>C:\Users\me\OneDrive</c> to
    /// the source-root-relative path fragment we splice into restic-
    /// glob anchored excludes: <c>Users/me/OneDrive</c>. Returns null
    /// if the path doesn't look like an absolute Windows path.
    /// </summary>
    internal static string? ToSourceRelativePath(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return null;
        var trimmed = absolutePath.TrimEnd('/', '\\');

        // UNC path: \\server\share\Sub\Path → strip the server+share so
        // the result is the source-relative tail "Sub/Path". Without
        // this, the previous code escaped the leading double-backslash
        // into "\\\\server\\share\\..." which never matched a duplicacy
        // forward-slash source-relative path — every cloud-folder
        // exclude on a UNC source silently no-op'd.
        if (trimmed.StartsWith("\\\\", StringComparison.Ordinal)
            || trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            // Skip the leading separator chars then \server\share\ —
            // the first two path segments after the prefix.
            var withoutPrefix = trimmed.AsSpan(2).ToString().Replace('\\', '/');
            int firstSlash = withoutPrefix.IndexOf('/');
            if (firstSlash < 0) return null;
            int secondSlash = withoutPrefix.IndexOf('/', firstSlash + 1);
            if (secondSlash < 0) return null;
            trimmed = withoutPrefix[(secondSlash + 1)..];
        }
        else if (trimmed.Length >= 2 && trimmed[1] == ':')
        {
            // Drive letter: "C:\Users\..." → "Users\...".
            trimmed = trimmed[2..];
        }

        trimmed = trimmed.TrimStart('/', '\\').Replace('\\', '/');
        if (trimmed.Length == 0) return null;

        // Restic globs don't need regex escaping. The dangerous glob
        // metachars are `*`, `?`, `[`, `]` — vanishingly rare in real
        // user folder names. We don't escape them here because they'd
        // typically be safe to leave as literal characters in restic's
        // pattern matcher, and aggressive escaping would emit
        // backslashes restic doesn't understand. Hand-curated cloud
        // paths from DetectCloudSyncFolders() shouldn't contain glob
        // metacharacters anyway.
        return trimmed;
    }
}
