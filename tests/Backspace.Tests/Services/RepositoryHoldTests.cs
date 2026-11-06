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

    /// <summary>The shape of a restic repository: a config and a keys folder.</summary>
    private static void Restic(string repository)
    {
        File.WriteAllText(Path.Combine(repository, "config"), "c");
        Directory.CreateDirectory(Path.Combine(repository, "keys"));
    }

    /// <summary>Another program's backup storage (Duplicacy's has a config
    /// file, chunks and snapshots) gets no restic folders, even for a backup:
    /// restic's init refuses it, so nothing would make them.</summary>
    [Fact]
    public void Another_programs_backup_storage_gets_no_restic_folders()
    {
        using var ws = new TempWorkspace("repo-hold-duplicacy");
        var storage = ws.Sub("storage");
        File.WriteAllText(Path.Combine(storage, "config"), "duplicacy");
        Directory.CreateDirectory(Path.Combine(storage, "chunks", "ab"));
        Directory.CreateDirectory(Path.Combine(storage, "snapshots"));
        using (RepositoryHold.For(storage, create: true))
            Assert.Throws<IOException>(() => Directory.Move(storage, storage + "-away")); // still held
        Assert.Equal(new[] { "chunks", "snapshots" }, Directory.GetDirectories(storage).Select(Path.GetFileName).Order().ToArray());
    }

    [Fact]
    public void An_existing_repository_has_every_folder_made_and_held()
    {
        using var ws = new TempWorkspace("repo-hold");
        var repository = ws.Sub("repo");
        Restic(repository);
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

    /// <summary>Round 4: another account can drop a file into the folder
    /// before the first backup. restic's init would still make its folders
    /// there, unheld, so a backup makes and holds them first.</summary>
    [Fact]
    public void A_first_backup_into_a_folder_that_already_holds_something_gets_held_folders()
    {
        using var ws = new TempWorkspace("repo-hold-other");
        var folder = ws.Sub("Backups");
        File.WriteAllText(Path.Combine(folder, "dropped.txt"), "planted before the first backup");
        // A listing or restore makes nothing where there is no repository.
        using (RepositoryHold.For(folder, create: false)) { }
        Assert.False(Directory.Exists(Path.Combine(folder, "locks")));
        using (RepositoryHold.For(folder, create: true))
        {
            var locks = Path.Combine(folder, "locks");
            Assert.True(Directory.Exists(locks));
            Assert.Throws<IOException>(() => Directory.Move(locks, locks + "-away"));
        }
        Assert.Equal("planted before the first backup", File.ReadAllText(Path.Combine(folder, "dropped.txt")));
    }

    /// <summary>Round 5: Backrest goes on running restic by itself after a
    /// call returns (indexing, statistics). A call that ends while the engine
    /// runs leaves the backup held until the engine stops.</summary>
    [Fact]
    public void A_backup_used_while_the_engine_runs_stays_held_until_the_engine_stops()
    {
        using var ws = new TempWorkspace("repo-hold-session");
        var repository = ws.Sub("repo");
        Restic(repository);
        var locks = Path.Combine(repository, "locks");
        var previous = RepositoryHold.EngineRunning;
        try
        {
            RepositoryHold.EngineRunning = () => true;
            RepositoryHold.Use(repository).Dispose();
            RepositoryHold.Use(repository).Dispose(); // a second call: still one hold
            Assert.Single(Directory.GetFiles(locks));
            Assert.Throws<IOException>(() => Directory.Move(locks, locks + "-away"));
            RepositoryHold.EngineRunning = () => false;
            RepositoryHold.EndEngineSession();
            Assert.Empty(Directory.GetFiles(locks));
            Directory.Move(locks, locks + "-away"); // released with the engine
        }
        finally { RepositoryHold.EngineRunning = previous; RepositoryHold.EndEngineSession(); }
    }

    /// <summary>Round 5: Backrest unlocks interrupted repositories on its own
    /// as it starts. Every repository on this PC in its configuration is held
    /// for the start; clouds and shares are left out; a planted link stops it.</summary>
    [Fact]
    public void Every_local_repository_in_the_engine_configuration_is_held_for_its_start()
    {
        using var ws = new TempWorkspace("repo-hold-startup");
        var repository = ws.Sub("repo");
        Restic(repository);
        var configPath = Path.Combine(ws.Root, "config.json");
        File.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(new { repos = new object[] {
            new { id = "local", uri = repository },
            new { id = "cloud", uri = "rclone:gdrive:Backups" },
            new { id = "share", uri = @"\\server\share\repo" } } }));
        var holds = Backspace.Services.BackrestSidecar.HoldLocalRepositories(configPath, TestContext.Current.CancellationToken);
        try
        {
            Assert.Single(holds);
            Assert.Throws<IOException>(() => Directory.Move(Path.Combine(repository, "locks"), Path.Combine(repository, "locks-away")));
        }
        finally { foreach (var hold in holds) hold.Dispose(); }

        var elsewhere = ws.Sub("elsewhere");
        Directory.Delete(Path.Combine(repository, "locks"));
        Junction(Path.Combine(repository, "locks"), elsewhere);
        try { Assert.Throws<SecurityException>(() => Backspace.Services.BackrestSidecar.HoldLocalRepositories(configPath, TestContext.Current.CancellationToken)); }
        finally { Directory.Delete(Path.Combine(repository, "locks")); }
    }

    /// <summary>Round 5: a folder with no repository yet is still held while
    /// the engine looks at it.</summary>
    [Fact]
    public void A_folder_without_a_repository_is_held_while_it_is_looked_at()
    {
        using var ws = new TempWorkspace("repo-hold-empty");
        var folder = ws.Sub("Backups");
        using (RepositoryHold.For(folder, create: false))
        {
            Assert.Throws<IOException>(() => Directory.Move(folder, folder + "-away"));
            Assert.False(Directory.Exists(Path.Combine(folder, "locks")));
        }
        Directory.Move(folder, folder + "-away");
    }

    /// <summary>Round 4: every engine call holds the repository; calls on the
    /// same one share a hold, kept until the last of them ends.</summary>
    [Fact]
    public void Engine_calls_on_one_repository_share_a_hold_until_the_last_ends()
    {
        using var ws = new TempWorkspace("repo-hold-shared");
        var repository = ws.Sub("repo");
        Restic(repository);
        var destination = new Backspace.Models.Destination { Name = "Local", Kind = Backspace.Models.DestinationKind.LocalFolder, PathOrSubpath = repository };
        var locks = Path.Combine(repository, "locks");
        var first = RepositoryHold.Use(destination)!;
        var second = RepositoryHold.Use(destination)!;
        Assert.Single(Directory.GetFiles(locks)); // one hold, one guard
        first.Dispose();
        Assert.Throws<IOException>(() => Directory.Move(locks, locks + "-away"));
        second.Dispose();
        Assert.Empty(Directory.GetFiles(locks));
        Directory.Move(locks, locks + "-away"); // released
        Assert.Null(RepositoryHold.Use(new Backspace.Models.Destination { Name = "Cloud", Kind = Backspace.Models.DestinationKind.GoogleDrive }));
    }

    [Fact]
    public void A_link_planted_where_a_folder_belongs_stops_the_run()
    {
        using var ws = new TempWorkspace("repo-hold-link");
        var repository = ws.Sub("repo");
        Restic(repository);
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
