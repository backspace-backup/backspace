using System.Diagnostics;
using System.IO;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U152: the service deletes its note on a drive only at the exact
/// path, never through a folder another account swapped for a link.</summary>
public sealed class ExactPathDeleteTests
{
    [Fact]
    public void Deletes_the_file_at_its_path_and_reports_a_missing_one()
    {
        using var ws = new TempWorkspace("exact-delete");
        var note = Path.Combine(ws.Root, ".backspace.b1.accepted");
        File.WriteAllText(note, "note");
        File.SetAttributes(note, FileAttributes.Hidden);
        Assert.True(ExactPathDelete.File(note));
        Assert.False(File.Exists(note));
        Assert.False(ExactPathDelete.File(note));
    }

    [Fact]
    public void A_folder_swapped_for_a_link_leaves_the_file_it_leads_to_alone()
    {
        using var ws = new TempWorkspace("exact-delete-link");
        var real = Directory.CreateDirectory(Path.Combine(ws.Root, "elsewhere")).FullName;
        var victim = Path.Combine(real, ".backspace.b1.accepted");
        File.WriteAllText(victim, "someone else's file");
        var link = Path.Combine(ws.Root, "Backups");
        // A junction needs no special right: any account that can change the folder can make one.
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, real }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
        {
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }
        try
        {
            Assert.Throws<IOException>(() => ExactPathDelete.File(Path.Combine(link, ".backspace.b1.accepted")));
            Assert.True(File.Exists(victim));
            // The link's own name is not followed either.
            Assert.Throws<IOException>(() => ExactPathDelete.File(link));
            Assert.True(Directory.Exists(real));
        }
        finally { Directory.Delete(link); }
    }
}
