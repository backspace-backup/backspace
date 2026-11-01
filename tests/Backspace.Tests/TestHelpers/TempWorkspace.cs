using System;
using System.IO;

namespace Backspace.Tests.TestHelpers;

/// <summary>
/// Temporary per-test directory on disk, auto-deleted on Dispose.
///
/// The workspace also swaps BACKSPACE_CONFIG_ROOT to a subdirectory of itself
/// so any Backspace service started during this test writes config,
/// logs, and repo state under the temp tree instead of the real portable
/// config folder. Disposing restores the previous environment value.
///
/// Usage:
///   using var ws = new TempWorkspace();
///   var source = ws.Sub("source");
///   SyntheticFileTree.Generate(source, seed: 42, targetBytes: 1_000_000);
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    private readonly string? _previousConfigRoot;
    private bool _disposed;

    public string Root { get; }
    public string ConfigRoot { get; }

    public TempWorkspace(string? label = null)
    {
        var name = label is null
            ? $"backspace-test-{Guid.NewGuid():N}"
            : $"backspace-test-{label}-{Guid.NewGuid():N}";
        Root = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(Root);

        ConfigRoot = Path.Combine(Root, "config");
        Directory.CreateDirectory(ConfigRoot);

        _previousConfigRoot = Environment.GetEnvironmentVariable("BACKSPACE_CONFIG_ROOT");
        Environment.SetEnvironmentVariable("BACKSPACE_CONFIG_ROOT", ConfigRoot);
    }

    /// <summary>Create (if missing) and return the path of a subdirectory of the workspace.</summary>
    public string Sub(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A subdirectory whose permissions behave like a folder the person
    /// made in Documents or on a drive: auto-inherited (AI) entries for its owner,
    /// SYSTEM and Administrators. Folders under %TEMP% have no AI flag, and Windows
    /// protects such legacy lists when a parent's permissions change, which real
    /// backup and program folders do not do (31 October 2026).</summary>
    public string OrdinaryFolder(string name)
    {
        var path = Sub(name);
        if (!OperatingSystem.IsWindows()) return path;
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        var security = new System.Security.AccessControl.DirectorySecurity();
        security.SetSecurityDescriptorSddlForm($"O:{sid}D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;{sid})",
            System.Security.AccessControl.AccessControlSections.Owner | System.Security.AccessControl.AccessControlSections.Access);
        new DirectoryInfo(path).SetAccessControl(security);
        return path;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Environment.SetEnvironmentVariable("BACKSPACE_CONFIG_ROOT", _previousConfigRoot);

        // Best-effort cleanup. Tests shouldn't hold open file handles but if
        // they do (a log still being written) we swallow the exception so
        // test teardown doesn't mask the real failure.
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
