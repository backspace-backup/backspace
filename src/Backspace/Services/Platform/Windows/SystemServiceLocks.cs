using System;
using System.IO;
using System.Security.Principal;

namespace Backspace.Services.Platform.Windows;

/// <summary>Where the window and the version-3 service (U100) share their
/// operation locks: the owner's %LOCALAPPDATA%\Backspace\locks, beside the
/// engines. Both can open it, it works across sign-in sessions, a lock goes
/// with the process holding it, and Dropbox or OneDrive never open these
/// files (a sync client reading a lock file made it look busy).</summary>
internal static class SystemServiceLocks
{
    internal static string? Directory()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var root = AppContext.BaseDirectory.TrimEnd('\\');
        var config = Path.GetFullPath(AppPaths.ConfigRoot).TrimEnd('\\');
        string? engineRoot = null;
        if (Environment.GetEnvironmentVariable(ServiceWorkerBinding.ServiceNameVariable) is { Length: > 0 } name)
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User?.Value == SystemService.LocalSystemSid
                && SystemService.SamePath(config, SystemService.DataDirectoryFor(root)))
                engineRoot = SystemService.Read(name, null)?.EngineRoot;
        }
        else if (SystemService.SamePath(config, ServicePortableLayout.Config(root)) && SystemService.ForCopy(root) is { Activated: true } system)
            engineRoot = system.EngineRoot;
        if (string.IsNullOrEmpty(engineRoot)) return null;
        var directory = Path.Combine(Path.GetDirectoryName(engineRoot.TrimEnd('\\'))!, "locks");
        System.IO.Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>The lock file for a named gate.</summary>
    internal static string Name(string logicalName)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(logicalName))) + ".lock";

    /// <summary>Opens a lock file; null while another process holds it.</summary>
    internal static FileStream? TryOpen(string directory, string name, bool exclusive)
    {
        var path = Path.Combine(directory, name);
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, exclusive ? FileAccess.ReadWrite : FileAccess.Read,
                exclusive ? FileShare.None : FileShare.Read);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
