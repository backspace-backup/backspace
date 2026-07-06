using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Backspace.Services;

/// <summary>
/// Lightest-possible update channel: on startup, query GitHub
/// Releases for the latest tag; if it's newer than the running
/// version, surface a non-blocking notification with a link to the
/// release page. No auto-download, no relaunch — manual update keeps
/// the trust boundary clean (user sees what version they're running
/// + decides when to switch). Squirrel / Sparkle / etc. can land
/// later if user volume justifies the operational cost.
///
/// Failure-isolated: the whole check runs in a fire-and-forget task,
/// catches everything, and never blocks app startup. No network on
/// the host? No firewall punch? Both fine — silent skip, retry next
/// launch.
/// </summary>
public sealed class UpdateChecker
{
    private const string ReleasesApi = "https://api.github.com/repos/backspace-backup/backspace/releases/latest";
    private const string UserAgent = "Backspace-UpdateCheck";
    private static readonly TimeSpan _httpTimeout = TimeSpan.FromSeconds(8);

    private static ILogger _log => AppLogger.For<UpdateChecker>();

    /// <summary>
    /// Kick off a background check. Returns immediately; the check
    /// fires on the thread pool and surfaces a toast through
    /// <see cref="ServiceLocator.Notifier"/> when (and only when) a
    /// newer version is published.
    /// </summary>
    public void Begin()
    {
        // Test escape hatch: the suite builds many MainWindows, and a
        // live GitHub GET (plus a possible update toast) per window
        // injects network-timed work at unpredictable points mid-run.
        if (Environment.GetEnvironmentVariable("BACKSPACE_NO_UPDATE_CHECK") == "1") return;

        _ = Task.Run(async () =>
        {
            try { await CheckOnceAsync(CancellationToken.None); }
            catch (Exception ex)
            {
                _log.Debug(ex, "Update check threw — silent skip");
            }
        });
    }

    internal async Task CheckOnceAsync(CancellationToken ct)
    {
        var current = CurrentVersion();
        if (current is null)
        {
            _log.Debug("Skipping update check: couldn't read current assembly version");
            return;
        }

        var latest = await FetchLatestAsync(ct);
        if (latest is null)
        {
            _log.Debug("Update check: GitHub returned no usable release");
            return;
        }

        if (latest.Parsed <= current)
        {
            _log.Debug("Update check: running {Current} ≥ latest {Latest} — up to date",
                current, latest.Parsed);
            return;
        }

        _log.Information("Newer release available: running {Current} → latest {Latest} ({Url})",
            current, latest.Parsed, latest.HtmlUrl);

        try
        {
            await ServiceLocator.Notifier.NotifyAsync(
                title: $"Backspace {latest.Tag} is out",
                detail: $"You're on {current}. Open the release notes to see what changed " +
                        $"and grab the new zip.\n{latest.HtmlUrl}",
                severity: NotificationSeverity.Success);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Update toast threw — non-fatal");
        }
    }

    private static Version? CurrentVersion()
    {
        // AssemblyInformationalVersion is the SemVer-shaped one
        // (set via <Version> in the csproj). Strip any +build
        // suffix; if it parses, use it. Fall back to AssemblyVersion
        // for the unusual case where the informational one is unset.
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var clean = info;
            var plus = clean.IndexOf('+');
            if (plus >= 0) clean = clean[..plus];
            if (Version.TryParse(clean, out var v)) return v;
        }
        return asm.GetName().Version;
    }

    private static async Task<ReleaseInfo?> FetchLatestAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = _httpTimeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var resp = await http.GetAsync(ReleasesApi, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var release = System.Text.Json.JsonSerializer.Deserialize<GitHubRelease>(body);
            if (release is null) return null;
            if (release.Draft || release.Prerelease) return null;

            var tag = release.TagName ?? "";
            if (tag.StartsWith("v", StringComparison.OrdinalIgnoreCase)) tag = tag[1..];
            if (!Version.TryParse(tag, out var parsed)) return null;

            return new ReleaseInfo(parsed, release.TagName ?? tag, release.HtmlUrl ?? "https://github.com/backspace-backup/backspace/releases/latest");
        }
        catch
        {
            return null;
        }
    }

    internal sealed record ReleaseInfo(Version Parsed, string Tag, string HtmlUrl);

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    }
}
