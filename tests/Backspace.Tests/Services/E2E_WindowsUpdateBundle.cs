using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class E2E_WindowsUpdateBundle
{
    [Fact]
    public async Task Recovery_launcher_waits_for_native_failure_and_preserves_the_installation()
    {
        var candidate = Environment.GetEnvironmentVariable("BACKSPACE_UPDATE_CANDIDATE");
        Assert.True(OperatingSystem.IsWindows() && Directory.Exists(candidate),
            "Set BACKSPACE_UPDATE_CANDIDATE to a published, framework-dependent Windows release folder.");
        using var ws = new TempWorkspace("native-recovery");
        var root = ws.Sub("café 日本 & ! % ' (recovery)");
        var exe = Path.Combine(root, "Backspace.exe");
        File.Copy(Path.Combine(candidate!, "Backspace.exe"), exe);
        var before = WindowsUpdatePackage.Hash(exe);
        var plan = new WindowsUpdatePackage.Plan { Root = root, Work = WindowsUpdatePackage.CreateWorkDirectory(root) };
        WindowsUpdatePackage.SavePlan(plan);
        WindowsUpdateRuntime.WriteRecoveryLauncher(plan);
        // A genuine release WinExe must fail before touching config or
        // starting its GUI. The old launcher returned zero immediately.
        using var busy = WindowsUpdateRuntime.AcquireShared(root, TimeSpan.Zero);
        var launcher = Directory.GetFiles(root, "Backspace-recovery-*.cmd").Single();
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            Arguments = "/d /s /c \"\"" + launcher + "\"\"",
        };
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        await child.StandardInput.WriteLineAsync(); // Dismiss only this owned launcher's failure pause.
        child.StandardInput.Close();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, child.ExitCode);
        var launchOutput = await stderr + await stdout;
        Assert.True(launchOutput.Contains("Close other Backspace processes before recovery", StringComparison.Ordinal), launchOutput);
        Assert.Equal(before, WindowsUpdatePackage.Hash(exe));
        Assert.Equal("Prepared", WindowsUpdateRuntime.ReadPlan(Path.Combine(plan.Work, "plan.json")).State);
        Assert.False(Directory.Exists(Path.Combine(root, "Backspace.config")));
    }

    [Fact]
    public async Task Real_portable_bundle_download_install_launch_and_rollback()
    {
        var candidate = Environment.GetEnvironmentVariable("BACKSPACE_UPDATE_CANDIDATE");
        Assert.True(OperatingSystem.IsWindows() && Directory.Exists(candidate),
            "Set BACKSPACE_UPDATE_CANDIDATE to a published, framework-dependent Windows release folder.");
        var product = FileVersionInfo.GetVersionInfo(Path.Combine(candidate!, "Backspace.exe")).ProductVersion!.Split('+')[0];
        // Prereleases use the stable numeric part for this package-integrity
        // test; the production release selector excludes prereleases entirely.
        var version = WindowsUpdatePackage.Normalize(Version.Parse(product.Split('-')[0]));
        var previous = Environment.GetEnvironmentVariable("BACKSPACE_UPDATE_PREVIOUS");
        if (!string.IsNullOrWhiteSpace(previous))
        {
            Assert.True(Directory.Exists(previous), "BACKSPACE_UPDATE_PREVIOUS must name a published Windows bundle.");
            var previousProduct = FileVersionInfo.GetVersionInfo(Path.Combine(previous, "Backspace.exe")).ProductVersion!.Split('+')[0];
            Assert.True(Version.Parse(previousProduct.Split('-')[0]) < version,
                "The previous bundle must be older, so this check proves an actual version change and rollback.");
        }
        using var ws = new TempWorkspace("native-update");
        var root = ws.Sub("café 日本 & ! (portable)");
        foreach (var name in new[] { "Backspace.exe", "backrest.exe", "restic.exe", "rclone.exe" })
            File.Copy(Path.Combine(string.IsNullOrWhiteSpace(previous) ? candidate! : previous, name), Path.Combine(root, name));
        var original = Directory.GetFiles(root).ToDictionary(p => Path.GetFileName(p)!, WindowsUpdatePackage.Hash);
        if (!string.IsNullOrWhiteSpace(previous))
            Assert.NotEqual(original["Backspace.exe"], WindowsUpdatePackage.Hash(Path.Combine(candidate!, "Backspace.exe")));
        var config = Path.Combine(root, "Backspace.config"); Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "secrets.bin"), "untouched encrypted fixture");
        var archive = Path.Combine(ws.Root, "candidate.zip");
        ZipFile.CreateFromDirectory(candidate!, archive, CompressionLevel.Fastest, false);
        using var http = new HttpClient(new FileHandler(archive));
        var release = new WindowsUpdatePackage.Release(version, "v" + version,
            WindowsUpdatePackage.ReleasesUrl, "test fixture", "https://github.com/test-fixture",
            WindowsUpdatePackage.Hash(archive), new FileInfo(archive).Length);
        if (product.Contains('-'))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => WindowsUpdatePackage.DownloadAsync(http, release, Path.Combine(root, "Backspace.exe"), null, default));
            foreach (var item in original) Assert.Equal(item.Value, WindowsUpdatePackage.Hash(Path.Combine(root, item.Key!)));
            return; // A prerelease executable cannot masquerade as a stable update.
        }
        var plan = await WindowsUpdatePackage.DownloadAsync(http, release, Path.Combine(root, "Backspace.exe"), null, default);
        WindowsUpdateRuntime.WriteRecoveryLauncher(plan);
        WindowsUpdatePackage.Install(plan, exe =>
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--version");
            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync();
            Assert.True(child.WaitForExit(30000), "Installed native executable did not finish --version.");
            Assert.Equal(0, child.ExitCode);
            Assert.Contains(version.ToString(), output.GetAwaiter().GetResult());
        });
        Assert.Equal("Installed", plan.State);
        Assert.Equal(WindowsUpdatePackage.Hash(Path.Combine(candidate!, "Backspace.exe")), WindowsUpdatePackage.Hash(Path.Combine(root, "Backspace.exe")));
        Assert.Equal("untouched encrypted fixture", File.ReadAllText(Path.Combine(config, "secrets.bin")));
        WindowsUpdatePackage.RollBack(plan);
        foreach (var item in original) Assert.Equal(item.Value, WindowsUpdatePackage.Hash(Path.Combine(root, item.Key!)));
        Assert.Equal("untouched encrypted fixture", File.ReadAllText(Path.Combine(config, "secrets.bin")));
    }

    private sealed class FileHandler(string path) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(path)) });
    }
}
