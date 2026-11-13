using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Backspace.Tests.TestHelpers;

/// <summary>
/// Tests of Windows behaviour (drive letters, \\server paths, files another
/// program holds open, Windows' own folders, Windows error codes, the
/// administrator service, VSS) are skipped elsewhere, saying why, rather
/// than passing without testing anything (U235).
/// </summary>
internal static class PlatformTest
{
    internal static void WindowsOnly(string why) =>
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only: " + why);

    /// <summary>A folder that leads to <paramref name="target"/>: a junction
    /// on Windows (no special right needed), a symbolic link elsewhere.</summary>
    internal static void LinkFolder(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    /// <summary>Replaces a file's contents. restic saves repository files
    /// read-only on Linux and macOS; a file in a folder you may write to can
    /// still be replaced, so its permissions are never changed.</summary>
    internal static void Overwrite(string path, byte[] contents)
    {
        try { File.WriteAllBytes(path, contents); }
        catch (UnauthorizedAccessException) when (!OperatingSystem.IsWindows())
        {
            File.Delete(path);
            File.WriteAllBytes(path, contents);
        }
    }
}
