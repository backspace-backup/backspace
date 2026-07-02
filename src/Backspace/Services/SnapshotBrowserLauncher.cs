using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Serilog;

namespace Backspace.Services;

/// <summary>
/// Opens Backrest's bundled snapshot browser for a given backup,
/// scoped to a single plan when possible.
///
/// Backrest ships a fully-featured SPA — snapshot listing, file
/// tree, restore/forget actions, log viewer — on the same HTTP
/// port the Connect-Go RPC binds to. Rather than reimplementing
/// snapshot navigation natively, the launcher just points the
/// user's default browser at the sidecar URL. Same UI Backrest's
/// standalone deployment ships; instant feature parity; no
/// platform-specific WebView2 / wkwebview runtime needed.
///
/// The browser opens at <c>http://127.0.0.1:&lt;port&gt;/#/plan/&lt;planId&gt;</c>
/// when a plan id can be derived (the backup has at least one
/// concrete (sourcePath, destination, storageName) target),
/// otherwise at the plain root.
///
/// If the user prefers an embedded view, a future iteration can
/// drop an Avalonia.WebView into a window pointed at the same
/// URL — the launcher's API stays the same.
/// </summary>
public sealed class SnapshotBrowserLauncher
{
    private static ILogger _log => AppLogger.For<SnapshotBrowserLauncher>();
    private readonly BackrestSidecar _sidecar;

    public SnapshotBrowserLauncher(BackrestSidecar sidecar) => _sidecar = sidecar;

    /// <summary>
    /// Open the user's default browser at Backrest's snapshot view
    /// for <paramref name="backup"/>. We pick the first concrete
    /// (sourcePath × destination × storageName) triple as the plan
    /// scope — that matches the same first-cell convention
    /// RestoreEngine + RevisionBrowser use.
    /// Async so a cold-start of the Backrest engine doesn't freeze
    /// the UI thread for 15 seconds.
    /// </summary>
    public async Task<LaunchResult> OpenForAsync(Backup backup)
    {
        if (backup is null)
            return LaunchResult.Failed("No backup selected.");

        // If the engine isn't up yet (rare — the app eager-starts it
        // shortly after first paint), wait briefly for it rather than
        // refusing with a "not ready" toast the user has to retry
        // through. 15 s is generous: cold start is ~1–2 s, and
        // anything past that is a real failure (binary missing, port
        // conflict, AV interference) the message below names.
        if (!_sidecar.IsRunning || string.IsNullOrEmpty(_sidecar.WebUiUrl))
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await _sidecar.EnsureStartedAsync(cts.Token).ConfigureAwait(false);
            }
            catch (EngineBinaryMissingException ex)
            {
                return LaunchResult.Failed(ex.Message);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Couldn't start the Backrest engine on demand");
                return LaunchResult.Failed(
                    "The Backrest engine couldn't start. Check the app log for details: " + ex.Message);
            }
            if (!_sidecar.IsRunning || string.IsNullOrEmpty(_sidecar.WebUiUrl))
                return LaunchResult.Failed(
                    "The Backrest engine still isn't responding after 15 s — try restarting the app.");
        }

        var url = BuildUrlFor(backup);
        try
        {
            using (var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })) { }
            _log.Information("Snapshot browser opened at {Url} for backup {Name}", url, backup.Name);
            return LaunchResult.Ok(url);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Couldn't open snapshot browser at {Url}", url);
            return LaunchResult.Failed(
                $"Couldn't open your browser at {url}. " +
                $"You can paste it into a browser manually. Reason: {ex.Message}");
        }
    }

    private string BuildUrlFor(Backup backup)
    {
        var baseUrl = _sidecar.WebUiUrl.TrimEnd('/');

        // First (source × destination × storage) triple becomes the
        // plan scope. Backrest's SPA accepts /#/plan/<id> in its
        // hash route, so a deep-link drops the user straight into
        // the snapshot list for that plan. Falling back to root
        // gives the user the dashboard (same as opening Backrest's
        // standalone UI cold).
        if (backup.SourcePaths.Count > 0 && backup.Targets.Count > 0)
        {
            var sourcePath = backup.SourcePaths[0];
            var target = backup.Targets[0];
            var destination = ServiceLocator.Config.Current.Destinations
                .Find(d => d.Id == target.DestinationId);
            if (destination is not null)
            {
                var planId = ResticBackrestEngine.MakePlanId(
                    backup, sourcePath, destination, target.StorageName);
                return $"{baseUrl}/#/plan/{Uri.EscapeDataString(planId)}";
            }
        }
        return baseUrl + "/";
    }
}

/// <summary>Outcome of a <see cref="SnapshotBrowserLauncher.OpenFor"/>
/// call. The URL is non-null on success so the UI can show "opened
/// at <i>http://127.0.0.1:54321/...</i>" as a confirmation toast.</summary>
public readonly record struct LaunchResult(bool Succeeded, string? Url, string Message)
{
    public static LaunchResult Ok(string url)
        => new(true, url, $"Snapshot browser opened at {url}");

    public static LaunchResult Failed(string reason)
        => new(false, null, reason);
}
