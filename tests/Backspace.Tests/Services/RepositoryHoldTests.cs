using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U152: every folder of a backup on this PC is held while the
/// engine works there, including the ones restic would make during the run,
/// so another account cannot empty one and turn it into a link that sends
/// restic's deletes (its lock files, old packs) onto other files.</summary>
public sealed class RepositoryHoldTests
{
    private static readonly string[] Standard = { "keys", "locks", "index", "snapshots", "data" };

    private static void Junction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, target }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }

    [Fact]
    public void An_existing_repository_has_every_folder_made_and_held()
    {
        using var ws = new TempWorkspace("repo-hold");
        var repository = ws.Sub("repo");
        File.WriteAllText(Path.Combine(repository, "config"), "c");
        Directory.CreateDirectory(Path.Combine(repository, "data", "ab"));
        // "locks" was deleted between runs by someone else: it is made again, under the hold.
        using (RepositoryHold.For(repository, create: false))
        {
            foreach (var name in Standard) Assert.True(Directory.Exists(Path.Combine(repository, name)), name);
            Assert.Equal(256, Directory.GetDirectories(Path.Combine(repository, "data")).Length);
            var locks = Path.Combine(repository, "locks");
            // Neither emptied (its guard file can't be removed) nor moved away.
            var guard = Assert.Single(Directory.GetFiles(locks));
            Assert.StartsWith(RepositoryHold.GuardPrefix, Path.GetFileName(guard));
            Assert.ThrowsAny<Exception>(() => File.Delete(guard));
            Assert.Throws<IOException>(() => Directory.Move(locks, locks + "-away"));
            Assert.ThrowsAny<Exception>(() => Directory.Delete(locks));
            // restic can still write, rename and remove its own files there.
            File.WriteAllText(Path.Combine(locks, "tmp"), "lock");
            File.Move(Path.Combine(locks, "tmp"), Path.Combine(locks, "0123"));
            File.Delete(Path.Combine(locks, "0123"));
            // "data" holds no guard file (restic opens everything there as a
            // folder and fails on it), yet it can't be moved or emptied: its
            // 256 held subfolders keep it.
            var data = Path.Combine(repository, "data");
            Assert.Empty(Directory.GetFiles(data));
            Assert.Throws<IOException>(() => Directory.Move(data, data + "-away"));
            Assert.ThrowsAny<Exception>(() => Directory.Delete(Path.Combine(data, "ab")));
        }
        Assert.Empty(Directory.GetFiles(Path.Combine(repository, "locks"))); // guard files go with the hold
    }

    [Fact]
    public void A_first_backup_gets_its_folders_made_under_the_hold()
    {
        using var ws = new TempWorkspace("repo-hold-new");
        var repository = Path.Combine(ws.Root, "Backups", "PC");
        using (RepositoryHold.For(repository, create: true))
        {
            foreach (var name in Standard) Assert.True(Directory.Exists(Path.Combine(repository, name)), name);
            Assert.Throws<IOException>(() => Directory.Move(repository, repository + "-away"));
        }
        // Without create (a check or a restore), nothing is made where there is no backup.
        var other = Path.Combine(ws.Root, "Elsewhere", "PC");
        using (RepositoryHold.For(other, create: false)) { }
        Assert.False(Directory.Exists(Path.Combine(ws.Root, "Elsewhere")));
    }

    [Fact]
    public void A_folder_that_holds_something_else_gets_no_repository_folders()
    {
        using var ws = new TempWorkspace("repo-hold-other");
        var folder = ws.Sub("Documents");
        File.WriteAllText(Path.Combine(folder, "letter.txt"), "mine");
        using (RepositoryHold.For(folder, create: true)) { }
        Assert.False(Directory.Exists(Path.Combine(folder, "locks")));
    }

    [Fact]
    public void A_link_planted_where_a_folder_belongs_stops_the_run()
    {
        using var ws = new TempWorkspace("repo-hold-link");
        var repository = ws.Sub("repo");
        File.WriteAllText(Path.Combine(repository, "config"), "c");
        var elsewhere = ws.Sub("elsewhere");
        File.WriteAllText(Path.Combine(elsewhere, "precious.txt"), "keep");
        Junction(Path.Combine(repository, "locks"), elsewhere);
        try
        {
            Assert.Throws<SecurityException>(() => RepositoryHold.For(repository, create: false));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(elsewhere, "precious.txt")));
            // Nothing stays held after the refusal.
            Directory.Move(repository, repository + "-moved");
            Directory.Move(repository + "-moved", repository);
        }
        finally { Directory.Delete(Path.Combine(repository, "locks")); }
    }
}
