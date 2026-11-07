using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Backspace.Services;

/// <summary>
/// Curated filter rules, in restic's own syntax: the Filters text is the
/// exclude file restic reads (<see cref="FilterTranslator"/>), so every rule
/// here is written exactly as restic gets it (U151, 6 November 2026).
///
///   • <see cref="EssentialPatterns"/> and <see cref="WindowsSystemPatterns"/>
///     — files and folders Windows keeps at the top of a drive (live page and
///     hibernation files, setup and update leftovers, the Windows folder).
///   • <see cref="CachesAndJunkPatterns"/> — caches, temp folders and
///     regenerable app data. Safe for the vast majority of people.
///   • <see cref="AlwaysExcludedPatterns"/> — written into every backup's text
///     as a visible block (<see cref="AlwaysBlockHeader"/>).
///   • <see cref="DetectCloudSyncFolders"/> — Dropbox, OneDrive and Google
///     Drive folders, offered one by one.
///
/// Rules are tied to the top of the drive, <c>?:/…</c> on Windows (<c>?</c>
/// is any one character and only a drive name holds <c>:</c>), so a folder a
/// person made on purpose deeper down (an old PC's copied profile, say) is
/// never matched. Rules that are useful wherever they are (node_modules/,
/// *.tmp, .DS_Store…) are written without an anchor and match at any depth.
/// A literal <c>$</c> is written <c>$$</c>: restic expands <c>$NAME</c>.
/// On Linux and macOS, where restic's leading <c>/</c> is the top of the file
/// system, profile rules become <c>/home/*/…</c> or <c>/Users/*/…</c> and the
/// Windows-only ones are left out.
///
/// Sources:
///   - Microsoft KB on volume-shadow-copy-friendly backup ("files that are
///     locked or in use by the OS"): hiberfil/pagefile/swapfile, system
///     volume information.
///   - Restic's "Backing up Windows" recipe (recommended exclude list).
///   - Common-knowledge consolidation from the maintainer's own multi-year
///     filter file.
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

    /// <summary>Heads the block of <see cref="AlwaysExcludedPatterns"/> at the
    /// end of each backup's text. The block runs to the next blank line.</summary>
    public const string AlwaysBlockHeader = "# === Backspace essential exclusions (always applied) ===";

    // ---------------------------------------------------------------
    // Pattern groups (Windows forms; see ForThisPlatform)
    // ---------------------------------------------------------------

    private static readonly string[] WindowsEssentials =
    {
        // Live OS memory snapshots: huge, locked, and useless restored.
        "?:/pagefile.sys",
        "?:/hiberfil.sys",
        "?:/swapfile.sys",
        "?:/DumpStack.log.tmp",

        // Per-drive system metadata and Windows-setup folders, at the top of
        // a drive only: elsewhere, "Recovery/" was a person's own folder
        // (U055, 31 October 2026). Recovery itself is no longer recommended
        // (U106): it can hold a manufacturer's recovery files.
        "?:/System Volume Information/",
        "?:/$$RECYCLE.BIN/",
        "?:/Config.Msi/",
        "?:/$$WinREAgent/",
        "?:/$$GetCurrent/",
        "?:/$$SysReset/",
        "?:/$$Windows.~BT/",
        "?:/$$Windows.~WS/",
        "?:/Windows10Upgrade/",
        "?:/Windows.old/",

        // Boot files: restoring them would clobber a fresh install's loader.
        "?:/bootmgr",
        "?:/BOOTNXT",
        "?:/BOOTSECT.BAK",

        // OneDrive's download scratch, which it creates at the top of a drive.
        "?:/OneDriveTemp/",
        "?:/PerfLogs/",
    };

    /// <summary>
    /// Written into every backup's text, as a visible block at its end
    /// (U106, U151): only what is useless in any restore. The page, swap and
    /// hibernation files are rebuilt by Windows at every start; System Volume
    /// Information holds shadow copies and the search index; the other
    /// folders are temporary files of Windows Update, upgrades, resets and
    /// installers; the boot files can't make a disk bootable from a file
    /// backup. Windows' app execution aliases (wt.exe, winget.exe…) are links
    /// the engine can't store ("unsupported file type": local_c's runs ended
    /// with 92 errors from them, 4 November 2026).
    /// </summary>
    private static readonly string[] WindowsAlwaysExcluded =
    {
        "?:/pagefile.sys",
        "?:/hiberfil.sys",
        "?:/swapfile.sys",
        "?:/DumpStack.log.tmp",
        "?:/System Volume Information/",
        "?:/Config.Msi/",
        "?:/$$WinREAgent/",
        "?:/$$GetCurrent/",
        "?:/$$SysReset/",
        "?:/$$Windows.~BT/",
        "?:/$$Windows.~WS/",
        "?:/Windows10Upgrade/",
        "?:/bootmgr",
        "?:/BOOTNXT",
        "?:/BOOTSECT.BAK",
        "?:/OneDriveTemp/",
        "?:/Users/*/AppData/Local/Microsoft/WindowsApps/",
    };

    private static readonly string[] WindowsSystem =
    {
        // The Windows directory itself: restore from a fresh install, not
        // from a snapshot of a live system.
        "?:/Windows/",

        // Office update cache.
        "?:/MSOCache/",

        // Windows Explorer thumbnail/icon caches, wherever they are.
        "thumbs.db",
        "IconCache.db",

        // Old IE/Edge per-user temp dirs.
        "Temporary Internet Files/",

        // Windows' own data under ProgramData, rebuilt by Windows: the search
        // index (large, always in use), error reports and Defender's files.
        // Installer caches only serve repairs on this PC.
        "?:/ProgramData/Microsoft/Search/",
        "?:/ProgramData/Microsoft/Windows/WER/",
        "?:/ProgramData/Microsoft/Windows Defender/",
        "?:/ProgramData/Package Cache/",
        // Written by Windows setup only to show the folders' translated names.
        // Elsewhere desktop.ini may hold a person's own folder settings.
        "?:/Program Files/desktop.ini",
        "?:/Program Files (x86)/desktop.ini",

        // The template for new accounts, and the folders driver installers
        // unpack into at the top of the drive (U055, 31 October 2026).
        "?:/Users/Default/",
        "?:/Drivers/",
        "?:/SWSetup/",
        "?:/NVIDIA/",
        "?:/AMD/",
        "?:/Intel/",
    };

    private static readonly string[] WindowsCaches =
    {
        // Windows' own temp folder for each account. Folders named temp or
        // tmp elsewhere and .dmp/.dump files can be a person's own work
        // (U106, 4 November 2026); *.tmp and *.temp are offered again (U151).
        "?:/Users/*/AppData/Local/Temp/",
        "*.tmp",
        "*.temp",

        // Interrupted downloads and crash reporters' folders.
        "*.crdownload",
        "?:/Users/*/AppData/**/Logs/CrashReporter/",

        // Browser / Chromium / Electron app caches.
        "ShaderCache/",
        "GPUCache/",
        "?:/Users/*/AppData/**/InternalCache/",
        "?:/Users/*/AppData/**/icon_cache/",

        // Cloud-sync caches (the providers' own scratch dirs, not the sync
        // folder itself: that's an opt-in exclusion below).
        ".dropbox.cache/",
        ".tmp.drivedownload/",

        // Dev: restorable from package manifests, wherever a project is.
        "node_modules/",
        "npm-cache/",
        "?:/Users/*/AppData/**/compile-cache/",
        "__pycache__/",
        "?:/Users/*/.nuget/packages/",
        "?:/Users/*/.gradle/caches/",
        "?:/Users/*/AppData/Local/pip/cache/",
        "?:/Users/*/AppData/Local/go-build/",
        // Go's module cache (GOMODCACHE): go downloads each module again
        // from its proxy when the folder is missing (U141).
        "?:/Users/*/go/pkg/mod/",

        // macOS metadata noise (a mixed-OS share).
        ".DS_Store",

        // App caches anywhere in AppData (** spans folders): browsers and
        // Electron apps keep them several levels down, such as
        // Chrome/User Data/Default/Cache.
        "?:/Users/*/AppData/**/Cache/",
        "?:/Users/*/AppData/**/Caches/",
        "?:/Users/*/AppData/**/Code Cache/",
        "?:/Users/*/AppData/**/GrShaderCache/",
        "?:/Users/*/AppData/**/DawnCache/",
        "?:/Users/*/AppData/**/DawnGraphiteCache/",
        "?:/Users/*/AppData/**/DawnWebGPUCache/",
        "?:/Users/*/AppData/**/Service Worker/CacheStorage/",
        "?:/Users/*/AppData/**/Service Worker/ScriptCache/",
        "?:/Users/*/AppData/Local/CrashDumps/",
        "?:/Users/*/AppData/Local/D3DSCache/",
        "?:/Users/*/AppData/Local/Microsoft/Windows/INetCache/",
        // Thumbnail and icon caches, error reports, installer caches and
        // Telegram's media cache, all rebuilt or not needed.
        "?:/Users/*/AppData/Local/Microsoft/Windows/Explorer/",
        "?:/Users/*/AppData/Local/Microsoft/Windows/WER/",
        "?:/Users/*/AppData/Local/Package Cache/",
        "tdata/user_data/media_cache/",

        // Store apps keep settings and data in Packages (Sticky Notes, for
        // example); only their caches are skipped (U055).
        "?:/Users/*/AppData/Local/Packages/*/AC/",
        "?:/Users/*/AppData/Local/Packages/*/TempState/",
        "?:/Users/*/AppData/*/Microsoft/Windows/Recent/",
        "?:/Users/*/AppData/*/Microsoft/Windows/Notifications/",
    };

    /// <summary>Recommended for any Windows source; part of the recommended
    /// block's first group.</summary>
    public static readonly IReadOnlyList<string> EssentialPatterns = ForThisPlatform(WindowsEssentials);

    /// <summary>See <see cref="WindowsAlwaysExcluded"/>. Empty on Linux and
    /// macOS, which have none of these files.</summary>
    public static readonly IReadOnlyList<string> AlwaysExcludedPatterns = ForThisPlatform(WindowsAlwaysExcluded);

    /// <summary>The "Windows system files" recommendations.</summary>
    public static readonly IReadOnlyList<string> WindowsSystemPatterns = ForThisPlatform(WindowsSystem);

    /// <summary>Caches, temp folders, and known-noisy app dirs.</summary>
    public static readonly IReadOnlyList<string> CachesAndJunkPatterns = ForThisPlatform(WindowsCaches);

    /// <summary>The Windows rules as this system's restic needs them: on
    /// Linux and macOS a profile rule starts at <c>/home/*/</c> or
    /// <c>/Users/*/</c>, and rules for Windows' own files are left out.</summary>
    private static IReadOnlyList<string> ForThisPlatform(IEnumerable<string> windowsRules)
        => Platform.PlatformInfo.IsWindows ? windowsRules.ToArray() : windowsRules.Select(ToUnix).OfType<string>().ToArray();

    internal static string? ToUnix(string rule)
    {
        const string profile = "?:/Users/*/";
        if (!rule.StartsWith("?:/", StringComparison.Ordinal)) return rule;
        if (rule.StartsWith(profile + "AppData/", StringComparison.OrdinalIgnoreCase)) return null;
        if (rule.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
            return (OperatingSystem.IsMacOS() ? "/Users/*/" : "/home/*/") + rule[profile.Length..];
        return null;
    }

    // ---------------------------------------------------------------
    // The always-applied block
    // ---------------------------------------------------------------

    /// <summary>The block's lines: its header, then each rule.</summary>
    internal static IReadOnlyList<string> AlwaysBlockLines()
        => AlwaysExcludedPatterns.Count == 0 ? Array.Empty<string>() : AlwaysExcludedPatterns.Prepend(AlwaysBlockHeader).ToArray();

    /// <summary>The block as text; empty where there is nothing to exclude.</summary>
    internal static string AlwaysBlockText() => string.Join("\n", AlwaysBlockLines());

    /// <summary>The first and last line of the always-applied block in
    /// <paramref name="lines"/>: its header and the lines up to the next blank
    /// one. Null when the text has no such block.</summary>
    internal static (int Start, int End)? FindAlwaysBlock(IReadOnlyList<string> lines)
    {
        var start = -1;
        for (var i = 0; i < lines.Count; i++) if (lines[i].Trim() == AlwaysBlockHeader) { start = i; break; }
        if (start < 0) return null;
        var end = start;
        while (end + 1 < lines.Count && lines[end + 1].Trim().Length > 0) end++;
        return (start, end);
    }

    /// <summary><paramref name="filtersText"/> with the always-applied block
    /// at its end, when it doesn't have one yet: a new backup's text.</summary>
    public static string WithAlwaysBlock(string? filtersText)
    {
        var text = filtersText ?? "";
        if (AlwaysExcludedPatterns.Count == 0 || FindAlwaysBlock(text.Replace("\r\n", "\n").Split('\n')) is not null) return text;
        var body = text.TrimEnd('\r', '\n', ' ', '\t');
        return (body.Length == 0 ? "" : body + "\n\n") + AlwaysBlockText() + "\n";
    }

    // ---------------------------------------------------------------
    // Rules written before U151
    // ---------------------------------------------------------------

    // Generic names that earlier lists matched at any depth and that now sit
    // under the profiles' AppData.
    private static readonly HashSet<string> ProfileGeneric = new(StringComparer.OrdinalIgnoreCase)
        { "InternalCache/", "icon_cache/", "compile-cache/", "Logs/CrashReporter/" };

    /// <summary>A rule of a recommended block written before U151, as the
    /// restic rule with the meaning approved on 6 November 2026: <c>/X</c> and
    /// <c>Users/*/…</c> from the top of the drive, four generic cache names
    /// under the profiles' AppData, the app aliases in each profile; anything
    /// else unchanged. A literal <c>$</c> becomes <c>$$</c>.</summary>
    internal static string LegacyToRestic(string rule, bool windows = true)
    {
        var glob = rule.Trim();
        string restic;
        if (!windows) restic = glob;
        else if (glob.StartsWith('/')) restic = "?:" + glob;
        else if (glob.StartsWith("Users/", StringComparison.OrdinalIgnoreCase)) restic = "?:/" + glob;
        else if (ProfileGeneric.Contains(glob)) restic = "?:/Users/*/AppData/**/" + glob;
        else if (glob.Equals("AppData/Local/Microsoft/WindowsApps/", StringComparison.OrdinalIgnoreCase)) restic = "?:/Users/*/" + glob;
        else restic = glob;
        return restic.Replace("$", "$$");
    }

    /// <summary>Every rule of Backspace's recommended lists before U151,
    /// including those already retired then.</summary>
    private static readonly string[] LegacyRules =
    {
        "/pagefile.sys", "/hiberfil.sys", "/swapfile.sys", "/DumpStack.log.tmp", "/System Volume Information/",
        "/$RECYCLE.BIN/", "/Config.Msi/", "/$WinREAgent/", "/$GetCurrent/", "/$SysReset/", "/$Windows.~BT/",
        "/$Windows.~WS/", "/Windows10Upgrade/", "/Windows.old/", "/bootmgr", "/BOOTNXT", "/BOOTSECT.BAK",
        "/OneDriveTemp/", "/PerfLogs/",
        "/Windows/", "/MSOCache/", "thumbs.db", "IconCache.db", "Temporary Internet Files/",
        "/ProgramData/Microsoft/Search/", "/ProgramData/Microsoft/Windows/WER/", "/ProgramData/Microsoft/Windows Defender/",
        "/ProgramData/Package Cache/", "/Program Files/desktop.ini", "/Program Files (x86)/desktop.ini",
        "/Users/Default/", "/Drivers/", "/SWSetup/", "/NVIDIA/", "/AMD/", "/Intel/",
        "Users/*/AppData/Local/Temp/", "*.crdownload", "Logs/CrashReporter/", "ShaderCache/", "GPUCache/",
        "InternalCache/", "icon_cache/", ".dropbox.cache/", ".tmp.drivedownload/", "node_modules/", "npm-cache/",
        "compile-cache/", "__pycache__/", "Users/*/.nuget/packages/", "Users/*/.gradle/caches/",
        "Users/*/AppData/Local/pip/cache/", "Users/*/AppData/Local/go-build/", "Users/*/go/pkg/mod/", ".DS_Store",
        "Users/*/AppData/**/Cache/", "Users/*/AppData/**/Caches/", "Users/*/AppData/**/Code Cache/",
        "Users/*/AppData/**/GrShaderCache/", "Users/*/AppData/**/DawnCache/", "Users/*/AppData/**/DawnGraphiteCache/",
        "Users/*/AppData/**/DawnWebGPUCache/", "Users/*/AppData/**/Service Worker/CacheStorage/",
        "Users/*/AppData/**/Service Worker/ScriptCache/", "Users/*/AppData/Local/CrashDumps/",
        "Users/*/AppData/Local/D3DSCache/", "Users/*/AppData/Local/Microsoft/Windows/INetCache/",
        "Users/*/AppData/Local/Microsoft/Windows/Explorer/", "Users/*/AppData/Local/Microsoft/Windows/WER/",
        "Users/*/AppData/Local/Package Cache/", "tdata/user_data/media_cache/",
        "Users/*/AppData/Local/Packages/*/AC/", "Users/*/AppData/Local/Packages/*/TempState/",
        "Users/*/AppData/*/Microsoft/Windows/Recent/", "Users/*/AppData/*/Microsoft/Windows/Notifications/",
        // Retired before U151 (U055, U065, U106).
        "DumpStack.log.tmp", "System Volume Information/", "$RECYCLE.BIN/", "Recovery/", "Config.Msi/",
        "$WinREAgent/", "$GetCurrent/", "$SysReset/", "$Windows.~BT/", "$Windows.~WS/",
        "Windows10Upgrade/", "Windows.old/", "PerfLogs/",
        "Users/*/AppData/*/Cache/", "Users/*/AppData/*/Caches/", "Users/*/AppData/Local/Packages/",
        "Users/*/AppData/*/Microsoft/Edge/", "OneDriveTemp/",
        "/Recovery/", "ntuser.dat*", "UsrClass.dat*",
        "temp/", "tmp/", "*.tmp", "*.temp", "*.dmp", "*.dump",
        "Users/*/AppData/*/Log/", "Users/*/AppData/*/Logs/",
        "Users/*/AppData/Local/Packages/*/LocalCache/", "Users/*/MicrosoftEdgeBackups/",
    };

    /// <summary>Patterns earlier versions recommended and these lists no longer
    /// contain, as written then and as converted (U151). Only these are
    /// offered for removal; any other rule in the block is the person's own.</summary>
    internal static readonly IReadOnlyList<string> RetiredPatterns = BuildRetired();

    private static IReadOnlyList<string> BuildRetired()
    {
        var current = new HashSet<string>(EssentialPatterns.Concat(WindowsSystemPatterns).Concat(CachesAndJunkPatterns), StringComparer.OrdinalIgnoreCase);
        return LegacyRules.Concat(LegacyRules.Select(r => LegacyToRestic(r)))
            .Where(r => !current.Contains(r))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

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

    // ---------------------------------------------------------------
    // Filter-text manipulation
    // ---------------------------------------------------------------

    /// <summary>
    /// Renders the chosen recommendation groups into a single text block
    /// bracketed by <see cref="AutoBlockHeader"/> / <see cref="AutoBlockFooter"/>.
    /// Cloud folders are written from the top of the drive (<see cref="CloudLine"/>).
    /// </summary>
    public static string RenderBlock(
        bool includeWindowsSystem,
        bool includeCachesAndJunk,
        IReadOnlyCollection<CloudSyncFolder> cloudExcludes,
        IReadOnlyCollection<string>? sources = null)
    {
        // Only rules that can match something in this backup's folders (U148).
        void Group(StringBuilder sb, string heading, IEnumerable<string> patterns)
        {
            sb.AppendLine(heading);
            var kept = patterns.Where(p => AppliesTo(p, sources)).ToList();
            if (kept.Count == 0) sb.AppendLine(NoneApplyNote);
            foreach (var p in kept) sb.AppendLine(p);
            sb.AppendLine();
        }
        var sb = new StringBuilder();
        sb.AppendLine(AutoBlockHeader);
        sb.AppendLine("# Generated by Backspace. Edit if you like: this whole block");
        sb.AppendLine("# will be replaced (preserving your edits outside the markers)");
        sb.AppendLine("# the next time you click \"Apply recommended exclusions\".");
        sb.AppendLine();

        Group(sb, EssentialsHeading, EssentialPatterns);
        if (includeWindowsSystem) Group(sb, WindowsHeading, WindowsSystemPatterns);
        if (includeCachesAndJunk) Group(sb, CachesHeading, CachesAndJunkPatterns);

        if (cloudExcludes.Count > 0)
        {
            sb.AppendLine(CloudHeading);
            foreach (var c in cloudExcludes)
            {
                if (CloudLine(c) is not { } line) continue;
                sb.AppendLine($"# {c.Label} at {c.Path}");
                sb.AppendLine(line);
            }
            sb.AppendLine();
        }

        sb.AppendLine(AutoBlockFooter);
        return sb.ToString();
    }

    // ---------------------------------------------------------------
    // Imported filters and later list changes (U056, U057)
    // ---------------------------------------------------------------

    private const string EssentialsHeading = "# Essentials: always excluded; safe for any source.";
    private const string WindowsHeading = "# Windows system files: locked, regenerable, or restore-unsafe.";
    private const string CachesHeading = "# App caches, temp files, dev junk: universally safe to skip.";
    private const string CloudHeading = "# Cloud-synced folders: already replicated in their providers' clouds.";

    /// <summary>Written under a group none of whose rules can match in this
    /// backup's folders, so the group stays chosen.</summary>
    internal const string NoneApplyNote = "# None of these apply to this backup's folders.";

    /// <summary>Whether a rule can match anything in these sources (U148, U151).
    /// A rule from the top of the drive (<c>?:/Windows/</c>) or of the file
    /// system (<c>/home/*/…</c>) applies to a folder on its way or inside it:
    /// <c>?:/Users/*/AppData/Local/Temp/</c> applies to C:\, C:\Users\me and
    /// C:\Users\me\AppData, not to C:\Users\me\Dropbox, nor to a folder it
    /// would leave out whole (C:\Users\me\AppData\Local\Temp\x). Any other rule can
    /// match at any depth, so it always applies. No sources: every rule
    /// applies.</summary>
    internal static bool AppliesTo(string pattern, IReadOnlyCollection<string>? sources)
    {
        var real = sources?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (real is null || real.Count == 0) return true;
        var p = pattern.Trim();
        if (p.StartsWith('!')) p = p[1..];
        p = p.Replace("$$", "$");
        bool drive;
        if (p.StartsWith("?:/", StringComparison.Ordinal)) { drive = true; p = p[3..]; }
        else if (p.StartsWith('/')) { drive = false; p = p[1..]; }
        else return true;
        var ruleParts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var source in real)
        {
            var path = source.Trim().Replace('\\', '/');
            var isDrive = path.Length >= 2 && path[1] == ':';
            if (isDrive != drive) continue;
            var sourceParts = (isDrive ? path[2..] : path).Split('/', StringSplitOptions.RemoveEmptyEntries);
            var fits = true;
            var deep = false;
            for (var i = 0; i < Math.Min(ruleParts.Length, sourceParts.Length) && fits; i++)
            {
                if (ruleParts[i] == "**") { deep = true; break; }
                fits = NameMatches(ruleParts[i], sourceParts[i]);
            }
            // A rule that would leave out the whole folder (a backup of a
            // folder inside a profile's Temp, say) is not offered for it.
            var wholeSource = fits && !deep && ruleParts.Length <= sourceParts.Length;
            if (fits && !wholeSource) return true;
        }
        return false;
    }

    private static bool NameMatches(string glob, string name)
    {
        try
        {
            var options = System.Text.RegularExpressions.RegexOptions.CultureInvariant
                | (FilterTranslator.IgnoresCase ? System.Text.RegularExpressions.RegexOptions.IgnoreCase : 0);
            return System.Text.RegularExpressions.Regex.IsMatch(name, FilterTranslator.GlobToRegex("/" + glob), options);
        }
        catch (ArgumentException) { return true; }
    }

    private static readonly (string Heading, IReadOnlyList<string> Patterns)[] Groups =
    {
        (EssentialsHeading, EssentialPatterns),
        (WindowsHeading, WindowsSystemPatterns),
        (CachesHeading, CachesAndJunkPatterns),
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

    private sealed record SavedBlock(int Start, int End, List<(string? Heading, string Line)> Lines);

    private static SavedBlock? FindBlock(string[] lines)
    {
        var start = Array.FindIndex(lines, l => l.Trim() == AutoBlockHeader);
        if (start < 0) return null;
        var end = Array.FindIndex(lines, start + 1, l => l.Trim() == AutoBlockFooter);
        if (end < 0) return null;
        var parsed = new List<(string?, string)>();
        string? heading = null;
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
        return new(start, end, parsed);
    }

    /// <summary>Patterns to add (missing from a group the block uses) and to
    /// remove (no longer in any recommended list), minus ignored ones.</summary>
    /// <param name="sources">The backup's folders: rules that can't match in
    /// them are not added, and recommended ones already there are offered for
    /// removal (U148). Null: every rule applies.</param>
    public static ListUpdate CheckForUpdates(string? filters, IReadOnlyCollection<string>? ignored = null, IReadOnlyCollection<string>? sources = null)
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
        var known = new HashSet<string>(Groups.SelectMany(g => g.Patterns), StringComparer.OrdinalIgnoreCase);
        var add = used.SelectMany(g => g.Patterns).Where(p => !present.Contains(p) && !skip.Contains(p) && AppliesTo(p, sources)).Distinct().ToList();
        var remove = patterns.Where(p => (retired.Contains(p) || known.Contains(p) && !AppliesTo(p, sources)) && !skip.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new(add, remove);
    }

    /// <summary>The editor's ticks as the saved recommended block has them, or
    /// null when the filters hold no recommended block.</summary>
    public sealed record SavedChoice(bool WindowsSystem, bool CachesAndJunk, IReadOnlyCollection<string> CloudLines);

    public static SavedChoice? ReadChoice(string? filters)
    {
        var block = FindBlock((filters ?? "").Replace("\r\n", "\n").Split('\n'));
        if (block is null) return null;
        var cloud = block.Lines.Where(l => l.Heading == CloudHeading && !l.Line.StartsWith('#')).Select(l => l.Line).ToList();
        return new(block.Lines.Any(l => l.Heading == WindowsHeading), block.Lines.Any(l => l.Heading == CachesHeading), cloud);
    }

    /// <summary>The rule that excludes <paramref name="folder"/>: its full
    /// path from the top of the drive (<c>?:/Users/me/OneDrive/</c>), or of the
    /// file system on Linux and macOS.</summary>
    public static string? CloudLine(CloudSyncFolder folder)
    {
        if (ToSourceRelativePath(folder.Path) is null) return null;
        var path = folder.Path.Trim().Replace('\\', '/').TrimEnd('/');
        var rule = path.Length >= 2 && path[1] == ':'
            ? "?:" + DuplicacyFilterConverter.EscapeNames(path[2..])
            : DuplicacyFilterConverter.EscapeNames(path);
        return (rule + "/").Replace("$", "$$");
    }

    /// <summary>True when applying these ticks would add or remove no rule:
    /// every ticked group is complete in the saved block, no unticked group
    /// has rules there, and the cloud folders match (U105).</summary>
    public static bool IsApplied(string? filters, bool includeWindowsSystem, bool includeCachesAndJunk,
        IReadOnlyCollection<CloudSyncFolder> cloudExcludes, IReadOnlyCollection<string>? sources = null)
    {
        var block = FindBlock((filters ?? "").Replace("\r\n", "\n").Split('\n'));
        if (block is null) return false;
        var present = new HashSet<string>(block.Lines
            .Where(l => !l.Line.StartsWith('#') && l.Heading != CloudHeading).Select(l => l.Line), StringComparer.OrdinalIgnoreCase);
        var wanted = new HashSet<string>(EssentialPatterns
            .Concat(includeWindowsSystem ? WindowsSystemPatterns : Array.Empty<string>())
            .Concat(includeCachesAndJunk ? CachesAndJunkPatterns : Array.Empty<string>())
            .Where(p => AppliesTo(p, sources)), StringComparer.OrdinalIgnoreCase);
        var known = Groups.SelectMany(g => g.Patterns);
        if (!wanted.All(present.Contains) || known.Any(p => present.Contains(p) && !wanted.Contains(p))) return false;
        var cloud = block.Lines.Where(l => l.Heading == CloudHeading && !l.Line.StartsWith('#')).Select(l => l.Line);
        return new HashSet<string>(cloud, StringComparer.OrdinalIgnoreCase)
            .SetEquals(cloudExcludes.Select(CloudLine).OfType<string>());
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
        var rendered = RenderBlock(patterns, cloud)
            .TrimEnd('\r', '\n').Replace("\r\n", "\n").Split('\n').ToList();
        if (own.Count > 0) rendered.InsertRange(rendered.Count - 1, new[] { "# Your own rules kept from this block." }.Concat(own).Append(""));
        var result = lines.Take(block.Start).Concat(rendered).Concat(lines.Skip(block.End + 1));
        return string.Join("\n", result);
    }

    /// <summary>
    /// Splits imported Duplicacy filters into the rules that are exactly
    /// one of Backspace's recommended patterns (counted as recommended) and
    /// the rest, kept as the person's own rules (U056). A line counts only
    /// when every rule it converts to, for these sources, is a recommended
    /// one; anything else, including includes, stays as written.
    /// </summary>
    internal static (string Custom, IReadOnlyList<string> Recommended) RecognizeImported(string duplicacyFilters, IReadOnlyCollection<string>? sources = null)
    {
        var byKey = Groups.SelectMany(g => g.Patterns)
            .GroupBy(Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var prefixes = DuplicacyFilterConverter.Prefixes(sources);
        var recognized = new List<string>();
        var custom = new List<string>();
        foreach (var raw in duplicacyFilters.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("e:(?i)", StringComparison.Ordinal)
                && DuplicacyFilterTranslator.ToGlobs(line[6..]) is { Count: > 0 } globs)
            {
                var rules = globs.SelectMany(g => DuplicacyFilterConverter.Anchor(g.Glob.EndsWith('/') ? g.Glob + "*" : g.Glob, prefixes)).ToList();
                if (rules.All(r => byKey.ContainsKey(Key(r))))
                {
                    foreach (var r in rules) if (!recognized.Contains(byKey[Key(r)])) recognized.Add(byKey[Key(r)]);
                    continue;
                }
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
    /// block, and the always-applied block stays last:
    ///
    ///   * A pair of <see cref="CustomBlockHeader"/> /
    ///     <see cref="CustomBlockFooter"/> marker lines is emitted at the
    ///     top. Any existing custom-block content is preserved between
    ///     them; any "loose" rules the user wrote outside the marker
    ///     pairs are moved INTO the custom block on first re-apply.
    ///   * The recommended block (passed in via
    ///     <paramref name="newBlock"/>) follows, replacing any previous
    ///     recommended-block content.
    ///   * The always-applied block comes last, as it was, or freshly
    ///     written when the text had none.
    ///
    /// The merge is idempotent: re-applying the same arguments leaves
    /// the result unchanged.
    /// </summary>
    public static string MergeIntoFilters(string? existingFilters, string newBlock)
    {
        var lines = (existingFilters ?? "").Replace("\r\n", "\n").Split('\n').ToList();
        var alwaysLines = new List<string>(AlwaysBlockLines());
        if (FindAlwaysBlock(lines) is { } always)
        {
            alwaysLines = lines.GetRange(always.Start, always.End - always.Start + 1);
            lines.RemoveRange(always.Start, always.End - always.Start + 1);
        }

        // 1. Extract any existing recommended-block content so we can
        //    drop it (newBlock supersedes it).
        var withoutRecommended = StripBlock(string.Join("\n", lines), AutoBlockHeader, AutoBlockFooter);

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
        if (alwaysLines.Count > 0)
        {
            if (!newBlock.EndsWith('\n')) sb.AppendLine();
            sb.AppendLine();
            foreach (var line in alwaysLines) sb.AppendLine(line);
        }
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
    /// Converts an absolute path like <c>C:\Users\me\OneDrive</c> to its
    /// part below the drive or share: <c>Users/me/OneDrive</c>. Returns
    /// null for a drive or share root.
    /// </summary>
    internal static string? ToSourceRelativePath(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return null;
        var trimmed = absolutePath.TrimEnd('/', '\\');

        // UNC path: \\server\share\Sub\Path → strip the server+share so
        // the result is the tail "Sub/Path".
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

        // Glob characters are escaped by the caller (CloudLine).
        return trimmed;
    }
}
