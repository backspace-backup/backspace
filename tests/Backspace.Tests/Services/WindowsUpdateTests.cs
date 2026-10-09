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
    [InlineData("Backspace.config/secrets.bin")]
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
        File.WriteAllText(Path.Combine(config, "secrets.bin"), "credentials must remain byte-for-byte unchanged");
        File.WriteAllText(Path.Combine(root, "personal-notes.txt"), "unrelated");
        var work = WindowsUpdatePackage.CreateWorkDirectory(root);
        var archive = Path.Combine(work, "download.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "Backspace.exe", "backrest.exe", "restic.exe", "rclone.exe", extra })
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
        Assert.Equal("credentials must remain byte-for-byte unchanged", File.ReadAllText(Path.Combine(root, "Backspace.config", "secrets.bin")));
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(root, "personal-notes.txt")));
        Assert.Equal("Installed", WindowsUpdateRuntime.ReadPlan(Path.Combine(plan.Work, "plan.json")).State);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    public void Failure_after_every_replacement_step_restores_the_complete_previous_bundle(int failAfter)
    {
        using var ws = new TempWorkspace(); var root = ws.Sub("app"); var plan = Prepare(root);
        bool launched = false;
        Assert.Throws<IOException>(() => WindowsUpdatePackage.Install(plan, _ => launched = true,
            n => { if (n == failAfter) throw new IOException("Injected disk failure"); }));
        Assert.False(launched);
        foreach (var name in new[] { "Backspace.exe", "backrest.exe", "restic.exe", "rclone.exe" })
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
        File.Move(Path.Combine(root, "backrest.exe"), Path.Combine(plan.Work, "previous", "backrest.exe"));
        var recovered = WindowsUpdateRuntime.ReadPlan(Path.Combine(plan.Work, "plan.json"));
        WindowsUpdatePackage.RollBack(recovered);
        Assert.Equal("old Backspace.exe", File.ReadAllText(Path.Combine(root, "Backspace.exe")));
        Assert.Equal("old backrest.exe", File.ReadAllText(Path.Combine(root, "backrest.exe")));
        Assert.Equal("credentials must remain byte-for-byte unchanged", File.ReadAllText(Path.Combine(root, "Backspace.config", "secrets.bin")));
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
            Assert.Empty(Directory.GetDirectories(Path.Combine(root, ".backspace-updates")));
        }
    }

    [Fact]
    public void Runtime_gate_protects_active_readers_and_reopens_after_they_exit()
    {
        using var ws = new TempWorkspace(); var root = ws.Sub("app");
        var first = WindowsUpdateRuntime.AcquireShared(root, TimeSpan.Zero);
        var second = WindowsUpdateRuntime.AcquireShared(root, TimeSpan.Zero);
        Assert.NotNull(first); Assert.NotNull(second); Assert.Null(WindowsUpdateRuntime.TryExclusive(root));
        first!.Dispose(); Assert.Null(WindowsUpdateRuntime.TryExclusive(root)); second!.Dispose();
        using var writer = WindowsUpdateRuntime.TryExclusive(root); Assert.NotNull(writer);
        Assert.Throws<IOException>(() => WindowsUpdateRuntime.AcquireShared(root, TimeSpan.Zero));
    }

    internal sealed class ReplyHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) });
    }

    [Fact]
    public async Task Another_real_process_prevents_replacement_until_it_exits()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var ws = new TempWorkspace(); var root = ws.Sub("app");
        var ready = Path.Combine(root, "ready"); var done = Path.Combine(root, "done");
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var script = "$f=[IO.File]::Open(" + Q(Path.Combine(root, WindowsUpdateRuntime.GateName))
            + ",[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::Read,[IO.FileShare]::Read); try { [IO.File]::WriteAllText("
            + Q(ready) + ",'ready'); $end=[DateTime]::UtcNow.AddSeconds(20); while(-not [IO.File]::Exists("
            + Q(done) + ") -and [DateTime]::UtcNow -lt $end) { Start-Sleep -Milliseconds 50 } } finally { $f.Dispose() }";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var child = Process.Start(start)!;
        try
        {
            for (var i = 0; i < 100 && !File.Exists(ready) && !child.HasExited; i++) await Task.Delay(50);
            Assert.True(File.Exists(ready), "Owned child did not acquire the shared runtime lease.");
            Assert.Null(WindowsUpdateRuntime.TryExclusive(root));
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
        var script = File.ReadAllText(Directory.GetFiles(root, "Backspace-recovery-*.cmd").Single());
        Assert.All(script, c => Assert.True(c < 128));
        var encoded = script.Split("-EncodedCommand ")[1].Split('\r')[0];
        var command = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.Contains(root.Replace("'", "''"), command);
        Assert.Contains("'--recover-update'", command);
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
        var unknown = Path.Combine(root, ".backspace-updates", "personal-files"); Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown, "keep.txt"), "keep");
        var current = Prepare(root); WindowsUpdateRuntime.WriteRecoveryLauncher(current);
        WindowsUpdatePackage.Install(current, _ => { });
        WindowsUpdateRuntime.PruneOlderCompletedUpdates(current);
        Assert.False(Directory.Exists(old.Work));
        Assert.True(File.Exists(Path.Combine(current.Work, "previous", "Backspace.exe")));
        Assert.False(File.Exists(Path.Combine(current.Work, "download.zip")));
        Assert.True(Directory.Exists(unfinished)); Assert.Equal("keep", File.ReadAllText(Path.Combine(unknown, "keep.txt")));
        Assert.Single(Directory.GetFiles(root, "Backspace-recovery-*.cmd"));
    }
}
