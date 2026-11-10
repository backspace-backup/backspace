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
/// Three groups, each ticked or not per backup (U165, 9 November 2026):
///   • <see cref="EssentialPatterns"/> — files no one needs in a backup: the
///     page, swap and hibernation files, System Volume Information, the
///     Recycle Bin, Windows setup and upgrade leftovers, boot files, and the
///     app execution aliases restic can't store. Ticked by default.
///   • <see cref="WindowsSystemPatterns"/> — the Windows folder and the data
///     Windows rebuilds (search index, installer caches, driver unpack folders).
///   • <see cref="CachesAndJunkPatterns"/> — caches, temp folders and
///     regenerable app data. Safe for the vast majority of people.
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

    // ---------------------------------------------------------------
    // Pattern groups (Windows forms; see ForThisPlatform)
    // ---------------------------------------------------------------

    /// <summary>
    /// Only what no restore can use (U165, 9 November 2026), ticked by
    /// default and offered for every backup like the other groups. Every
    /// rule is tied to the top of a drive, so a folder a person made deeper
    /// down with the same name is never matched.
    /// </summary>
    private static readonly string[] WindowsEssentials =
    {
        // Rebuilt by Windows at every start: huge, locked, useless restored.
        // DumpStack.log.tmp is the crash-dump driver's live log.
        "?:/pagefile.sys",
        "?:/hiberfil.sys",
        "?:/swapfile.sys",
        "?:/DumpStack.log.tmp",

        // Shadow copies and restore points, and deleted files.
        "?:/System Volume Information/",
        "?:/$$RECYCLE.BIN/",

        // Temporary files of Windows Update, upgrades, resets and installers,
        // and the previous installation an upgrade leaves for ten days: its
        // files can't roll Windows back from a file backup. Recovery itself
        // is not listed (U106): it can hold a manufacturer's recovery files.
        "?:/Config.Msi/",
        "?:/$$WinREAgent/",
        "?:/$$GetCurrent/",
        "?:/$$SysReset/",
        "?:/$$Windows.~BT/",
        "?:/$$Windows.~WS/",
        "?:/Windows10Upgrade/",
        "?:/Windows.old/",
        "?:/PerfLogs/",

        // Boot files: a file backup can't make a disk bootable, and restoring
        // them would clobber a fresh install's loader.
        "?:/bootmgr",
        "?:/BOOTNXT",
        "?:/BOOTSECT.BAK",

        // OneDrive's download scratch, which it creates at the top of a drive.
        "?:/OneDriveTemp/",

        // Windows' app execution aliases (wt.exe, winget.exe…): links restic
        // can't store. Without this rule each one is an "unsupported file
        // type" error and the run ends as a partial backup (local_c: 92
        // errors, 4 November 2026).
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

    /// <summary>The "Essentials" group, first in the block. Empty on Linux and
    /// macOS, which have none of these files.</summary>
    public static readonly IReadOnlyList<string> EssentialPatterns = ForThisPlatform(WindowsEssentials);

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

    /// <summary>The three tickable groups, in the block's order.</summary>
    public enum Group { Essentials, WindowsSystem, CachesAndJunk }

    /// <summary>The rules of <paramref name="group"/>.</summary>
    public static IReadOnlyList<string> PatternsOf(Group group) => group switch
    {
        Group.Essentials => EssentialPatterns,
        Group.WindowsSystem => WindowsSystemPatterns,
        _ => CachesAndJunkPatterns,
    };

    /// <summary>The group's name as the editor shows it.</summary>
    public static string NameOf(Group group) => group switch
    {
        Group.Essentials => "Essentials",
        Group.WindowsSystem => "Windows system files",
        _ => "App caches and temporary files",
    };

    /// <summary>The group a recommended rule belongs to, or null for a rule
    /// in no current list.</summary>
    public static Group? GroupOf(string pattern)
    {
        foreach (var g in Enum.GetValues<Group>())
            if (PatternsOf(g).Contains(pattern, StringComparer.OrdinalIgnoreCase)) return g;
        return null;
    }

    /// <summary>The rules Apply writes for <paramref name="group"/> in this
    /// backup, in the block's order: those that can match in its folders,
    /// without the ones the person hid. Shown under each group in the editor.</summary>
    public static IReadOnlyList<string> RulesFor(Group group, IReadOnlyCollection<string>? sources, IReadOnlyCollection<string>? hidden = null)
    {
        var skip = new HashSet<string>(hidden ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        return InBlockOrder(PatternsOf(group).Where(p => !skip.Contains(p) && AppliesTo(p, sources))).ToList();
    }

    /// <summary>The rules of a backup without its own text (a folder's size
    /// before any backup exists): the Essentials, ticked by default.</summary>
    internal static string EssentialsText() => string.Join("\n", EssentialPatterns);

    // ---------------------------------------------------------------
    // Texts written before U165 (9 November 2026)
    // ---------------------------------------------------------------

    /// <summary>Until U165 every text ended with this block, which ran to the
    /// next blank line, and the recommended block's first group had
    /// <see cref="LegacyEssentialsHeading"/>.</summary>
    internal const string LegacyAlwaysBlockHeader = "# === Backspace essential exclusions (always applied) ===";
    internal const string LegacyEssentialsHeading = "# Essentials: always excluded; safe for any source.";

    /// <summary>What the always-applied block held, for Duplicacy filters
    /// converted now: they were given that block (U151).</summary>
    private static readonly IReadOnlyList<string> LegacyAlwaysRules = ForThisPlatform(new[]
    {
        "?:/pagefile.sys", "?:/hiberfil.sys", "?:/swapfile.sys", "?:/DumpStack.log.tmp",
        "?:/System Volume Information/", "?:/Config.Msi/", "?:/$$WinREAgent/", "?:/$$GetCurrent/",
        "?:/$$SysReset/", "?:/$$Windows.~BT/", "?:/$$Windows.~WS/", "?:/Windows10Upgrade/",
        "?:/bootmgr", "?:/BOOTNXT", "?:/BOOTSECT.BAK", "?:/OneDriveTemp/",
        "?:/Users/*/AppData/Local/Microsoft/WindowsApps/",
    });

    /// <summary>True when <paramref name="text"/> was written before U165.</summary>
    internal static bool HasLegacyEssentials(string? text)
        => (text ?? "").Split('\n').Any(l => l.Trim() is LegacyAlwaysBlockHeader or LegacyEssentialsHeading);

    /// <summary>
    /// A text written before U165 in today's form, excluding the same files:
    /// the always-applied block is removed and its rules join the Essentials
    /// group of the recommended block, which gets today's heading. A text
    /// without a recommended block gets one holding only that group. Rules
    /// that can match nothing in <paramref name="sources"/> are left out
    /// (the old block was the same for every backup), so the group offers no
    /// removals. Any other text is returned unchanged.
    /// </summary>
    /// <param name="formerlyAlways">For Duplicacy filters converted now: they
    /// get the Essentials the always-applied block would have given them.</param>
    internal static string UpgradeEssentials(string text, IReadOnlyCollection<string>? sources, bool formerlyAlways = false)
    {
        if (!formerlyAlways && !HasLegacyEssentials(text)) return text;
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var essentials = new List<string>(formerlyAlways ? LegacyAlwaysRules : Array.Empty<string>());

        var always = lines.FindIndex(l => l.Trim() == LegacyAlwaysBlockHeader);
        if (always >= 0)
        {
            var end = always;
            while (end + 1 < lines.Count && lines[end + 1].Trim().Length > 0) end++;
            essentials.AddRange(lines.Skip(always + 1).Take(end - always).Select(l => l.Trim()).Where(l => !l.StartsWith('#')));
            var start = always > 0 && lines[always - 1].Trim().Length == 0 ? always - 1 : always;
            lines.RemoveRange(start, end - start + 1);
        }

        List<string> EssentialsGroup()
        {
            var kept = InBlockOrder(essentials.Distinct(StringComparer.OrdinalIgnoreCase).Where(p => CanMatch(p, sources))).ToList();
            return kept.Count == 0 ? new List<string> { EssentialsHeading, NoneApplyNote } : kept.Prepend(EssentialsHeading).ToList();
        }

        var header = lines.FindIndex(l => l.Trim() == AutoBlockHeader);
        var footer = header < 0 ? -1 : lines.FindIndex(header + 1, l => l.Trim() == AutoBlockFooter);
        if (footer < 0)
        {
            if (essentials.Count == 0) return string.Join("\n", lines);
            var body = string.Join("\n", lines).TrimEnd('\n', ' ', '\t');
            var block = BlockIntro.Prepend(AutoBlockHeader).Append("").Concat(EssentialsGroup()).Append("").Append(AutoBlockFooter);
            return (body.Length == 0 ? "" : body + "\n\n") + string.Join("\n", block) + "\n";
        }

        // The old Essentials group: its heading, then rules up to a blank line.
        var heading = lines.FindIndex(header, footer - header, l => l.Trim() is LegacyEssentialsHeading or EssentialsHeading);
        int groupStart, groupEnd;
        if (heading >= 0)
        {
            groupStart = heading;
            groupEnd = heading;
            while (groupEnd + 1 < footer && lines[groupEnd + 1].Trim().Length > 0) groupEnd++;
            essentials.AddRange(lines.Skip(heading + 1).Take(groupEnd - heading).Select(l => l.Trim())
                .Where(l => !l.StartsWith('#')));
        }
        else
        {
            // No Essentials group yet (an imported block): it goes first.
            groupStart = lines.FindIndex(header + 1, footer - header - 1, l => IsHeading(l.Trim()));
            if (groupStart < 0) groupStart = footer;
            groupEnd = groupStart - 1;
        }
        var group = EssentialsGroup();
        if (heading < 0) group.Add("");
        lines.RemoveRange(groupStart, groupEnd - groupStart + 1);
        lines.InsertRange(groupStart, group);
        return string.Join("\n", lines);
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

    /// <summary>The comment lines under <see cref="AutoBlockHeader"/>.</summary>
    private static readonly string[] BlockIntro =
    {
        "# Generated by Backspace. Edit if you like: this whole block",
        "# will be replaced (preserving your edits outside the markers)",
        "# the next time you click \"Apply recommended exclusions\".",
    };

    /// <summary>
    /// Renders the chosen recommendation groups into a single text block
    /// bracketed by <see cref="AutoBlockHeader"/> / <see cref="AutoBlockFooter"/>.
    /// Cloud folders are written from the top of the drive (<see cref="CloudLine"/>).
    /// </summary>
    /// <param name="hidden">Suggestions the person hid for this backup (U165):
    /// never written, so Apply doesn't bring them back.</param>
    /// <param name="kept">Rules of the saved block the person chose to keep
    /// when Backspace suggested removing them: written again.</param>
    public static string RenderBlock(
        bool includeWindowsSystem,
        bool includeCachesAndJunk,
        IReadOnlyCollection<CloudSyncFolder> cloudExcludes,
        IReadOnlyCollection<string>? sources = null,
        bool includeEssentials = true,
        IReadOnlyCollection<string>? hidden = null,
        IReadOnlyCollection<string>? kept = null)
    {
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Only rules that can match something in this backup's folders (U148).
        void WriteGroup(StringBuilder sb, Group group)
        {
            sb.AppendLine(HeadingOf(group));
            var rules = RulesFor(group, sources, hidden);
            if (rules.Count == 0) sb.AppendLine(NoneApplyNote);
            foreach (var p in rules) { sb.AppendLine(p); written.Add(p); }
            sb.AppendLine();
        }
        var sb = new StringBuilder();
        sb.AppendLine(AutoBlockHeader);
        foreach (var line in BlockIntro) sb.AppendLine(line);
        sb.AppendLine();

        if (includeEssentials) WriteGroup(sb, Group.Essentials);
        if (includeWindowsSystem) WriteGroup(sb, Group.WindowsSystem);
        if (includeCachesAndJunk) WriteGroup(sb, Group.CachesAndJunk);

        // A kept rule stays only while its group is ticked, or when no list has it.
        var keep = (kept ?? Array.Empty<string>())
            .Where(p => !written.Contains(p) && GroupOf(p) switch
            {
                Group.Essentials => includeEssentials,
                Group.WindowsSystem => includeWindowsSystem,
                Group.CachesAndJunk => includeCachesAndJunk,
                _ => true,
            }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (keep.Count > 0)
        {
            sb.AppendLine(OwnHeading);
            foreach (var p in keep) sb.AppendLine(p);
            sb.AppendLine();
        }

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

    // The saved block is read back by these headings: changing one needs a
    // conversion of saved texts (as U165 did for the Essentials).
    internal const string EssentialsHeading = "# Essentials: files no one needs in a backup.";
    private const string WindowsHeading = "# Windows system files: locked, regenerable, or restore-unsafe.";
    private const string CachesHeading = "# App caches, temp files, dev junk: universally safe to skip.";
    private const string CloudHeading = "# Cloud-synced folders: already replicated in their providers' clouds.";
    /// <summary>Heads rules of the block that are in no ticked list: kept by
    /// the person when a removal was suggested, or written into the block.</summary>
    internal const string OwnHeading = "# Your own rules kept from this block.";

    private static string HeadingOf(Group group) => group switch
    {
        Group.Essentials => EssentialsHeading,
        Group.WindowsSystem => WindowsHeading,
        _ => CachesHeading,
    };

    private static bool IsHeading(string line)
        => line is EssentialsHeading or WindowsHeading or CachesHeading or CloudHeading or OwnHeading;

    /// <summary>Written under a group none of whose rules can match in this
    /// backup's folders, so the group stays chosen.</summary>
    internal const string NoneApplyNote = "# None of these apply to this backup's folders.";

    /// <summary>A group's rules in the order the block lists them (U165):
    /// rules from the top of the drive (or of the file system) first, then
    /// rules that match at any depth, each sorted by path ignoring case, so
    /// related rules sit together. Every recommended rule leaves something
    /// out, so their order changes nothing in what is backed up.</summary>
    internal static IEnumerable<string> InBlockOrder(IEnumerable<string> patterns)
        => patterns.OrderBy(SortKey, StringComparer.Ordinal);

    private static string SortKey(string pattern)
    {
        var p = pattern.Trim().TrimStart('!').Replace("$$", "$");
        var anchored = p.StartsWith("?:/", StringComparison.Ordinal) || p.StartsWith('/');
        if (p.StartsWith("?:", StringComparison.Ordinal)) p = p[2..];
        return (anchored ? "0" : "1") + p.ToLowerInvariant();
    }

    /// <summary>Whether a rule can match anything in these sources (U148, U151).
    /// A rule from the top of the drive (<c>?:/Windows/</c>) or of the file
    /// system (<c>/home/*/…</c>) applies to a folder on its way or inside it:
    /// <c>?:/Users/*/AppData/Local/Temp/</c> applies to C:\, C:\Users\me and
    /// C:\Users\me\AppData, not to C:\Users\me\Dropbox, nor to a folder it
    /// would leave out whole (C:\Users\me\AppData\Local\Temp\x). Any other rule can
    /// match at any depth, so it always applies. No sources: every rule
    /// applies.</summary>
    internal static bool AppliesTo(string pattern, IReadOnlyCollection<string>? sources)
        => Fits(pattern, sources, wholeSourceCounts: false);

    /// <summary>Like <see cref="AppliesTo"/>, but a rule that leaves out a
    /// whole backed-up folder counts too: false only when the rule can match
    /// nothing in these sources, so leaving it out changes nothing.</summary>
    internal static bool CanMatch(string pattern, IReadOnlyCollection<string>? sources)
        => Fits(pattern, sources, wholeSourceCounts: true);

    private static bool Fits(string pattern, IReadOnlyCollection<string>? sources, bool wholeSourceCounts)
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
            if (fits && (wholeSourceCounts || !wholeSource)) return true;
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
    /// recommended rules the person already had, and to rewrite a saved block.</summary>
    /// <param name="ticked">Headings of groups written even with none of
    /// their rules present, under <see cref="NoneApplyNote"/>, so they stay ticked.</param>
    internal static string RenderBlock(IReadOnlyCollection<string> patterns, IReadOnlyList<string>? cloudLines = null,
        IReadOnlyCollection<string>? ticked = null)
    {
        var chosen = new HashSet<string>(patterns, StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        sb.AppendLine(AutoBlockHeader);
        foreach (var line in BlockIntro) sb.AppendLine(line);
        sb.AppendLine();
        foreach (var (heading, list) in Groups)
        {
            var present = InBlockOrder(list.Where(chosen.Contains)).ToList();
            if (present.Count == 0 && ticked?.Contains(heading) != true) continue;
            sb.AppendLine(heading);
            if (present.Count == 0) sb.AppendLine(NoneApplyNote);
            foreach (var p in present) sb.AppendLine(p);
            sb.AppendLine();
        }
        // Rules the block holds that are in no list stay too.
        var known = new HashSet<string>(Groups.SelectMany(g => g.Patterns), StringComparer.OrdinalIgnoreCase);
        var own = patterns.Where(p => !known.Contains(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (own.Count > 0)
        {
            sb.AppendLine(OwnHeading);
            foreach (var p in own) sb.AppendLine(p);
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
                if (IsHeading(line)) heading = line;
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
        var used = Groups.Where(g => block.Lines.Any(l => l.Heading == g.Heading)).ToList();
        var known = new HashSet<string>(Groups.SelectMany(g => g.Patterns), StringComparer.OrdinalIgnoreCase);
        var add = used.SelectMany(g => g.Patterns).Where(p => !present.Contains(p) && !skip.Contains(p) && AppliesTo(p, sources)).Distinct().ToList();
        var remove = patterns.Where(p => (retired.Contains(p) || known.Contains(p) && !AppliesTo(p, sources)) && !skip.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new(add, remove);
    }

    /// <summary>The editor's ticks as the saved recommended block has them, or
    /// null when the filters hold no recommended block.</summary>
    public sealed record SavedChoice(bool Essentials, bool WindowsSystem, bool CachesAndJunk, IReadOnlyCollection<string> CloudLines);

    public static SavedChoice? ReadChoice(string? filters)
    {
        var block = FindBlock((filters ?? "").Replace("\r\n", "\n").Split('\n'));
        if (block is null) return null;
        var cloud = block.Lines.Where(l => l.Heading == CloudHeading && !l.Line.StartsWith('#')).Select(l => l.Line).ToList();
        return new(block.Lines.Any(l => l.Heading == EssentialsHeading), block.Lines.Any(l => l.Heading == WindowsHeading),
            block.Lines.Any(l => l.Heading == CachesHeading), cloud);
    }

    /// <summary>The rules of the saved block, without its cloud folders.</summary>
    public static IReadOnlyList<string> BlockRules(string? filters)
    {
        var block = FindBlock((filters ?? "").Replace("\r\n", "\n").Split('\n'));
        return block is null ? Array.Empty<string>()
            : block.Lines.Where(l => !l.Line.StartsWith('#') && l.Heading != CloudHeading).Select(l => l.Line).ToList();
    }

    /// <summary>The rule that excludes <paramref name="folder"/>: its full
    /// path from the top of the drive (<c>?:/Users/me/OneDrive/</c>), or of the
    /// file system on Linux and macOS.</summary>
    public static string? CloudLine(CloudSyncFolder folder) => FolderLine(folder.Path);

    /// <summary>The rule that excludes the folder at <paramref name="fullPath"/>,
    /// written from the top of the drive (<c>?:/Users/me/x/</c>), or of the
    /// file system on Linux and macOS; glob characters in the names are
    /// escaped and <c>$</c> doubled. Null for a drive or share root.</summary>
    public static string? FolderLine(string fullPath)
    {
        if (ToSourceRelativePath(fullPath) is null) return null;
        var path = fullPath.Trim().Replace('\\', '/').TrimEnd('/');
        var rule = path.Length >= 2 && path[1] == ':'
            ? "?:" + DuplicacyFilterConverter.EscapeNames(path[2..])
            : DuplicacyFilterConverter.EscapeNames(path);
        return (rule + "/").Replace("$", "$$");
    }

    /// <summary>True when applying these ticks would add or remove no rule:
    /// every ticked group is complete in the saved block, no unticked group
    /// has rules there, and the cloud folders match (U105). Hidden
    /// suggestions count as done: Apply neither adds nor removes them.</summary>
    public static bool IsApplied(string? filters, bool includeWindowsSystem, bool includeCachesAndJunk,
        IReadOnlyCollection<CloudSyncFolder> cloudExcludes, IReadOnlyCollection<string>? sources = null,
        bool includeEssentials = true, IReadOnlyCollection<string>? hidden = null)
    {
        var block = FindBlock((filters ?? "").Replace("\r\n", "\n").Split('\n'));
        if (block is null) return false;
        if (block.Lines.Any(l => l.Heading == EssentialsHeading) != includeEssentials
            || block.Lines.Any(l => l.Heading == WindowsHeading) != includeWindowsSystem
            || block.Lines.Any(l => l.Heading == CachesHeading) != includeCachesAndJunk) return false;
        var skip = new HashSet<string>(hidden ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var present = new HashSet<string>(block.Lines
            .Where(l => !l.Line.StartsWith('#') && l.Heading != CloudHeading).Select(l => l.Line), StringComparer.OrdinalIgnoreCase);
        var wanted = new HashSet<string>(
            (includeEssentials ? RulesFor(Group.Essentials, sources, skip) : Array.Empty<string>())
            .Concat(includeWindowsSystem ? RulesFor(Group.WindowsSystem, sources, skip) : Array.Empty<string>())
            .Concat(includeCachesAndJunk ? RulesFor(Group.CachesAndJunk, sources, skip) : Array.Empty<string>()),
            StringComparer.OrdinalIgnoreCase);
        var known = Groups.SelectMany(g => g.Patterns);
        if (!wanted.All(present.Contains) || known.Any(p => present.Contains(p) && !wanted.Contains(p) && !skip.Contains(p))) return false;
        var cloud = block.Lines.Where(l => l.Heading == CloudHeading && !l.Line.StartsWith('#')).Select(l => l.Line);
        return new HashSet<string>(cloud, StringComparer.OrdinalIgnoreCase)
            .SetEquals(cloudExcludes.Select(CloudLine).OfType<string>());
    }

    /// <summary>Applies <see cref="CheckForUpdates"/> to the saved block,
    /// keeping its groups, its cloud-folder lines and every rule outside it.</summary>
    public static string ApplyUpdates(string filters, ListUpdate update)
    {
        var lines = filters.Replace("\r\n", "\n").Split('\n');
        var block = FindBlock(lines);
        if (block is null || update.IsEmpty) return filters;
        var removed = new HashSet<string>(update.Remove, StringComparer.OrdinalIgnoreCase);
        var kept = block.Lines.Where(l => !l.Line.StartsWith('#') && l.Heading != CloudHeading && !removed.Contains(l.Line)).Select(l => l.Line);
        var cloud = block.Lines.Where(l => l.Heading == CloudHeading && l.Line != CloudHeading).Select(l => l.Line).ToList();
        var ticked = block.Lines.Select(l => l.Heading).OfType<string>().ToHashSet();
        var rendered = RenderBlock(kept.Concat(update.Add).ToList(), cloud, ticked)
            .TrimEnd('\r', '\n').Replace("\r\n", "\n").Split('\n');
        var result = lines.Take(block.Start).Concat(rendered).Concat(lines.Skip(block.End + 1));
        return string.Join("\n", result);
    }

    // ---------------------------------------------------------------
    // Updated recommendations: applied automatically, or offered (U165)
    // ---------------------------------------------------------------

    /// <summary>The changes today's lists bring to this backup's ticked groups,
    /// minus the ones the person hid: the card's "New filter suggestions".</summary>
    public static ListUpdate PendingFor(Models.Backup backup)
        => CheckForUpdates(backup.FiltersText, backup.IgnoredRecommendationChanges, backup.SourcePaths);

    /// <summary>True when the backup's card offers a review: it doesn't take
    /// updated recommendations automatically, and some are pending.</summary>
    public static bool HasSuggestions(Models.Backup backup)
        => !backup.AutoApplyRecommendations && !PendingFor(backup).IsEmpty;

    /// <summary>
    /// Brings saved texts up to date when settings load (in the app and in the
    /// background service, which runs the saved settings): texts written before
    /// U165 get today's Essentials group (<see cref="UpgradeEssentials"/>), and
    /// a backup that takes Backspace's updated recommendations automatically
    /// gets the pending changes of its ticked groups, hidden ones excepted.
    /// Returns the names of the backups changed.
    /// </summary>
    internal static IReadOnlyList<string> UpdateStored(Models.AppConfig config)
    {
        var changed = new List<string>();
        foreach (var backup in config.Backups)
        {
            var text = UpgradeEssentials(backup.FiltersText ?? "", backup.SourcePaths);
            if (backup.AutoApplyRecommendations && CheckForUpdates(text, backup.IgnoredRecommendationChanges, backup.SourcePaths) is { IsEmpty: false } update)
                text = ApplyUpdates(text, update);
            if (text == (backup.FiltersText ?? "")) continue;
            backup.FiltersText = text;
            changed.Add(backup.Name);
        }
        return changed;
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
    /// block:
    ///
    ///   * A pair of <see cref="CustomBlockHeader"/> /
    ///     <see cref="CustomBlockFooter"/> marker lines is emitted at the
    ///     top. Any existing custom-block content is preserved between
    ///     them; any "loose" rules the user wrote outside the marker
    ///     pairs are moved INTO the custom block on first re-apply.
    ///   * The recommended block (passed in via
    ///     <paramref name="newBlock"/>) follows, replacing any previous
    ///     recommended-block content.
    ///
    /// The merge is idempotent: re-applying the same arguments leaves
    /// the result unchanged.
    /// </summary>
    public static string MergeIntoFilters(string? existingFilters, string newBlock)
    {
        // 1. Extract any existing recommended-block content so we can
        //    drop it (newBlock supersedes it).
        var withoutRecommended = StripBlock(existingFilters ?? "", AutoBlockHeader, AutoBlockFooter);

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

    /// <summary><paramref name="text"/> without its recommended block: the
    /// person's own rules only.</summary>
    internal static string WithoutBlock(string text)
        => StripBlock(text, AutoBlockHeader, AutoBlockFooter).TrimEnd('\r', '\n', ' ', '\t') + "\n";

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
