using System.IO;
using System.Linq;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Versions before the portable layout left a task helper and the
/// snapshot-mount library under LocalAppData (found on the developer laptop,
/// 4 November 2026). Startup removes exactly those files, and their folders
/// only when empty; the engines cache there stays.
/// </summary>
public sealed class PrePortableLeftoversTests
{
    [Fact]
    public void Only_the_old_helper_files_go_and_the_engines_stay()
    {
        using var workspace = new TempWorkspace("pre-portable");
        var local = workspace.Root;
        string Make(params string[] parts)
        {
            var path = Path.Combine(new[] { local }.Concat(parts).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
            return path;
        }
        var stub = Make("Programs", "Backspace", "Backspace-stub.exe");
        var lastExe = Make("Backspace", "last-exe-path.txt");
        var msil = Make("Backspace", "winfsp", "winfsp-msil.dll");
        var msi = Make("Backspace", "winfsp", "winfsp.msi");
        Directory.CreateDirectory(Path.Combine(local, "Backspace", "verify"));
        var engine = Make("Backspace", "bin", "13bd12379e6186cf", "restic.exe");
        var lockFile = Make("Backspace", "locks", "operations.lock");
        var someoneElses = Make("Programs", "Other", "Backspace-stub.exe");

        Program.RemovePrePortableLeftovers(local);

        foreach (var gone in new[] { stub, lastExe, msil, msi }) Assert.False(File.Exists(gone), gone);
        Assert.False(Directory.Exists(Path.Combine(local, "Programs", "Backspace")));
        Assert.False(Directory.Exists(Path.Combine(local, "Backspace", "winfsp")));
        Assert.False(Directory.Exists(Path.Combine(local, "Backspace", "verify")));
        foreach (var kept in new[] { engine, lockFile, someoneElses }) Assert.True(File.Exists(kept), kept);
    }

    [Fact]
    public void A_folder_with_anything_else_in_it_is_left_alone()
    {
        using var workspace = new TempWorkspace("pre-portable-other");
        var verify = Path.Combine(workspace.Root, "Backspace", "verify");
        Directory.CreateDirectory(verify);
        File.WriteAllText(Path.Combine(verify, "notes.txt"), "keep");
        Program.RemovePrePortableLeftovers(workspace.Root);
        Assert.True(File.Exists(Path.Combine(verify, "notes.txt")));
    }
}
