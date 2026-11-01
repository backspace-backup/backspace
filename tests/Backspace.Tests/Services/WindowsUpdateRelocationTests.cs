using System;
using System.IO;
using System.Linq;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class WindowsUpdateRelocationTests
{
    [Theory]
    [InlineData("Prepared")]
    [InlineData("Installed")]
    [InlineData("RolledBack")]
    public void Finished_or_downloaded_update_history_does_not_prevent_startup_after_portable_folder_moves(string state)
    {
        using var workspace = new TempWorkspace();
        var root = workspace.Sub("portable-before");
        var work = WindowsUpdatePackage.CreateWorkDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "Backspace.config"));
        File.WriteAllText(Path.Combine(root, "Backspace.exe"), "Current application bytes");
        File.WriteAllText(Path.Combine(root, "Backspace.config", "config.json"), "User settings bytes");
        WindowsUpdatePackage.SavePlan(new() { Root = root, Work = work, State = state });
        var moved = Path.Combine(workspace.Root, "portable-after");
        MoveFixture(root, moved);
        var before = Directory.GetFiles(moved, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, File.ReadAllBytes);

        Assert.False(WindowsUpdateRuntime.RecoverInterruptedBeforeStartup(moved, Array.Empty<string>()));

        Assert.False(Directory.Exists(root));
        Assert.Equal(before.Count, Directory.GetFiles(moved, "*", SearchOption.AllDirectories).Length);
        foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
    }

    [Fact]
    public void Moving_an_unfinished_replacement_does_not_authorize_recovery_against_old_paths()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.Sub("portable-before");
        var work = WindowsUpdatePackage.CreateWorkDirectory(root);
        WindowsUpdatePackage.SavePlan(new() { Root = root, Work = work, State = "Installing" });
        File.WriteAllText(Path.Combine(root, "Backspace.exe"), "Keep current bytes");
        var moved = Path.Combine(workspace.Root, "portable-after");
        MoveFixture(root, moved);
        Assert.ThrowsAny<IOException>(() => WindowsUpdateRuntime.RecoverInterruptedBeforeStartup(moved, Array.Empty<string>()));
        Assert.Equal("Keep current bytes", File.ReadAllText(Path.Combine(moved, "Backspace.exe")));
    }

    private static void MoveFixture(string source, string destination)
    {
        // This prepares the user's already-moved folder, before invoking any
        // recovery code. Windows filters can briefly retain freshly written
        // fixture files; a persistent lock still fails after two seconds.
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { Directory.Move(source, destination); return; }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 5 or 32
                && elapsed.Elapsed < TimeSpan.FromSeconds(2))
            { System.Threading.Thread.Sleep(25); }
        }
    }
}
