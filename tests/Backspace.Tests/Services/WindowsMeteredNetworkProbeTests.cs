using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// The metered probe calls Windows' network cost API directly instead of
/// through the Windows SDK projection (U032). These run the real API on
/// this machine; the line-by-line comparison against the projection is
/// recorded in the Review desk.
/// </summary>
public sealed class WindowsMeteredNetworkProbeTests
{
    /// <summary>GitHub's Windows runner reports an unknown cost; scheduled backups
    /// there were skipped as "metered" (31 October 2026).</summary>
    [Theory]
    [InlineData(0 /* Unknown */, false, false, false, false)]
    [InlineData(1 /* Unrestricted */, false, false, false, false)]
    [InlineData(2 /* Fixed */, false, false, false, true)]
    [InlineData(3 /* Variable */, false, false, false, true)]
    [InlineData(0 /* Unknown */, true, false, false, true)]
    [InlineData(1 /* Unrestricted */, false, true, false, true)]
    [InlineData(1 /* Unrestricted */, false, false, true, true)]
    public void Only_a_known_cost_or_roaming_counts_as_metered(int type, bool roaming, bool overLimit, bool backgroundRestricted, bool metered)
        => Assert.Equal(metered, WindowsMeteredNetworkProbe.IsMetered(new((NetworkCostInterop.NetworkCostType)type, roaming, overLimit, backgroundRestricted), includeBackgroundRestricted: true));

    [Fact]
    public void Sample_reads_every_connected_profile_from_Windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var network = new NetworkCostInterop.NetworkInformation();
        var (metered, detail) = WindowsMeteredNetworkProbe.SampleMetered(network, includeBackgroundRestricted: true);
        if (detail == "no active profile") { Assert.False(metered); return; }
        Assert.StartsWith("profiles: [", detail);
        // Each profile reports a real cost type, never a slot read as garbage.
        // An unknown cost (GitHub's runner) is not metered.
        Assert.Matches(@"^profiles: (\[[^\]]+ cost=(Unknown|Unrestricted|Fixed|Variable) roam=(True|False) over=(True|False) bg=(True|False)\] ?)+$", detail);
        Assert.Equal(detail.Contains("cost=Fixed") || detail.Contains("cost=Variable")
            || detail.Contains("roam=True") || detail.Contains("over=True") || detail.Contains("bg=True"), metered);
    }

    [Fact]
    public void Ignoring_background_restriction_never_makes_a_network_more_metered()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var network = new NetworkCostInterop.NetworkInformation();
        var strict = WindowsMeteredNetworkProbe.SampleMetered(network, includeBackgroundRestricted: true);
        var relaxed = WindowsMeteredNetworkProbe.SampleMetered(network, includeBackgroundRestricted: false);
        Assert.True(strict.metered || !relaxed.metered, $"{strict.detail} / {relaxed.detail}");
    }

    [Fact]
    public async Task Watching_subscribes_to_network_changes_and_releases_the_subscription_when_stopped()
    {
        if (!OperatingSystem.IsWindows()) return;
        // The suite disables the platform probe globally; this test runs the real one.
        var watchdog = new MeteredNetworkWatchdog(WindowsMeteredNetworkProbe.RunAsync) { PollWhenUnmeteredSeconds = 1, PollWhenMeteredSeconds = 1 };
        var lines = new List<string>();
        watchdog.Status += line => { lock (lines) lines.Add(line); };
        using var stop = new CancellationTokenSource();
        var run = Task.Run(() => watchdog.RunAsync(stop.Token), TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            lock (lines) if (lines.Exists(l => l.StartsWith("[Poll]"))) break;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Assert.True(NetworkCostInterop.StatusChangedHandler.Live >= 1, "The watchdog must listen for network changes while it runs.");
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        lock (lines)
        {
            Assert.Contains(lines, l => l.StartsWith("[Startup] "));
            Assert.Contains(lines, l => l.StartsWith("[Poll] "));
            Assert.DoesNotContain(lines, l => l.Contains("probe failed"));
        }
        // Windows released the handler once the subscription ended.
        Assert.Equal(0, NetworkCostInterop.StatusChangedHandler.Live);
    }
}
