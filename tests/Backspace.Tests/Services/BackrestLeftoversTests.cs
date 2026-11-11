using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U175 step 4: Backrest's old working folders, which held a readable copy of
/// repository passwords, are removed from this computer, and nothing else:
/// not the engine folder's other files, not a similarly named folder, never
/// through a link.
/// </summary>
public sealed class BackrestLeftoversTests
{
    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void Backrests_folders_go_and_everything_else_in_the_engine_folder_stays()
    {
        using var workspace = new TempWorkspace("backrest-leftovers");
        var root = workspace.Sub("engine");
        // What Backrest left: its folder with the old mount cache, two
        // workers' folders, their claim files and a per-run folder.
        Write(Path.Combine(root, "backrest", "config.json"), "{\"repos\":[{\"password\":\"plain-text\"}]}");
        Write(Path.Combine(root, "backrest", "mount-cache", "b1", "file.bin"), "cached");
        Write(Path.Combine(root, "backrest-1", "config.json"), "{\"password\":\"plain-text\"}");
        Write(Path.Combine(root, "backrest-7", "oplog.sqlite"), "history");
        Write(Path.Combine(root, "engine-runs", "scheduled", "123-abc", "config.json"), "{\"password\":\"plain-text\"}");
        Write(Path.Combine(root, "worker-slot-0.lock"), "");
        Write(Path.Combine(root, "worker-slot-7.lock"), "");
        // What stays: Backspace's own files, and names that only look alike.
        Write(Path.Combine(root, "filters", "plan.txt"), "*.tmp");
        Write(Path.Combine(root, "rclone-remotes", "remote.conf"), "[remote]");
        Write(Path.Combine(root, "repositories.json"), "{}");
        Write(Path.Combine(root, "scratch", "keep.tmp"), "scratch");
        Write(Path.Combine(root, "backrest-8", "keep.txt"), "not a worker folder");
        Write(Path.Combine(root, "backrest-old", "keep.txt"), "another name");
        Write(Path.Combine(root, "mount-cache", "b1", "file.bin"), "the new cache");

        var removed = BackrestLeftovers.Remove(root);

        Assert.Equal(new[] { "backrest", "backrest-1", "backrest-7", "engine-runs" }, removed.Select(Path.GetFileName).Order());
        foreach (var gone in new[] { "backrest", "backrest-1", "backrest-7", "engine-runs" })
            Assert.False(Directory.Exists(Path.Combine(root, gone)), gone);
        Assert.False(File.Exists(Path.Combine(root, "worker-slot-0.lock")));
        Assert.False(File.Exists(Path.Combine(root, "worker-slot-7.lock")));
        foreach (var kept in new[] { "filters/plan.txt", "rclone-remotes/remote.conf", "repositories.json", "scratch/keep.tmp",
                     "backrest-8/keep.txt", "backrest-old/keep.txt", "mount-cache/b1/file.bin" })
            Assert.True(File.Exists(Path.Combine(root, kept)), kept);
        // Nothing left to do the second time.
        Assert.Empty(BackrestLeftovers.Remove(root));
    }

    /// <summary>A link with Backrest's name, or a link inside its folder, is
    /// never followed: what it leads to stays whole.</summary>
    [Fact]
    public async Task Links_are_never_followed()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace("backrest-leftovers-links");
        var root = workspace.Sub("engine");
        var outside = workspace.Sub("outside");
        Write(Path.Combine(outside, "precious.txt"), "the person's file");
        Write(Path.Combine(root, "backrest-1", "config.json"), "{\"password\":\"plain-text\"}");
        await Junction(Path.Combine(root, "backrest"), outside);
        await Junction(Path.Combine(root, "backrest-1", "linked"), outside);
        try
        {
            var removed = BackrestLeftovers.Remove(root);
            Assert.Equal(new[] { "backrest-1" }, removed.Select(Path.GetFileName));
            Assert.Equal("the person's file", File.ReadAllText(Path.Combine(outside, "precious.txt")));
            Assert.True(Directory.Exists(Path.Combine(root, "backrest")), "A link with Backrest's name is left alone.");
            Assert.False(Directory.Exists(Path.Combine(root, "backrest-1")));
        }
        finally { if (Directory.Exists(Path.Combine(root, "backrest"))) Directory.Delete(Path.Combine(root, "backrest")); }
        // An engine folder that is itself a link: nothing is removed through it.
        var linkedRoot = Path.Combine(workspace.Root, "linked-engine");
        Write(Path.Combine(outside, "backrest", "config.json"), "{}");
        await Junction(linkedRoot, outside);
        try
        {
            Assert.Empty(BackrestLeftovers.Remove(linkedRoot));
            Assert.True(File.Exists(Path.Combine(outside, "backrest", "config.json")));
        }
        finally { Directory.Delete(linkedRoot); }
    }

    private static async Task Junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, process.ExitCode);
    }
}
