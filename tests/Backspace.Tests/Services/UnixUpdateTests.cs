using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U245: in-app updates on Linux and macOS. The release's package for
/// this computer is chosen; only the program (or Backspace.app) is taken from
/// it; it replaces the running copy under the user's name for it, and rolls
/// back on failure. The staging and replacement run on Linux and macOS.</summary>
public sealed class UnixUpdateTests
{
    private static readonly byte[] Elf = [0x7F, (byte)'E', (byte)'L', (byte)'F', 1, 2, 3, 4];
    private static readonly byte[] MachO = [0xCF, 0xFA, 0xED, 0xFE, 9, 9, 9];

    [Theory]
    [InlineData("linux", false, "Backspace-linux-x64.tar.gz")]
    [InlineData("linux", true, "Backspace-linux-arm64.tar.gz")]
    [InlineData("macos", true, "Backspace-macos-arm64.zip")]
    [InlineData("macos", false, "Backspace-macos-x64.zip")]
    [InlineData(null, false, null)]
    public void Each_system_takes_its_own_package(string? system, bool arm, string? expected) =>
        Assert.Equal(expected, WindowsUpdatePackage.UnixPackageName(system, arm));

    private static string ReleaseJson(params string[] assets) => "{\"tag_name\":\"v9.9.9\",\"html_url\":\"https://github.com/backspace-backup/backspace/releases/tag/v9.9.9\",\"draft\":false,\"prerelease\":false,\"body\":\"\",\"assets\":["
        + string.Join(",", assets.Select(a => "{\"name\":\"" + a + "\",\"browser_download_url\":\"https://github.com/backspace-backup/backspace/releases/download/v9.9.9/" + a
            + "\",\"size\":100,\"digest\":\"sha256:" + new string('a', 64) + "\"}")) + "]}";

    [Fact]
    public void A_release_offers_the_package_for_this_computer_or_says_it_has_none()
    {
        var json = ReleaseJson("Backspace.exe", "Backspace-linux-x64.tar.gz", "Backspace-macos-arm64.zip");
        var linux = WindowsUpdatePackage.ParseRelease(json, new Version(1, 0, 0), "Backspace-linux-x64.tar.gz")!;
        Assert.Equal("Backspace-linux-x64.tar.gz", linux.UnixPackage);
        Assert.EndsWith("/Backspace-linux-x64.tar.gz", linux.AssetUrl);
        Assert.False(linux.Executable);
        var windows = WindowsUpdatePackage.ParseRelease(json, new Version(1, 0, 0), null)!;
        Assert.True(windows.Executable);
        Assert.Null(windows.UnixPackage);
        var none = Assert.Throws<InvalidDataException>(() => WindowsUpdatePackage.ParseRelease(json, new Version(1, 0, 0), "Backspace-linux-arm64.tar.gz"));
        Assert.Contains("Backspace-linux-arm64.tar.gz", none.Message);
    }

    private static string TarGz(string dir, params (string Name, byte[] Data)[] files)
    {
        var path = Path.Combine(dir, "package.tar.gz");
        using var output = File.Create(path);
        using var gz = new GZipStream(output, CompressionLevel.Fastest);
        using var tar = new TarWriter(gz);
        foreach (var (name, data) in files)
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });
        return path;
    }

    private static string AppZip(string dir, params (string Name, byte[] Data)[] files)
    {
        var path = Path.Combine(dir, "package.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, data) in files)
            using (var s = zip.CreateEntry(name).Open()) s.Write(data);
        return path;
    }

    private static void UnixOnly() => Assert.SkipWhen(OperatingSystem.IsWindows(), "Linux and macOS only: Windows updates replace Backspace.exe (WindowsUpdateTests).");

    [Fact]
    public void Linux_the_program_replaces_the_running_copy_and_rolls_back()
    {
        UnixOnly();
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "Linux's package; macOS's is checked below.");
        var root = Directory.CreateTempSubdirectory("bs-unix-update-").FullName;
        try
        {
            var installed = Path.Combine(root, "Backspace");
            File.WriteAllBytes(installed, [.. Elf, 0]);
            var work = WindowsUpdatePackage.CreateWorkDirectory(root);
            var package = TarGz(work, ("Backspace", [.. Elf, 1]));
            var plan = WindowsUpdatePackage.StageUnix(package, root, work, "Backspace", "9.9.9", CancellationToken.None);
            Assert.True(File.GetUnixFileMode(Path.Combine(work, "incoming", "Backspace")).HasFlag(UnixFileMode.UserExecute));
            WindowsUpdatePackage.Install(plan, _ => { });
            Assert.Equal([.. Elf, 1], File.ReadAllBytes(installed));
            // Nothing beside the program but its settings folder.
            Assert.Equal(new[] { "Backspace", AppPaths.AppFolderName }, Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).Order().ToArray());
            WindowsUpdatePackage.RollBack(plan);
            Assert.Equal([.. Elf, 0], File.ReadAllBytes(installed));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Linux_anything_but_the_one_program_is_refused()
    {
        UnixOnly();
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "Linux's package.");
        var root = Directory.CreateTempSubdirectory("bs-unix-update-").FullName;
        try
        {
            foreach (var files in new[] { new[] { ("Backspace", Elf), ("evil.sh", Elf) }, new[] { ("../Backspace", Elf) }, new[] { ("Backspace", Encoding.ASCII.GetBytes("#!/bin/sh")) } })
            {
                var work = WindowsUpdatePackage.CreateWorkDirectory(root);
                var package = TarGz(work, files);
                Assert.Throws<InvalidDataException>(() => WindowsUpdatePackage.StageUnix(package, root, work, "Backspace", "9.9.9", CancellationToken.None));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void The_app_bundle_replaces_the_running_one_under_its_name_and_rolls_back()
    {
        UnixOnly();
        var root = Directory.CreateTempSubdirectory("bs-unix-update-").FullName;
        try
        {
            // The user renamed it; the update keeps that name.
            var installed = Path.Combine(root, "My Backspace.app");
            Directory.CreateDirectory(Path.Combine(installed, "Contents", "MacOS"));
            File.WriteAllBytes(Path.Combine(installed, "Contents", "MacOS", "Backspace"), [.. MachO, 0]);
            File.WriteAllText(Path.Combine(installed, "Contents", "Info.plist"), "old");
            var work = WindowsUpdatePackage.CreateWorkDirectory(root);
            var package = AppZip(work, ("Backspace.app/Contents/MacOS/Backspace", [.. MachO, 1]), ("Backspace.app/Contents/Info.plist", Encoding.ASCII.GetBytes("new")),
                ("Backspace.app/Contents/Resources/Backspace.icns", [1, 2, 3]));
            var plan = WindowsUpdatePackage.StageUnix(package, root, work, "My Backspace.app", "9.9.9", CancellationToken.None);
            WindowsUpdatePackage.Install(plan, _ => { });
            Assert.Equal([.. MachO, 1], File.ReadAllBytes(Path.Combine(installed, "Contents", "MacOS", "Backspace")));
            Assert.Equal("new", File.ReadAllText(Path.Combine(installed, "Contents", "Info.plist")));
            Assert.True(File.Exists(Path.Combine(installed, "Contents", "Resources", "Backspace.icns")));
            Assert.Equal(new[] { AppPaths.AppFolderName, "My Backspace.app" }, Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).Order().ToArray());
            WindowsUpdatePackage.RollBack(plan);
            Assert.Equal([.. MachO, 0], File.ReadAllBytes(Path.Combine(installed, "Contents", "MacOS", "Backspace")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(installed, "Contents", "Info.plist")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void An_app_package_with_anything_outside_the_bundle_or_no_program_is_refused()
    {
        UnixOnly();
        var root = Directory.CreateTempSubdirectory("bs-unix-update-").FullName;
        try
        {
            foreach (var files in new[]
            {
                new[] { ("Backspace.app/Contents/MacOS/Backspace", MachO), ("Backspace.app/Contents/Info.plist", MachO), ("evil", MachO) },
                new[] { ("Backspace.app/../evil", MachO) },
                new[] { ("Backspace.app/Contents/Info.plist", MachO) },
                new[] { ("Backspace.app/Contents/MacOS/Backspace", Elf), ("Backspace.app/Contents/Info.plist", MachO) },
            })
            {
                var work = WindowsUpdatePackage.CreateWorkDirectory(root);
                var package = AppZip(work, files);
                Assert.Throws<InvalidDataException>(() => WindowsUpdatePackage.StageUnix(package, root, work, "Backspace.app", "9.9.9", CancellationToken.None));
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
