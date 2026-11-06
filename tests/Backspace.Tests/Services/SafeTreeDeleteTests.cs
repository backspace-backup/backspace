using System.Diagnostics;
using System.IO;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U152: deleting a restore's temporary folder removes links inside
/// it, never what they lead to.</summary>
public sealed class SafeTreeDeleteTests
{
    private static void Junction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, target }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }

    [Fact]
    public void Deletes_nested_files_including_read_only_ones()
    {
        using var ws = new TempWorkspace("safe-tree");
        var root = Path.Combine(ws.Root, ".backspace-restore-1");
        Directory.CreateDirectory(Path.Combine(root, "a", "b"));
        File.WriteAllText(Path.Combine(root, "a", "b", "x.txt"), "x");
        var locked = Path.Combine(root, "a", "ro.txt");
        File.WriteAllText(locked, "ro");
        File.SetAttributes(locked, FileAttributes.ReadOnly);
        SafeTreeDelete.Delete(root);
        Assert.False(Directory.Exists(root));
        SafeTreeDelete.Delete(root); // already gone: nothing to do
    }

    [Fact]
    public void A_link_inside_is_removed_and_its_target_kept()
    {
        using var ws = new TempWorkspace("safe-tree-link");
        var victim = Directory.CreateDirectory(Path.Combine(ws.Root, "victim")).FullName;
        File.WriteAllText(Path.Combine(victim, "precious.txt"), "keep me");
        var root = Directory.CreateDirectory(Path.Combine(ws.Root, ".backspace-restore-2")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "sub", "restored.txt"), "temporary");
        Junction(Path.Combine(root, "sub", "swapped"), victim);
        Junction(Path.Combine(root, "top-link"), victim);
        SafeTreeDelete.Delete(root);
        Assert.False(Directory.Exists(root));
        Assert.True(File.Exists(Path.Combine(victim, "precious.txt")));

        // The folder handed in is itself a link: only the link goes.
        var rootLink = Path.Combine(ws.Root, ".backspace-restore-3");
        Junction(rootLink, victim);
        SafeTreeDelete.Delete(rootLink);
        Assert.False(Directory.Exists(rootLink));
        Assert.True(File.Exists(Path.Combine(victim, "precious.txt")));
    }

    /// <summary>Round 4: the restore folder above the temporary folder is
    /// renamed and replaced by a link to one of the owner's folders, whose
    /// subfolder has the temporary folder's name. Its contents are kept.</summary>
    [Fact]
    public void A_temporary_folder_reached_through_a_swapped_folder_above_is_not_emptied()
    {
        using var ws = new TempWorkspace("safe-tree-above");
        var owner = Directory.CreateDirectory(Path.Combine(ws.Root, "Owner", ".backspace-restore-4")).FullName;
        File.WriteAllText(Path.Combine(owner, "precious.txt"), "keep me");
        var restore = Path.Combine(ws.Root, "Restore");
        Junction(restore, Path.GetDirectoryName(owner)!);
        try
        {
            Assert.Throws<IOException>(() => SafeTreeDelete.Delete(Path.Combine(restore, ".backspace-restore-4")));
            Assert.Equal("keep me", File.ReadAllText(Path.Combine(owner, "precious.txt")));
        }
        finally { Directory.Delete(restore); }
    }
}
