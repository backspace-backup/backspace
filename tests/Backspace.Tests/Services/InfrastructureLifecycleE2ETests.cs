using System;
using System.IO;
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
    public async Task Two_isolated_daemons_coexist_and_same_directory_launch_cannot_kill_owner()
    {
        Assert.True(RealEngineFixture.BinariesPresent, RealEngineFixture.MissingBinariesReason());
        using var workspace = new TempWorkspace();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var first = new BackrestSidecar(AppPaths.BackrestBinary, AppPaths.ResticBinary, workspace.Sub("first"));
        await using var second = new BackrestSidecar(AppPaths.BackrestBinary, AppPaths.ResticBinary, workspace.Sub("second"));
        await using var collision = new BackrestSidecar(AppPaths.BackrestBinary, AppPaths.ResticBinary, first.DataDir);
        await first.StartAsync(timeout.Token);
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
