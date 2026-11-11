using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Backspace.Services;

/// <summary>
/// Files that belong to this computer, not to the portable folder (U153).
///
/// <para>The engine's working files can hold readable credentials: rclone
/// keeps cloud tokens in its configuration. They are rebuilt from the vault
/// and the settings whenever they are missing, so they live outside
/// <c>Backspace.config</c> (which the owner syncs) in
/// <c>%LOCALAPPDATA%\Backspace\engine\&lt;16 hex of SHA-256 of the upper-case
/// configuration folder&gt;</c>: the generated rclone remotes, the exclude
/// files, the mounted drive's cache and the background service's scratch
/// folder. (Backrest's folders, which held repository passwords, are removed
/// since U175: <see cref="BackrestLeftovers"/>.)
/// The per-computer copies of the vault key sit in
/// <c>%LOCALAPPDATA%\Backspace\keys</c>.</para>
///
/// <para>The background service runs as Windows' own account; it sets
/// <c>BACKSPACE_MACHINE_ROOT</c> to the owner's <c>%LOCALAPPDATA%\Backspace</c>
/// (beside the engines it already uses), so its workers find the same place.
/// Tests set that variable too. A test configuration outside an
/// executable's folder, without it, keeps these under its own <c>engine</c>
/// folder.</para>
///
/// <para>Run history and engine transcripts stay in <c>Backspace.config\logs</c>
/// and <c>Backspace.config\service\logs</c> (<see cref="AppPaths.EngineLogsRoot"/>):
/// the app reads run history from there (U138).</para>
/// </summary>
internal static class EngineWorkPaths
{
    internal const string MachineRootVariable = "BACKSPACE_MACHINE_ROOT";

    /// <summary>True when the configuration folder is this copy's own
    /// <c>Backspace.config</c> (or a folder inside it, such as the service's).</summary>
    internal static bool IsPortableConfig(string? config = null)
    {
        var root = Path.GetFullPath(config ?? AppPaths.ConfigRoot).TrimEnd('\\', '/');
        var portable = Path.GetFullPath(Path.Combine(AppPaths.ExeDirectory, AppPaths.AppFolderName)).TrimEnd('\\', '/');
        return root.Equals(portable, StringComparison.OrdinalIgnoreCase)
            || root.StartsWith(portable + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>This computer's folder for this account's Backspace files.</summary>
    internal static string MachineRoot
    {
        get
        {
            if (Environment.GetEnvironmentVariable(MachineRootVariable) is { Length: > 0 } overridden)
                return Path.GetFullPath(overridden);
            if (IsPortableConfig())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Backspace");
            return Path.Combine(Path.GetFullPath(AppPaths.ConfigRoot), "engine");
        }
    }

    internal static string KeysDirectory => Path.Combine(MachineRoot, "keys");

    /// <summary>The 16 hex digits naming a configuration's working folder.</summary>
    internal static string ConfigHash(string config)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(config).TrimEnd('\\', '/').ToUpperInvariant())))[..16].ToLowerInvariant();

    /// <summary>The working folder for the current configuration.</summary>
    internal static string Root => RootFor(AppPaths.ConfigRoot);

    internal static string RootFor(string config)
    {
        if (Environment.GetEnvironmentVariable(MachineRootVariable) is not { Length: > 0 } && !IsPortableConfig(config))
            return Path.Combine(Path.GetFullPath(config), "engine");
        return Path.Combine(MachineRoot, "engine", ConfigHash(config));
    }

    internal static string RcloneRemotesDir => Path.Combine(Root, "rclone-remotes");
    internal static string RcloneConfigFile => Path.Combine(Root, "rclone.conf");
    /// <summary>The mounted drive's copies of files read so far, per backup.</summary>
    internal static string MountCacheDir => Path.Combine(Root, "mount-cache");
    internal static string ScratchDir => Path.Combine(Root, "scratch");
}
