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
