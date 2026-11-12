using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class WindowsUpdateTests
{
    internal static string ReleaseJson(string digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", string tag = "v99.0.0",
        bool prerelease = false, string? assetUrl = null, long size = 20) => JsonSerializer.Serialize(new
    {
        tag_name = tag, draft = false, prerelease,
        html_url = "https://github.com/backspace-backup/backspace/releases/tag/" + tag,
        body = "A clearer interface and safer updates.",
        assets = new[] { new { name = WindowsUpdatePackage.AssetName, size, digest,
            browser_download_url = assetUrl ?? "https://github.com/backspace-backup/backspace/releases/download/" + tag + "/" + WindowsUpdatePackage.AssetName } }
    });

    /// <summary>U102 (3 November 2026): releases offer Backspace.exe alone; the
    /// updater installs that file, and still reads a release with the archive.</summary>
    [Fact]
    public void A_release_offering_Backspace_exe_is_installed_from_that_file()
    {
        const string tag = "v99.0.0";
        var json = JsonSerializer.Serialize(new
        {
            tag_name = tag, draft = false, prerelease = false,
            html_url = "https://github.com/backspace-backup/backspace/releases/tag/" + tag, body = "",
            assets = new[] { new { name = "Backspace.exe", size = 20L, digest = "sha256:" + new string('b', 64),
                browser_download_url = "https://github.com/backspace-backup/backspace/releases/download/" + tag + "/Backspace.exe" } },
        });
        var release = WindowsUpdatePackage.ParseRelease(json, new Version(1, 0))!;
        Assert.True(release.Executable);
        Assert.EndsWith("/Backspace.exe", release.AssetUrl);
        Assert.False(WindowsUpdatePackage.ParseRelease(ReleaseJson(), new Version(1, 0))!.Executable);

        using var ws = new TempWorkspace();
        var root = ws.Sub("portable");
        var work = WindowsUpdatePackage.CreateWorkDirectory(root);
        var download = Path.Combine(work, "download.exe");
        File.WriteAllBytes(download, "MZnew Backspace"u8.ToArray());
        var plan = WindowsUpdatePackage.Stage(download, root, work, "Backspace.exe", "99.0.0");
        Assert.Equal("Backspace.exe", Assert.Single(plan.Files).Name);
        File.WriteAllBytes(Path.Combine(work, "not-a-program.exe"), "PK zip"u8.ToArray());
        var other = WindowsUpdatePackage.CreateWorkDirectory(root);
        File.WriteAllBytes(Path.Combine(other, "download.exe"), "PK zip"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => WindowsUpdatePackage.Stage(Path.Combine(other, "download.exe"), root, other, "Backspace.exe", "99.0.0"));
    }

    [Fact]
    public void Release_requires_a_new_stable_version_and_preserves_notes()
    {
        var release = WindowsUpdatePackage.ParseRelease(ReleaseJson(), new Version(1, 0, 0, 0));
        Assert.Equal(new Version(99, 0, 0), release!.Version);
        Assert.Contains("safer updates", release.Notes);
        Assert.Null(WindowsUpdatePackage.ParseRelease(ReleaseJson(tag: "v1.0.0"), new Version(1, 0, 0, 0)));
        Assert.Null(WindowsUpdatePackage.ParseRelease(ReleaseJson(prerelease: true), new Version(1, 0)));
        Assert.Null(WindowsUpdatePackage.ParseRelease(ReleaseJson(tag: "v99.0.0-rc1"), new Version(1, 0)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha256:abc")]
    [InlineData("sha512:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Missing_or_malformed_checksum_prevents_automatic_install(string digest) =>
        Assert.Throws<InvalidDataException>(() => WindowsUpdatePackage.ParseRelease(ReleaseJson(digest), new Version(1, 0)));

    [Theory]
    [InlineData("http://github.com/backspace-backup/backspace/releases/download/v99.0.0/Backspace-windows-x64.zip")]
    [InlineData("https://github.com/other/repo/releases/download/v99.0.0/Backspace-windows-x64.zip")]
    [InlineData("https://github.com/backspace-backup/backspace/releases/download/v98.0.0/Backspace-windows-x64.zip")]
    [InlineData("https://github.com/backspace-backup/backspace/releases/download/v99.0.0/Backspace-windows-x64.zip?redirect=elsewhere")]
    public void Downloads_must_belong_to_the_exact_official_release(string url) =>
        Assert.Throws<InvalidDataException>(() => WindowsUpdatePackage.ParseRelease(ReleaseJson(assetUrl: url), new Version(1, 0)));

    [Theory]
    [InlineData("../Backspace.exe")]
    [InlineData("Backspace.config/vault.bin")]
    [InlineData("C:/Backspace.exe")]
    [InlineData("Backspace.exe:secret")]
    [InlineData("unrelated.exe")]
    public void Unsafe_archive_paths_are_rejected_without_changing_the_installation(string entry)
    {
        using var ws = new TempWorkspace();
        var root = ws.Sub("app"); File.WriteAllText(Path.Combine(root, "Backspace.exe"), "old program");
        var work = WindowsUpdatePackage.CreateWorkDirectory(root);
        var archive = Path.Combine(work, "download.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var stream = zip.CreateEntry(entry).Open()) stream.Write("MZbad"u8);
        Assert.Throws<InvalidDataException>(() => WindowsUpdatePackage.Extract(archive, root, work, "Backspace.exe", "99.0.0", default));
        Assert.Equal("old program", File.ReadAllText(Path.Combine(root, "Backspace.exe")));
    }

    private static WindowsUpdatePackage.Plan Prepare(string root, string extra = "new-library.dll")
    {
        var config = Path.Combine(root, "Backspace.config"); Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "vault.bin"), "credentials must remain byte-for-byte unchanged");
        File.WriteAllText(Path.Combine(root, "personal-notes.txt"), "unrelated");
        var work = WindowsUpdatePackage.CreateWorkDirectory(root);
        var archive = Path.Combine(work, "download.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "Backspace.exe", "restic.exe", "rclone.exe", extra })
            {
                if (name != extra) File.WriteAllText(Path.Combine(root, name), "old " + name);
                using var file = zip.CreateEntry(name).Open(); file.Write(Encoding.UTF8.GetBytes("MZnew " + name));
            }
        }
        return WindowsUpdatePackage.Extract(archive, root, work, "Backspace.exe", "99.0.0", default);
    }

    [Fact]
    public void Complete_bundle_replacement_preserves_settings_unrelated_files_and_previous_version()
    {
        using var ws = new TempWorkspace(); var root = ws.Sub("app"); var plan = Prepare(root);
        string? launched = null;
        WindowsUpdatePackage.Install(plan, path => launched = path);
        Assert.Equal(Path.Combine(root, "Backspace.exe"), launched);
        foreach (var f in plan.Files) Assert.Equal("MZnew " + f.Name, File.ReadAllText(Path.Combine(root, f.Name)));
        Assert.Equal("old Backspace.exe", File.ReadAllText(Path.Combine(plan.Work, "previous", "Backspace.exe")));
        Assert.Equal("credentials must remain byte-for-byte unchanged", File.ReadAllText(Path.Combine(root, "Backspace.config", "vault.bin")));
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(root, "personal-notes.txt")));
        Assert.Equal("Installed", WindowsUpdateRuntime.ReadPlan(Path.Combine(plan.Work, "plan.json")).State);
    }

    [Fact]
    public void Plan_save_retries_a_temporary_windows_file_access_conflict()
    {
        using var ws = new TempWorkspace(); var root = ws.Sub("app"); var plan = Prepare(root);
        var planPath = Path.Combine(plan.Work, "plan.json");
        var original = File.ReadAllText(planPath);
        plan.State = "Installing";
        var attempts = 0;
        var delays = new List<int>();

        WindowsUpdatePackage.SavePlan(plan,
            (source, destination, overwrite) =>
            {
                if (++attempts < 3)
                    throw new IOException("Synthetic sharing violation", unchecked((int)0x80070020));
                File.Move(source, destination, overwrite);
            }, delays.Add);

        Assert.Equal(3, attempts);
        Assert.Equal(new[] { 50, 100 }, delays);
        Assert.Equal("Installing", WindowsUpdateRuntime.ReadPlan(planPath).State);
        Assert.False(File.Exists(planPath + ".new"));
        Assert.NotEqual(original, File.ReadAllText(planPath));
    }

    [Fact]
    public void Plan_save_keeps_the_old_record_and_staged_copy_when_access_stays_blocked()
    {
        using var ws = new TempWorkspace(); var root = ws.Sub("app"); var plan = Prepare(root);
        var planPath = Path.Combine(plan.Work, "plan.json");
        var original = File.ReadAllText(planPath);
        plan.State = "RolledBack";
        var staged = JsonSerializer.Serialize(plan);
        var attempts = 0;

        // Blocked for a long time: it keeps trying (U135) until stopped.
        Assert.Throws<OperationCanceledException>(() => WindowsUpdatePackage.SavePlan(plan,
            (_, _, _) =>
            {
                attempts++;
                throw new IOException("Synthetic sharing violation", unchecked((int)0x80070020));
            }, _ => { if (attempts == 40) throw new OperationCanceledException(); }));

        Assert.Equal(40, attempts);
        Assert.Equal(original, File.ReadAllText(planPath));
        Assert.Equal(staged, File.ReadAllText(planPath + ".new"));
    }

    [Theory]
    // Two moves for each of the bundle's four files (Backspace.exe, restic.exe,
    // rclone.exe and a new library): every step.
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void Failure_after_every_replacement_step_restores_the_complete_previous_bundle(int failAfter)
    {
        using var ws = new TempWorkspace(); var root = ws.Sub("app"); var plan = Prepare(root);
        bool launched = false;
        Assert.Throws<IOException>(() => WindowsUpdatePackage.Install(plan, _ => launched = true,
            n => { if (n == failAfter) throw new IOException("Injected disk failure"); }));
        Assert.False(launched);
        foreach (var name in new[] { "Backspace.exe", "restic.exe", "rclone.exe" })
            Assert.Equal("old " + name, File.ReadAllText(Path.Combine(root, name)));
        Assert.False(File.Exists(Path.Combine(root, "new-library.dll")));
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(root, "personal-notes.txt")));
        Assert.Equal("RolledBack", plan.State);
    }

    [Fact]
    public void Failed_launch_restores_old_files_and_later_recovery_cannot_overwrite_a_newer_installation()
    {
        using var ws = new TempWorkspace(); var root = ws.Sub("app"); var plan = Prepare(root);
        Assert.Throws<IOException>(() => WindowsUpdatePackage.Install(plan, _ => throw new IOException("Cannot launch")));
        Assert.Equal("old Backspace.exe", File.ReadAllText(Path.Combine(root, "Backspace.exe")));
        WindowsUpdatePackage.Install(plan, _ => { });
        File.WriteAllText(Path.Combine(root, "Backspace.exe"), "a later application version");
        Assert.Throws<IOException>(() => WindowsUpdatePackage.RollBack(plan));
        Assert.Equal("a later application version", File.ReadAllText(Path.Combine(root, "Backspace.exe")));
    }

    [Fact]
    public void Modified_staging_file_is_rejected_before_any_installed_file_moves()
    {
        using var ws = new TempWorkspace(); var root = ws.Sub("app"); var plan = Prepare(root);
        File.WriteAllText(Path.Combine(plan.Work, "incoming", "restic.exe"), "changed after verification");
        Assert.Throws<InvalidDataException>(() => WindowsUpdatePackage.Install(plan, _ => Assert.Fail("Must not launch")));
        Assert.Equal("old Backspace.exe", File.ReadAllText(Path.Combine(root, "Backspace.exe")));
        Assert.Equal("old restic.exe", File.ReadAllText(Path.Combine(root, "restic.exe")));
    }

    [Fact]
    public void A_saved_interrupted_install_can_be_rolled_back_without_configuration_changes()
    {
        using var ws = new TempWorkspace(); var root = ws.Sub("app"); var plan = Prepare(root);
        plan.PreviouslyPresent = plan.Files.Where(f => File.Exists(Path.Combine(root, f.Name))).Select(f => f.Name).ToList();
        plan.State = "Installing"; WindowsUpdatePackage.SavePlan(plan);
        Directory.CreateDirectory(Path.Combine(plan.Work, "previous"));
        File.Move(Path.Combine(root, "Backspace.exe"), Path.Combine(plan.Work, "previous", "Backspace.exe"));
        File.Move(Path.Combine(plan.Work, "incoming", "Backspace.exe"), Path.Combine(root, "Backspace.exe"));
        // Stop between moving the next old file and installing its replacement.
        File.Move(Path.Combine(root, "restic.exe"), Path.Combine(plan.Work, "previous", "restic.exe"));
        var recovered = WindowsUpdateRuntime.ReadPlan(Path.Combine(plan.Work, "plan.json"));
        WindowsUpdatePackage.RollBack(recovered);
        Assert.Equal("old Backspace.exe", File.ReadAllText(Path.Combine(root, "Backspace.exe")));
        Assert.Equal("old restic.exe", File.ReadAllText(Path.Combine(root, "restic.exe")));
        Assert.Equal("credentials must remain byte-for-byte unchanged", File.ReadAllText(Path.Combine(root, "Backspace.config", "vault.bin")));
    }

    [Fact]
    public async Task A_truncated_or_wrong_hash_download_never_replaces_the_app()
    {
        using var ws = new TempWorkspace(); var root = ws.Sub("app"); var exe = Path.Combine(root, "Backspace.exe");
        File.WriteAllText(exe, "old application");
        foreach (var size in new[] { 20L, 3L })
        {
            using var http = new HttpClient(new ReplyHandler("bad"));
            var release = WindowsUpdatePackage.ParseRelease(ReleaseJson(size: size), new Version(1, 0))!;
            await Assert.ThrowsAsync<InvalidDataException>(() => WindowsUpdatePackage.DownloadAsync(http, release, exe, null, default));
            Assert.Equal("old application", File.ReadAllText(exe));
            Assert.Empty(Directory.GetDirectories(WindowsUpdateRuntime.UpdatesFolder(root)));
        }
    }

    /// <summary>U218 (owner, 12 November 2026): "we should only ever have
    /// Backspace.exe and Backspace.config. No clutter, even temporarily."
    /// A whole update, with the gate held and its recovery launcher written,
    /// leaves nothing else beside the app.</summary>
    [Fact]
    public void An_update_leaves_only_the_app_and_Backspace_config_beside_it()
    {
        using var ws = new TempWorkspace(); var root = ws.Sub("app");
        var plan = Prepare(root);
        File.Delete(Path.Combine(root, "personal-notes.txt"));
        using (var gate = WindowsUpdateRuntime.AcquireShared(root, TimeSpan.Zero))
        {
            Assert.NotNull(gate);
            WindowsUpdateRuntime.WriteRecoveryLauncher(plan);
            WindowsUpdatePackage.Install(plan, _ => { });
            WindowsUpdateRuntime.WriteResult(root, "Updated.", true);
            Assert.Equal(new[] { "Backspace.config" }, Directory.GetDirectories(root).Select(Path.GetFileName));
            Assert.Equal(new[] { "Backspace.exe", "new-library.dll", "rclone.exe", "restic.exe" }, Directory.GetFiles(root).Select(Path.GetFileName).Order());
        }
        Assert.True(WindowsUpdateRuntime.MayReplaceIn(root));
        Assert.False(WindowsUpdateRuntime.MayReplaceIn(Path.Combine(root, "missing")));

    }

    [Fact]
    public void Runtime_gate_protects_active_readers_and_reopens_after_they_exit()
    {
        using var ws = new TempWorkspace(); var root = ws.Sub("app");
        var first = WindowsUpdateRuntime.AcquireShared(root, TimeSpan.Zero);
        var second = WindowsUpdateRuntime.AcquireShared(root, TimeSpan.Zero);
        Assert.NotNull(first); Assert.NotNull(second); Assert.Null(WindowsUpdateRuntime.TryExclusive(root, TimeSpan.Zero));
        first!.Dispose(); Assert.Null(WindowsUpdateRuntime.TryExclusive(root, TimeSpan.Zero)); second!.Dispose();
        using var writer = WindowsUpdateRuntime.TryExclusive(root); Assert.NotNull(writer);
        Assert.Throws<IOException>(() => WindowsUpdateRuntime.AcquireShared(root, TimeSpan.Zero));
    }

    internal sealed class ReplyHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) });
    }

    /// <summary>U218: a process that dies holding the gate (a crash, a power
    /// cut) leaves nothing stale: Windows releases its mutex and the update
    /// can go ahead at once. A lock file could not promise that.</summary>
    [Fact]
    public async Task A_process_killed_while_holding_the_gate_never_blocks_an_update()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var ws = new TempWorkspace(); var root = ws.Sub("app");
        var ready = Path.Combine(root, "ready");
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var script = "$m=[Threading.Mutex]::new($false," + Q(RuntimeGate.SlotName(root, 0)) + "); [void]$m.WaitOne(0); [IO.File]::WriteAllText("
            + Q(ready) + ",'ready'); Start-Sleep -Seconds 60";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var child = Process.Start(start)!;
        for (var i = 0; i < 200 && !File.Exists(ready) && !child.HasExited; i++) await Task.Delay(50);
        Assert.True(File.Exists(ready), "The child did not take the gate.");
        Assert.Null(WindowsUpdateRuntime.TryExclusive(root, TimeSpan.Zero));
        child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        using (var writer = WindowsUpdateRuntime.TryExclusive(root)) Assert.NotNull(writer);
        // And nothing was written for it: the folder holds only the test's marker.
        Assert.Equal(new[] { "ready" }, Directory.GetFileSystemEntries(root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Another_real_process_prevents_replacement_until_it_exits()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var ws = new TempWorkspace(); var root = ws.Sub("app");
        var ready = Path.Combine(root, "ready"); var done = Path.Combine(root, "done");
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        // U218: another process owns a slot mutex, as Backspace does while it runs.
        var script = "$m=[Threading.Mutex]::new($false," + Q(RuntimeGate.SlotName(root, 0)) + "); [void]$m.WaitOne(0); try { [IO.File]::WriteAllText("
            + Q(ready) + ",'ready'); $end=[DateTime]::UtcNow.AddSeconds(20); while(-not [IO.File]::Exists("
            + Q(done) + ") -and [DateTime]::UtcNow -lt $end) { Start-Sleep -Milliseconds 50 } } finally { $m.ReleaseMutex(); $m.Dispose() }";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var child = Process.Start(start)!;
        try
        {
            for (var i = 0; i < 100 && !File.Exists(ready) && !child.HasExited; i++) await Task.Delay(50);
            Assert.True(File.Exists(ready), "Owned child did not acquire the shared runtime lease.");
            Assert.Null(WindowsUpdateRuntime.TryExclusive(root, TimeSpan.Zero));
            Assert.False(child.HasExited);
        }
        finally { File.WriteAllText(done, "finish"); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25)); }
        Assert.Equal(0, child.ExitCode);
        using var writer = WindowsUpdateRuntime.TryExclusive(root); Assert.NotNull(writer);
    }

    [Fact]
    public void Recovery_launcher_encodes_unicode_and_shell_metacharacters_as_data()
    {
        using var ws = new TempWorkspace(); var root = ws.Sub("café 日本 & ! % ' (app)"); var plan = Prepare(root);
        WindowsUpdateRuntime.WriteRecoveryLauncher(plan);
        var script = File.ReadAllText(Path.Combine(plan.Work, "Recover Backspace.cmd"));
        Assert.All(script, c => Assert.True(c < 128));
        var encoded = script.Split("-EncodedCommand ")[1].Split('\r')[0];
        var command = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.Contains(root.Replace("'", "''"), command);
        Assert.Contains("--recover-update", command);
    }

    [Fact]
    public void Recovery_runner_survives_replacement_and_rollback_without_being_moved()
    {
        using var ws = new TempWorkspace(); var root = ws.Sub("app"); var plan = Prepare(root);
        var original = File.ReadAllBytes(Path.Combine(root, plan.ExeName));
        WindowsUpdateRuntime.WriteRecoveryLauncher(plan);
        var runner = Path.Combine(plan.Work, "recovery-runner.exe");
        Assert.Equal(original, File.ReadAllBytes(runner));
        WindowsUpdatePackage.Install(plan, _ => { });
        Assert.NotEqual(original, File.ReadAllBytes(Path.Combine(root, plan.ExeName)));
        Assert.Equal(original, File.ReadAllBytes(runner));
        WindowsUpdatePackage.RollBack(plan);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(root, plan.ExeName)));
        Assert.Equal(original, File.ReadAllBytes(runner));
    }

    [Fact]
    public void Preparing_recovery_again_preserves_a_matching_runner_and_rejects_a_changed_one()
    {
        using var ws = new TempWorkspace(); var root = ws.Sub("app"); var plan = Prepare(root);
        var original = File.ReadAllBytes(Path.Combine(root, plan.ExeName));
        WindowsUpdateRuntime.WriteRecoveryLauncher(plan);
        WindowsUpdateRuntime.WriteRecoveryLauncher(plan);
        var runner = Path.Combine(plan.Work, "recovery-runner.exe");
        Assert.Equal(original, File.ReadAllBytes(runner));
        File.WriteAllText(runner, "changed recovery fixture");
        Assert.Throws<IOException>(() => WindowsUpdateRuntime.WriteRecoveryLauncher(plan));
        Assert.Equal("changed recovery fixture", File.ReadAllText(runner));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(root, plan.ExeName)));
        Assert.Empty(Directory.GetFiles(plan.Work, "recovery-*.tmp"));
    }

    [Fact]
    public void Recovery_does_not_discard_a_modified_new_library()
    {
        using var ws = new TempWorkspace(); var root = ws.Sub("app"); var plan = Prepare(root);
        WindowsUpdatePackage.Install(plan, _ => { });
        File.WriteAllText(Path.Combine(root, "new-library.dll"), "later change");
        Assert.Throws<IOException>(() => WindowsUpdatePackage.RollBack(plan));
        Assert.Equal("later change", File.ReadAllText(Path.Combine(root, "new-library.dll")));
        Assert.Equal("MZnew Backspace.exe", File.ReadAllText(Path.Combine(root, "Backspace.exe")));
    }

    [Fact]
    public void Cleanup_keeps_the_latest_recovery_and_never_removes_unknown_or_unfinished_work()
    {
        using var ws = new TempWorkspace(); var root = ws.Sub("app"); var old = Prepare(root);
        WindowsUpdateRuntime.WriteRecoveryLauncher(old); WindowsUpdatePackage.Install(old, _ => { });
        var unfinished = WindowsUpdatePackage.CreateWorkDirectory(root);
        WindowsUpdatePackage.SavePlan(new WindowsUpdatePackage.Plan { Root = root, Work = unfinished, State = "Installing" });
        var unknown = Path.Combine(WindowsUpdateRuntime.UpdatesFolder(root), "personal-files"); Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown, "keep.txt"), "keep");
        var current = Prepare(root); WindowsUpdateRuntime.WriteRecoveryLauncher(current);
        WindowsUpdatePackage.Install(current, _ => { });
        var cleanupErrors = new System.Collections.Generic.List<Exception>();
        WindowsUpdateRuntime.PruneOlderCompletedUpdates(current, cleanupErrors.Add);
        Assert.False(Directory.Exists(old.Work), string.Join("\n", cleanupErrors));
        Assert.True(File.Exists(Path.Combine(current.Work, "previous", "Backspace.exe")));
        Assert.False(File.Exists(Path.Combine(current.Work, "download.zip")));
        Assert.True(Directory.Exists(unfinished)); Assert.Equal("keep", File.ReadAllText(Path.Combine(unknown, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(current.Work, "Recover Backspace.cmd")));
        // U218: nothing beside the app but the app and Backspace.config.
        Assert.Equal(new[] { "Backspace.config" }, Directory.GetDirectories(root).Select(Path.GetFileName));
    }

    [Fact]
    public async System.Threading.Tasks.Task Owned_update_cleanup_retries_a_real_temporary_sharing_lock()
    {
        using var ws = new TempWorkspace(); var root = ws.Sub("app"); var plan = Prepare(root);
        using var held = new FileStream(Path.Combine(plan.Work, "download.zip"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = System.Threading.Tasks.Task.Run(async () => { await System.Threading.Tasks.Task.Delay(150); held.Dispose(); });
        var errors = new System.Collections.Generic.List<Exception>();
        WindowsUpdatePackage.SafeRemoveWork(root, plan.Work, onFailure: errors.Add);
        await release;
        Assert.Empty(errors); Assert.False(Directory.Exists(plan.Work));
    }

    [Fact]
    public void Owned_update_cleanup_retains_a_persistently_locked_file_and_reports_the_failure()
    {
        using var ws = new TempWorkspace(); var root = ws.Sub("app"); var plan = Prepare(root);
        var path = Path.Combine(plan.Work, "download.zip");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var errors = new System.Collections.Generic.List<Exception>();
        WindowsUpdatePackage.SafeRemoveWork(root, plan.Work, onFailure: errors.Add);
        Assert.Single(errors); Assert.IsAssignableFrom<IOException>(errors[0]); Assert.True(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(root, "Backspace.exe")));
    }

    private sealed class DownloadHandler(System.Net.HttpStatusCode github) : System.Net.Http.HttpMessageHandler
    {
        public System.Collections.Generic.List<string> Requests { get; } = new();
        protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.Host + " " + string.Join(",", request.Headers.Accept));
            var status = request.RequestUri.Host == "github.com" ? github : System.Net.HttpStatusCode.OK;
            return System.Threading.Tasks.Task.FromResult(new System.Net.Http.HttpResponseMessage(status) { Content = new System.Net.Http.ByteArrayContent(new byte[] { 1, 2, 3 }) });
        }
    }

    private static string WithApiUrl(string json, string api) => json.Replace("\"browser_download_url\"", "\"url\":\"" + api + "\",\"browser_download_url\"");

    /// <summary>8 November 2026: github.com answered 504 for the release file
    /// while GitHub's API served it. The updater falls back to the API; a
    /// client error (404) does not, and the API link must be this repository's.</summary>
    [Fact]
    public async System.Threading.Tasks.Task A_server_error_on_github_com_falls_back_to_the_API_asset()
    {
        var api = "https://api.github.com/repos/backspace-backup/backspace/releases/assets/123";
        var release = WindowsUpdatePackage.ParseRelease(WithApiUrl(ReleaseJson(), api), new Version(1, 0))!;
        Assert.Equal(api, release.ApiAssetUrl);

        var gateway = new DownloadHandler(System.Net.HttpStatusCode.GatewayTimeout);
        using (var response = await WindowsUpdatePackage.GetAssetAsync(new System.Net.Http.HttpClient(gateway), release, TestContext.Current.CancellationToken))
            Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(new[] { "github.com ", "api.github.com application/octet-stream" }, gateway.Requests);

        var missing = new DownloadHandler(System.Net.HttpStatusCode.NotFound);
        await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() => WindowsUpdatePackage.GetAssetAsync(new System.Net.Http.HttpClient(missing), release, TestContext.Current.CancellationToken));
        Assert.Single(missing.Requests);

        Assert.Null(WindowsUpdatePackage.ParseRelease(WithApiUrl(ReleaseJson(), "https://api.github.com/repos/someone/else/releases/assets/1"), new Version(1, 0))!.ApiAssetUrl);
        Assert.Null(WindowsUpdatePackage.ParseRelease(ReleaseJson(), new Version(1, 0))!.ApiAssetUrl);
    }
}
