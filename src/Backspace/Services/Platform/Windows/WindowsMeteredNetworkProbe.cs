using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using static Backspace.Services.Platform.Windows.NetworkCostInterop;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// Windows-only implementation of the metered-network probe, on Windows'
/// <c>Windows.Networking.Connectivity</c> cost API (called through
/// <see cref="NetworkCostInterop"/>). Lives under Services/Platform/Windows/
/// so it never reaches the cross-platform TFM.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsMeteredNetworkProbe
{
    /// <summary>
    /// Adaptive-cadence poll on top of the Windows cost API, plus an
    /// immediate check whenever Windows reports a network change.
    /// "Raise event" goes through the platform-neutral
    /// <see cref="MeteredNetworkWatchdog"/> shell so callers see one
    /// consistent surface.
    /// </summary>
    public static async Task RunAsync(MeteredNetworkWatchdog host, CancellationToken ct)
    {
        bool? lastMetered = null;
        int interval = host.PollWhenUnmeteredSeconds;
        var evaluateLock = new object();
        var disposed = false;

        using var network = new NetworkInformation();
        // Unsubscribing uses the token Windows returned for this exact
        // subscription, so the handler can't leak.
        IDisposable? subscription = null;

        try
        {
            subscription = network.SubscribeNetworkStatusChanged(NetworkInformation_StatusChanged);
            EvaluateLocked("Startup");

            while (!ct.IsCancellationRequested)
            {
                int sleepSeconds;
                lock (evaluateLock) sleepSeconds = interval;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(sleepSeconds), ct);
                }
                catch (TaskCanceledException) { break; }

                EvaluateLocked("Poll");
            }
        }
        finally
        {
            // Set BEFORE unsubscribe so a handler-in-flight observes
            // the flag even if it landed between this line and the
            // unsubscribe actually completing.
            lock (evaluateLock) disposed = true;
            subscription?.Dispose();
        }

        void NetworkInformation_StatusChanged()
        {
            // Bail if RunAsync has already finished — defends against
            // a late-fire event landing on a captured Status delegate
            // whose consumer has moved on.
            lock (evaluateLock) { if (disposed) return; }
            EvaluateLocked("Event");
        }

        void EvaluateLocked(string reason)
        {
            lock (evaluateLock)
            {
                if (disposed) return;
                Evaluate(host, network, ref lastMetered, ref interval, reason);
            }
        }
    }

    private static void Evaluate(MeteredNetworkWatchdog host, NetworkInformation network, ref bool? lastMetered, ref int interval, string reason)
    {
        bool anyMetered;
        string detail;
        try
        {
            (anyMetered, detail) = SampleMetered(network, host.IncludeBackgroundRestricted);
        }
        catch (Exception ex)
        {
            host.RaiseStatus($"[{reason}] probe failed: {ex.Message}");
            return;
        }

        host.RaiseStatus($"[{reason}] {detail}");

        if (lastMetered != anyMetered)
        {
            lastMetered = anyMetered;
            interval = anyMetered ? host.PollWhenMeteredSeconds : host.PollWhenUnmeteredSeconds;
            if (anyMetered)
            {
                host.Logger.Warning("Metered network detected — backup will be aborted: {Detail}", detail);
                host.RaiseMeteredDetected(detail);
            }
            else
            {
                host.Logger.Information("Network is unmetered: {Detail}", detail);
            }
        }
    }

    internal static (bool metered, string detail) SampleMetered(NetworkInformation network, bool includeBackgroundRestricted)
    {
        bool any = false;
        var details = new System.Text.StringBuilder("profiles: ");

        var internetProfile = network.GetInternetConnectionProfile();
        if (internetProfile is null)
            return (false, "no active profile");

        // Windows objects are released as soon as this sample is taken.
        var all = new System.Collections.Generic.List<ConnectionProfile?>();
        try
        {
            all = network.GetConnectionProfiles();
            var seen = new System.Collections.Generic.HashSet<Guid>();
            var profiles = new System.Collections.Generic.List<ConnectionProfile> { internetProfile };
            foreach (var p in all)
            {
                if (p is null) continue;
                Guid id = Guid.Empty;
                try { id = p.NetworkAdapterId; } catch { }
                if (id != Guid.Empty && !seen.Add(id)) continue;
                if (!p.IsSameObject(internetProfile)) profiles.Add(p);
            }

            foreach (var p in profiles)
            {
                var level = p.GetNetworkConnectivityLevel();
                if (level == NetworkConnectivityLevel.None) continue;

                var cost = p.GetConnectionCost();
                var meteredHere = IsMetered(cost, includeBackgroundRestricted);

                if (meteredHere) any = true;

                details.Append($"[{Safe(p.ProfileName)} cost={cost.NetworkCostType} ")
                       .Append($"roam={cost.Roaming} over={cost.OverDataLimit} bg={cost.BackgroundDataUsageRestricted}] ");
            }

            return (any, details.ToString().TrimEnd());
        }
        finally
        {
            internetProfile.Dispose();
            foreach (var p in all) p?.Dispose();
        }
    }

    /// <summary>Fixed or variable cost, roaming or over the data limit. "Unknown"
    /// means Windows has no cost information (servers, virtual machines such as
    /// GitHub's runners); like Windows Update, that is not treated as metered.</summary>
    internal static bool IsMetered(ConnectionCost cost, bool includeBackgroundRestricted)
        => cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable
           || cost.Roaming
           || cost.OverDataLimit
           || (includeBackgroundRestricted && cost.BackgroundDataUsageRestricted);

    private static string Safe(string? s) => string.IsNullOrEmpty(s) ? "(none)" : s;
}
