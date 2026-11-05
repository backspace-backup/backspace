using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>Engines are fetched into %LOCALAPPDATA%\Backspace\bin and only
/// accepted with the SHA-256 built into Backspace.exe (U100).</summary>
public sealed class EngineInstallationTests
{
    private static (EngineInstallation.Manifest Manifest, string Source) Release(TempWorkspace ws)
    {
        var source = ws.Sub("release");
        var files = new Dictionary<string, string>();
        foreach (var name in new[] { "backrest.exe", "restic.exe", "rclone.exe" })
        {
            var bytes = RandomNumberGenerator.GetBytes(4096);
            File.WriteAllBytes(Path.Combine(source, name), bytes);
            files[name] = Convert.ToHexString(SHA256.HashData(bytes));
        }
        return (new EngineInstallation.Manifest("9.9.9", files), source);
    }

    private static async Task WithSource(string source, string root, Func<Task> body)
    {
        Environment.SetEnvironmentVariable("BACKSPACE_ENGINE_SOURCE", source);
        Environment.SetEnvironmentVariable("BACKSPACE_ENGINE_ROOT", root);
        try { await body(); }
        finally
        {
            Environment.SetEnvironmentVariable("BACKSPACE_ENGINE_SOURCE", null);
            Environment.SetEnvironmentVariable("BACKSPACE_ENGINE_ROOT", null);
        }
    }

    [Fact]
    public async Task Missing_or_damaged_engines_are_fetched_and_checked()
    {
        using var ws = new TempWorkspace("engines");
        var (manifest, source) = Release(ws);
        var root = ws.Sub("bin");
        var set = Path.Combine(root, EngineInstallation.SetId(manifest));
        await WithSource(source, root, async () =>
        {
            await EngineInstallation.EnsureAsync(manifest, set, null, TestContext.Current.CancellationToken);
            Assert.True(EngineInstallation.IsComplete(set, manifest));

            // A damaged copy (another computer, a partial sync) is replaced.
            File.WriteAllText(Path.Combine(set, "restic.exe"), "damaged");
            Assert.False(EngineInstallation.IsComplete(set, manifest));
            await EngineInstallation.EnsureAsync(manifest, set, null, TestContext.Current.CancellationToken);
            Assert.True(EngineInstallation.IsComplete(set, manifest));
            Assert.Empty(Directory.GetFiles(set, "*.download"));
        });
    }

    [Fact]
    public async Task Older_engine_sets_are_removed_at_once_unless_still_running()
    {
        using var ws = new TempWorkspace("engines-old");
        var (manifest, source) = Release(ws);
        var root = ws.Sub("bin");
        var set = Path.Combine(root, EngineInstallation.SetId(manifest));
        var old = Directory.CreateDirectory(Path.Combine(root, "0000000000000000")).FullName;
        File.WriteAllText(Path.Combine(old, "restic.exe"), "old");
        var running = Directory.CreateDirectory(Path.Combine(root, "1111111111111111")).FullName;
        await using (new FileStream(Path.Combine(running, "restic.exe"), FileMode.Create, FileAccess.Write, FileShare.None))
        await WithSource(source, root, async () =>
        {
            await EngineInstallation.EnsureAsync(manifest, set, null, TestContext.Current.CancellationToken);
            Assert.False(Directory.Exists(old));
            Assert.True(Directory.Exists(running)); // in use by another Backspace: kept for now
            Assert.True(EngineInstallation.IsComplete(set, manifest));
        });
    }

    [Fact]
    public async Task A_download_that_does_not_match_is_never_used()
    {
        using var ws = new TempWorkspace("engines-mismatch");
        var (manifest, source) = Release(ws);
        File.WriteAllText(Path.Combine(source, "rclone.exe"), "tampered");
        var root = ws.Sub("bin");
        var set = Path.Combine(root, EngineInstallation.SetId(manifest));
        await WithSource(source, root, async () =>
        {
            var error = await Assert.ThrowsAsync<EngineBinaryMissingException>(
                () => EngineInstallation.EnsureAsync(manifest, set, null, TestContext.Current.CancellationToken));
            Assert.Contains("didn't match", error.Message);
            Assert.False(File.Exists(Path.Combine(set, "rclone.exe")));
            Assert.Empty(Directory.GetFiles(set, "*.download"));
        });
    }

    [Fact]
    public async Task Offline_first_start_explains_that_the_download_resumes_online()
    {
        using var ws = new TempWorkspace("engines-offline");
        var (manifest, _) = Release(ws);
        var root = ws.Sub("bin");
        var set = Path.Combine(root, EngineInstallation.SetId(manifest));
        await WithSource("http://127.0.0.1:1/unreachable", root, async () =>
        {
            var error = await Assert.ThrowsAsync<EngineBinaryMissingException>(
                () => EngineInstallation.EnsureAsync(manifest, set, null, TestContext.Current.CancellationToken));
            Assert.Contains("once this computer is online", error.Message);
        });
    }

    [Fact]
    public void An_engine_set_is_identified_by_its_files_not_their_order()
    {
        var a = new EngineInstallation.Manifest("1.0.3", new() { ["restic.exe"] = "AA", ["rclone.exe"] = "BB" });
        var b = new EngineInstallation.Manifest("1.0.4", new() { ["rclone.exe"] = "bb", ["restic.exe"] = "aa" });
        var c = new EngineInstallation.Manifest("1.0.3", new() { ["restic.exe"] = "AA", ["rclone.exe"] = "CC" });
        Assert.Equal(EngineInstallation.SetId(a), EngineInstallation.SetId(b));
        Assert.NotEqual(EngineInstallation.SetId(a), EngineInstallation.SetId(c));
        // This build carries its own manifest: the release download is pinned.
        Assert.NotNull(EngineInstallation.Current);
        Assert.Equal(3, EngineInstallation.Current!.Files.Count);
    }

    /// <summary>U102: each engine comes from its own project's Windows archive
    /// at the pinned version, and only that one file is taken from it.</summary>
    [Fact]
    public void Engines_come_from_their_own_projects_at_the_pinned_versions()
    {
        var manifest = new EngineInstallation.Manifest("1.1.1", new Dictionary<string, string>(), true,
            new Dictionary<string, string> { ["backrest"] = "v1.14.1", ["restic"] = "v0.19.1", ["rclone"] = "v1.74.1" });
        Assert.Equal(("https://github.com/garethgeorge/backrest/releases/download/v1.14.1/backrest_Windows_x86_64.zip", "backrest.exe"),
            EngineInstallation.Origin(manifest, "backrest.exe"));
        Assert.Equal(("https://github.com/restic/restic/releases/download/v0.19.1/restic_0.19.1_windows_amd64.zip", "restic_0.19.1_windows_amd64.exe"),
            EngineInstallation.Origin(manifest, "restic.exe"));
        Assert.Equal(("https://downloads.rclone.org/v1.74.1/rclone-v1.74.1-windows-amd64.zip", "rclone-v1.74.1-windows-amd64/rclone.exe"),
            EngineInstallation.Origin(manifest, "rclone.exe"));
        // U137: with Backspace's own restic build pinned, restic comes from its
        // engine release (engine/restic); the other engines are unchanged.
        var patched = manifest with { Engines = new(manifest.Engines!) { ["resticBackspace"] = "v0.19.1-backspace.1" } };
        Assert.Equal(("https://github.com/backspace-backup/backspace/releases/download/engine-restic-v0.19.1-backspace.1/restic_0.19.1-backspace.1_windows_amd64.zip", "restic_0.19.1-backspace.1_windows_amd64.exe"),
            EngineInstallation.Origin(patched, "restic.exe"));
        Assert.Equal(EngineInstallation.Origin(manifest, "rclone.exe"), EngineInstallation.Origin(patched, "rclone.exe"));
        // A build without pinned versions (1.1.0) still uses its own release.
        Assert.Equal(("https://github.com/backspace-backup/backspace/releases/download/v1.1.0/restic.exe", (string?)null),
            EngineInstallation.Origin(new EngineInstallation.Manifest("1.1.0", new Dictionary<string, string>(), true), "restic.exe"));
    }

    [Fact]
    public void Only_the_engine_file_is_taken_from_its_archive()
    {
        using var ws = new TempWorkspace();
        var archive = Path.Combine(ws.Root, "rclone.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var w = zip.CreateEntry("rclone-v1.74.1-windows-amd64/README.txt").Open()) w.Write("readme"u8);
            using (var w = zip.CreateEntry("rclone-v1.74.1-windows-amd64/rclone.exe").Open()) w.Write("MZ rclone"u8);
        }
        var target = Path.Combine(ws.Root, "rclone.exe");
        EngineInstallation.TakeFromArchive(archive, "rclone-v1.74.1-windows-amd64/rclone.exe", target);
        Assert.Equal("MZ rclone", File.ReadAllText(target));
        Assert.Single(Directory.GetFiles(ws.Root, "*.exe"));
        Assert.Throws<InvalidDataException>(() => EngineInstallation.TakeFromArchive(archive, "restic.exe", Path.Combine(ws.Root, "x.exe")));
    }
}
