using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>Only starts our own isolated local Backrest daemons. No cloud,
/// account, UAC, scheduled-task registration or ambient-process sweep.</summary>
public sealed class InfrastructureLifecycleE2ETests
{
    [Fact]
    public async Task First_start_and_missing_password_recovery_never_accept_unauthenticated_requests()
    {
        Assert.True(RealEngineFixture.BinariesPresent, RealEngineFixture.MissingBinariesReason());
        using var workspace = new TempWorkspace();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var sidecar = new BackrestSidecar(AppPaths.BackrestBinary, AppPaths.ResticBinary, workspace.Sub("first-start"));
        for (var launch = 0; launch < 2; launch++)
        {
            var starting = sidecar.StartAsync(timeout.Token);
            using var http = new HttpClient { BaseAddress = new Uri(sidecar.WebUiUrl) };
            var observed = 0;
            do
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/v1.Backrest/GetConfig")
                { Content = new StringContent("{}") };
                request.Headers.Add("Origin", "https://untrusted.example");
                try
                {
                    using var response = await http.SendAsync(request, timeout.Token);
                    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                    observed++;
                }
                catch (HttpRequestException) when (!starting.IsCompleted) { }
                if (!starting.IsCompleted) await Task.Delay(10, timeout.Token);
            } while (!starting.IsCompleted || observed == 0);
            await starting;
            await sidecar.Client!.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>(
                "/v1.Backrest/GetConfig", new(), timeout.Token);
            using var wrong = await http.PostAsync("/v1.Authentication/Login",
                new StringContent("{\"username\":\"backspace\",\"password\":\"wrong\"}", System.Text.Encoding.UTF8, "application/json"), timeout.Token);
            Assert.False(wrong.IsSuccessStatusCode);
            await sidecar.StopAsync(TimeSpan.Zero, timeout.Token);
            File.Delete(Path.Combine(sidecar.DataDir, "sidecar-auth.txt"));
        }
    }

    [Fact]
    public async Task Real_daemon_retains_snapshot_cloud_exclusion_and_bandwidth_flags()
    {
        Assert.True(RealEngineFixture.BinariesPresent, RealEngineFixture.MissingBinariesReason());
        using var workspace = new TempWorkspace();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var sidecar = new BackrestSidecar(AppPaths.BackrestBinary, AppPaths.ResticBinary, workspace.Sub("flags"));
        await sidecar.StartAsync(timeout.Token);
        var cfg = await sidecar.Client!.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>(
            "/v1.Backrest/GetConfig", new(), timeout.Token);
        cfg.Repos.Add(new BackrestRepo { Id = "flags-repo", Uri = workspace.Sub("repository"), Password = "disposable-password", AutoInitialize = true });
        var plan = ResticBackrestEngine.BuildPlanFromBackup("flags-plan", "flags-repo",
            new Backspace.Models.Backup { UseVss = true, ExcludeCloudFiles = true, BandwidthLimitKBps = 123 }, workspace.Sub("source"));
        cfg.Plans.Add(plan);
        await sidecar.Client.CallAsync<BackrestConfig, BackrestConfig>("/v1.Backrest/SetConfig", cfg, timeout.Token);
        var saved = await sidecar.Client.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>(
            "/v1.Backrest/GetConfig", new(), timeout.Token);
        Assert.Equal(new[] { "--use-fs-snapshot", "--exclude-cloud-files", "--limit-upload", "123" },
            Assert.Single(saved.Plans).BackupFlags);
        Assert.DoesNotContain("backupFlags", File.ReadAllText(System.IO.Path.Combine(sidecar.DataDir, "config.json")));
    }

    [Fact]
    public async Task Wrong_saved_password_stops_the_real_daemon_and_preserves_configuration()
    {
        Assert.True(RealEngineFixture.BinariesPresent, RealEngineFixture.MissingBinariesReason());
        using var workspace = new TempWorkspace();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var sidecar = new BackrestSidecar(AppPaths.BackrestBinary, AppPaths.ResticBinary, workspace.Sub("auth"));
        await sidecar.StartAsync(timeout.Token);
        await sidecar.StopAsync(TimeSpan.Zero, timeout.Token);
        var authPath = Path.Combine(sidecar.DataDir, "sidecar-auth.txt");
        var savedPassword = File.ReadAllText(authPath);
        var configPath = Path.Combine(sidecar.DataDir, "config.json");
        var originalConfig = File.ReadAllText(configPath);
        File.WriteAllText(authPath, "deliberately-wrong-test-password");
        await Assert.ThrowsAsync<IOException>(() => sidecar.StartAsync(timeout.Token));
        Assert.False(sidecar.IsRunning);
        Assert.Null(sidecar.Client);
        Assert.False(File.Exists(sidecar.PidFilePath));
        Assert.Equal(originalConfig, File.ReadAllText(configPath));
        // A failed login must release ownership so a corrected retry can start.
        File.WriteAllText(authPath, savedPassword);
        await sidecar.StartAsync(timeout.Token);
        Assert.True(sidecar.IsRunning);
    }

    [Fact]
    public async Task Two_isolated_daemons_coexist_and_same_directory_launch_cannot_kill_owner()
    {
        Assert.True(RealEngineFixture.BinariesPresent, RealEngineFixture.MissingBinariesReason());
        using var workspace = new TempWorkspace();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var first = new BackrestSidecar(AppPaths.BackrestBinary, AppPaths.ResticBinary, workspace.Sub("first"));
        await using var second = new BackrestSidecar(AppPaths.BackrestBinary, AppPaths.ResticBinary, workspace.Sub("second"));
        await using var collision = new BackrestSidecar(AppPaths.BackrestBinary, AppPaths.ResticBinary, first.DataDir);
        await first.StartAsync(timeout.Token);
        using (var unauthenticated = new HttpClient { BaseAddress = new Uri(first.WebUiUrl) })
        using (var response = await unauthenticated.PostAsync("/v1.Backrest/GetConfig", new StringContent("{}"), timeout.Token))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var ownerRecord = File.ReadAllText(first.PidFilePath);
        await second.StartAsync(timeout.Token);
        Assert.True(first.IsRunning, "Starting a second daemon must not kill the first.");
        Assert.True(second.IsRunning);
        Assert.NotEqual(first.Port, second.Port);
        await Assert.ThrowsAsync<IOException>(() => collision.StartAsync(timeout.Token));
        Assert.Equal(ownerRecord, File.ReadAllText(first.PidFilePath));
        Assert.True(first.IsRunning);
        await first.Client!.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>(
            "/v1.Backrest/GetConfig", new(), timeout.Token);
        await second.StopAsync(TimeSpan.Zero, timeout.Token);
        Assert.True(first.IsRunning, "Stopping the second owner must leave the first daemon alive.");
        await first.Client.CallAsync<BackrestSidecar.EmptyRequest, BackrestConfig>(
            "/v1.Backrest/GetConfig", new(), timeout.Token);
    }
}
