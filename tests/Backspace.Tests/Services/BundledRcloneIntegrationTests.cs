using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

public class BundledRcloneIntegrationTests
{
    [Fact]
    public async Task RealRestic_UsesBundledRclone_WhenPathHasSpacesAndNoRcloneIsOnPath()
    {
        Assert.True(File.Exists(AppPaths.ResticBinary) && File.Exists(AppPaths.RcloneBinary), "This check requires the bundled engines.");
        var root = Path.Combine(Path.GetTempPath(), "backspace-rclone-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var binaryDir = Path.Combine(root, "Owner's portable app [test], 1");
            Directory.CreateDirectory(binaryDir);
            var rclone = Path.Combine(binaryDir, Path.GetFileName(AppPaths.RcloneBinary));
            File.Copy(AppPaths.RcloneBinary, rclone);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(rclone, File.GetUnixFileMode(AppPaths.RcloneBinary));
            var conf = Path.Combine(root, "rclone.conf");
            await File.WriteAllTextAsync(conf, "[fixture]\ntype = local\n", timeout.Token);
            var repository = "rclone:fixture:" + Path.Combine(root, "repository").Replace('\\', '/');
            async Task<(int code, string text)> Run(bool useBundled)
            {
                var start = new ProcessStartInfo(AppPaths.ResticBinary)
                {
                    WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardError = true, RedirectStandardOutput = true
                };
                start.Environment["PATH"] = OperatingSystem.IsWindows() ? Environment.SystemDirectory : "/nonexistent";
                start.Environment["RCLONE_CONFIG"] = conf;
                start.Environment["RESTIC_PASSWORD"] = "disposable-fixture-password";
                start.Environment["RESTIC_CACHE_DIR"] = Path.Combine(root, "cache");
                if (useBundled) BundledEngineEnvironment.IncludeRclone(start, rclone);
                start.ArgumentList.Add("-r"); start.ArgumentList.Add(repository); start.ArgumentList.Add("init");
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                try { await process.WaitForExitAsync(timeout.Token); }
                catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
                return (process.ExitCode, await stdout + await stderr);
            }
            var without = await Run(false);
            Assert.NotEqual(0, without.code);
            Assert.Contains("rclone", without.text);
            Assert.False(File.Exists(Path.Combine(root, "repository", "config")));
            var withBundled = await Run(true);
            Assert.True(withBundled.code == 0, withBundled.text);
            Assert.True(File.Exists(Path.Combine(root, "repository", "config")));
        }
        finally
        {
            // Windows can retain the just-exited executable's image handle
            // briefly. Keep cleanup bounded and fail if it never releases.
            for (var attempt = 0; ; attempt++)
            {
                try { Directory.Delete(root, recursive: true); break; }
                catch (Exception ex) when (attempt < 25 && ex is IOException or UnauthorizedAccessException)
                { await Task.Delay(200, TestContext.Current.CancellationToken); }
            }
        }
    }

    [Fact]
    public async Task RealBackrest_CreatesRcloneRepository_FromItsSeparateDataDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "backspace-backrest-rclone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var conf = Path.Combine(root, "rclone.conf");
            await File.WriteAllTextAsync(conf, "[fixture]\ntype = local\n", timeout.Token);
            await using var sidecar = new BackrestSidecar(AppPaths.BackrestBinary, AppPaths.ResticBinary, Path.Combine(root, "daemon data"));
            await sidecar.StartAsync(timeout.Token);
            var result = await sidecar.Client!.CallAsync<AddRepoRequest, BackrestConfig>("/v1.Backrest/AddRepo", new AddRepoRequest
            {
                Repo = new BackrestRepo
                {
                    Id = "bundled-rclone-check", Uri = "rclone:fixture:" + Path.Combine(root, "repository").Replace('\\', '/'),
                    Password = "disposable-fixture-password", Env = { "RCLONE_CONFIG=" + conf }
                }
            }, timeout.Token);
            Assert.True(File.Exists(Path.Combine(root, "repository", "config")));
            Assert.Equal(64, result.Repos.Single().Guid.Length);
        }
        finally { ResticBackrestEngine.DeleteRestoreStaging(root); }
    }
}
