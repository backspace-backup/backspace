using System.Diagnostics;
using System.IO;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U152: a restored item is moved into place by handle. A folder
/// another account swaps for a link after the restore began can neither carry
/// the owner's files out of their profile nor put a file into it.</summary>
public sealed class HandleRenameTests
{
    private static void Junction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, target }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }

    private static (string Root, string Location, string Staging) Restore(TempWorkspace ws)
    {
        var root = Directory.CreateDirectory(Path.Combine(ws.Root, "Restore")).FullName;
        var staging = Directory.CreateDirectory(Path.Combine(root, ".backspace-restore-1", "Desktop")).FullName;
        return (root, HandleRename.FolderLocation(root), staging);
    }

    [Fact]
    public void Moves_a_restored_file_and_never_replaces_one()
    {
        using var ws = new TempWorkspace("handle-rename");
        var (root, location, staging) = Restore(ws);
        var staged = Path.Combine(staging, "notes.txt");
        File.WriteAllText(staged, "restored");
        var destination = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "Desktop")).FullName, "notes.txt");
        HandleRename.Move(staged, destination, root, location);
        Assert.Equal("restored", File.ReadAllText(destination));
        Assert.False(File.Exists(staged));

        File.WriteAllText(staged, "second");
        Assert.Throws<IOException>(() => HandleRename.Move(staged, destination, root, location));
        Assert.Equal("restored", File.ReadAllText(destination));
    }

    [Fact]
    public void A_staging_folder_swapped_for_a_link_does_not_move_the_owners_files()
    {
        using var ws = new TempWorkspace("handle-rename-source");
        var (root, location, staging) = Restore(ws);
        var desktop = Directory.CreateDirectory(Path.Combine(ws.Root, "Owner", "Desktop")).FullName;
        File.WriteAllText(Path.Combine(desktop, "notes.txt"), "the owner's current file");
        Directory.Move(staging, staging + "-away");
        Junction(staging, desktop);
        try
        {
            var destination = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "Desktop")).FullName, "notes.txt");
            Assert.Throws<IOException>(() => HandleRename.Move(Path.Combine(staging, "notes.txt"), destination, root, location));
            Assert.Equal("the owner's current file", File.ReadAllText(Path.Combine(desktop, "notes.txt")));
            Assert.False(File.Exists(destination));
        }
        finally { Directory.Delete(staging); }
    }

    [Fact]
    public void A_destination_folder_swapped_for_a_link_receives_nothing()
    {
        using var ws = new TempWorkspace("handle-rename-destination");
        var (root, location, staging) = Restore(ws);
        var staged = Path.Combine(staging, "Chrome.lnk");
        File.WriteAllText(staged, "planted shortcut");
        var startup = Directory.CreateDirectory(Path.Combine(ws.Root, "Owner", "Startup")).FullName;
        var destinationFolder = Path.Combine(root, "Desktop");
        Junction(destinationFolder, startup);
        try
        {
            Assert.Throws<IOException>(() => HandleRename.Move(staged, Path.Combine(destinationFolder, "Chrome.lnk"), root, location));
            Assert.Empty(Directory.EnumerateFileSystemEntries(startup));
            Assert.True(File.Exists(staged));
        }
        finally { Directory.Delete(destinationFolder); }
    }

    [Fact]
    public void A_restore_folder_reached_through_a_link_is_refused()
    {
        using var ws = new TempWorkspace("handle-rename-root");
        var real = Directory.CreateDirectory(Path.Combine(ws.Root, "real", "Restore")).FullName;
        var link = Path.Combine(ws.Root, "linked");
        Junction(link, Path.GetDirectoryName(real)!);
        try { Assert.Throws<IOException>(() => HandleRename.FolderLocation(Path.Combine(link, "Restore"))); }
        finally { Directory.Delete(link); }
    }
}
