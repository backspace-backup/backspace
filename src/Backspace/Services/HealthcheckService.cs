using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Serilog;

namespace Backspace.Services;

/// <summary>
/// Healthchecks.io integration. Two audiences:
///
/// 1) Management (API key): create/update checks on the user's behalf so they don't
///    have to touch the web UI — they paste their key, we handle naming/tagging and
///    setting the expected schedule from the Task Scheduler trigger.
///
/// 2) Pings (ping-key in the check's URL): called at the end of a run with /fail
///    or success. Ping doesn't need the management API key.
/// </summary>
public sealed class HealthcheckService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static ILogger _log => AppLogger.For<HealthcheckService>();
    private readonly ConfigStore _config;
    private readonly SecretsStore _secrets;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public HealthcheckService(ConfigStore config, SecretsStore secrets)
    {
        _config = config;
        _secrets = secrets;
    }

    private string BaseUrl => _config.Current.Monitoring.HealthchecksBaseUrl.TrimEnd('/');

    private bool TryGetApiKey(out string key)
    {
        var @ref = _config.Current.Monitoring.HealthchecksApiKeyRef;
        if (!string.IsNullOrEmpty(@ref) && _secrets.TryGet(@ref, out key!) && !string.IsNullOrEmpty(key))
            return true;
        key = "";
        return false;
    }

    // ---- management ----

    public async Task<HealthcheckRecord?> UpsertCheckAsync(Backup backup, CancellationToken ct)
    {
        if (!TryGetApiKey(out var apiKey))
        {
            _log.Information("Skipping Healthchecks.io upsert for {Backup} — no API key configured", backup.Name);
            return null;
        }

        // Translate the backup schedule to a (schedule, tz) Healthchecks understands.
        // Daily/Weekly/Hourly → pick either "timeout" (simple) or "cron".
        // We use cron for anything more specific than a fixed cadence.
        // HC grace = max(user's Skip-grace expressed as seconds, 4h
        // floor). The 4h floor is the original "slow-but-OK backup
        // shouldn't page us" default; the SkipNotificationGraceDays
        // alignment adds defense-in-depth so a HC alarm and an
        // in-app escalation fire on the same time-scale. With the
        // user's default 7 days of grace, HC stays green for ~7
        // days of skipped runs — matching what our orchestrator
        // would do. The user explicitly asked for this: "Make sure
        // the Healthcheck.io checks you create match the grace period
        // desired by the user (it is a secondary protection layer)."
        var skipGraceSeconds = backup.SkipNotificationGraceDays > 0
            ? (long)TimeSpan.FromDays(backup.SkipNotificationGraceDays).TotalSeconds
            : 0;
        // Healthchecks.io API ceiling: grace must be <= 2,592,000s
        // (30 days). The wizard's NumericUpDown allows up to 365
        // days; without this clamp, an upsert with skipGraceSeconds
        // > 30 days returns HTTP 400 and the upsert path falls into
        // the "log warning, return null" branch — the user's HC
        // secondary protection layer silently doesn't exist.
        const long HCApiGraceCeilingSeconds = 30L * 24 * 60 * 60;
        var clamped = Math.Min(HCApiGraceCeilingSeconds, Math.Max((long)TimeSpan.FromHours(4).TotalSeconds, skipGraceSeconds));
        var graceSeconds = (int)clamped;
        // Warn the operator (and surface in the support log) when the
        // clamp engages — the user's in-app grace is then LONGER than
        // the HC layer, so HC may alarm before our own escalation. Not
        // a correctness bug, but the user thinks "secondary protection
        // covers the full window" and we want a breadcrumb if their
        // HC alarm fires unexpectedly.
        if (skipGraceSeconds > HCApiGraceCeilingSeconds)
        {
            _log.Warning(
                "Healthchecks.io grace clamped for {Backup}: user grace = {UserDays} days, HC API ceiling = 30 days. HC may alarm before the in-app grace expires.",
                backup.Name, backup.SkipNotificationGraceDays);
        }
        var body = new UpsertRequest
        {
            Name = backup.Name,
            Tags = $"backspace backup-{backup.Name}",
            Desc = $"Auto-managed by Backspace. Sources: {string.Join(" ; ", backup.SourcePaths)}",
            Grace = graceSeconds,
            UniqueBy = new[] { "name", "tags" },
        };

        var (isCron, cron) = BuildCron(backup);
        if (isCron)
        {
            body.Schedule = cron;
            body.Tz = TimeZoneInfo.Local.Id;
        }
        else
        {
            body.Timeout = (int)ComputeTimeoutSeconds(backup);
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/v3/checks/");
        req.Headers.Add("X-Api-Key", apiKey);
        req.Content = JsonContent.Create(body, options: Json);

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.Warning("Healthchecks.io upsert for {Backup} returned HTTP {Status}", backup.Name, (int)resp.StatusCode);
                return null;
            }
            var parsed = await resp.Content.ReadFromJsonAsync<HealthcheckRecord>(Json, ct);
            // The ping_url is effectively a write-capability bearer
            // token — anyone with the URL can ping success/failure for
            // that check. Log only the trailing 4 chars of the UUID so
            // support can correlate without recovering the live URL.
            _log.Information("Healthchecks.io check upserted for {Backup} (uuid …{UuidTail})",
                backup.Name, TailOfUuid(parsed?.PingUrl));
            return parsed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Mirror VerifyCheckUuidAsync + PingAsync — propagate
            // cancellation so the caller can distinguish "user closed
            // the editor / aborted" from a real network failure
            // instead of logging a misleading "upsert failed".
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Healthchecks.io upsert failed for {Backup}", backup.Name);
            return null;
        }
    }

    public async Task<bool> DeleteCheckAsync(string checkUuid, CancellationToken ct)
    {
        if (!TryGetApiKey(out var apiKey)) return false;
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{BaseUrl}/api/v3/checks/{checkUuid}");
        req.Headers.Add("X-Api-Key", apiKey);
        using var resp = await _http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// Verifies that <paramref name="checkUuid"/> belongs to a real check
    /// in the user's Healthchecks.io project.
    ///
    /// <paramref name="inlineApiKey"/> / <paramref name="inlineBaseUrl"/>
    /// let callers (the BackupEditor's inline Healthchecks-setup card)
    /// pass credentials WITHOUT first writing them to global config —
    /// so a failed verify doesn't leak a user-pasted API key into
    /// Settings. When inline values are absent, we fall back to whatever
    /// is configured globally.
    /// </summary>
    public async Task<UuidVerificationResult> VerifyCheckUuidAsync(
        string checkUuid,
        string? inlineApiKey = null,
        string? inlineBaseUrl = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(checkUuid))
            return UuidVerificationResult.Empty();
        if (!Guid.TryParse(checkUuid.Trim(), out var parsed))
            return UuidVerificationResult.MalformedShape();

        var apiKey = !string.IsNullOrWhiteSpace(inlineApiKey)
            ? inlineApiKey.Trim()
            : (TryGetApiKey(out var globalKey) ? globalKey : "");
        if (string.IsNullOrEmpty(apiKey))
            return UuidVerificationResult.NoApiKey();

        var baseUrl = (!string.IsNullOrWhiteSpace(inlineBaseUrl) ? inlineBaseUrl.Trim() : BaseUrl).TrimEnd('/');

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v3/checks/{parsed}");
            req.Headers.Add("X-Api-Key", apiKey);
            using var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                _log.Information("Verified Healthchecks.io check (uuid …{UuidTail})", TailOfId(parsed));
                return UuidVerificationResult.Valid();
            }
            if ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403)
            {
                _log.Warning("Healthchecks.io rejected API key while verifying check (uuid …{UuidTail}, HTTP {Status})", TailOfId(parsed), (int)resp.StatusCode);
                return UuidVerificationResult.NotAuthorized();
            }
            if ((int)resp.StatusCode == 404)
            {
                _log.Warning("Healthchecks.io check (uuid …{UuidTail}) not found in this account", TailOfId(parsed));
                return UuidVerificationResult.NotFound();
            }
            _log.Warning("Healthchecks.io returned HTTP {Status} verifying check (uuid …{UuidTail})", (int)resp.StatusCode, TailOfId(parsed));
            return UuidVerificationResult.NetworkError($"Healthchecks.io returned HTTP {(int)resp.StatusCode}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // User cancelled the verify (closed the editor, hit Cancel)
            // — propagate so the caller can distinguish from a real
            // network failure. Reporting it as "Couldn't reach
            // Healthchecks.io: A task was canceled" surfaces a
            // confusing red error in the UI for a user-initiated stop.
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Couldn't reach Healthchecks.io to verify check (uuid …{UuidTail})", TailOfId(parsed));
            return UuidVerificationResult.NetworkError(ex.Message);
        }
    }

    /// <summary>Last 4 chars of a raw UUID (or "?" if too short / null),
    /// for safe log correlation. The ping URL is
    /// https://hc-ping.com/{uuid} so the full UUID is effectively a
    /// write-capability bearer token — never log it whole.</summary>
    private static string TailOfId(string? uuid)
    {
        if (string.IsNullOrEmpty(uuid) || uuid.Length < 4) return "?";
        return uuid.Substring(uuid.Length - 4);
    }

    /// <summary>Convenience overload for the parsed-Guid case in
    /// <see cref="VerifyCheckUuidAsync"/>. <c>Guid.ToString()</c>
    /// emits the canonical 36-char form, so taking the last 4 chars
    /// after the trailing hex group is the same as
    /// <see cref="TailOfId(string?)"/> on the raw UUID string.</summary>
    private static string TailOfId(Guid uuid) => TailOfId(uuid.ToString());

    /// <summary>Returns the last 4 chars of the UUID embedded in a
    /// healthchecks.io ping URL, for safe log correlation. Returns
    /// "?" if the URL is missing or doesn't match the expected
    /// shape — never echoes the full UUID.</summary>
    private static string TailOfUuid(string? pingUrl)
    {
        if (string.IsNullOrEmpty(pingUrl)) return "?";
        var slash = pingUrl.LastIndexOf('/');
        if (slash < 0 || slash + 1 >= pingUrl.Length) return "?";
        var tail = pingUrl.Substring(slash + 1);
        return tail.Length <= 4 ? "?" : tail.Substring(tail.Length - 4);
    }

    public readonly record struct UuidVerificationResult(
        UuidVerificationOutcome Outcome,
        string Message)
    {
        public bool IsValid => Outcome == UuidVerificationOutcome.Valid;
        public bool IsBlockingError =>
            Outcome is UuidVerificationOutcome.MalformedShape
                       or UuidVerificationOutcome.NotFound
                       or UuidVerificationOutcome.NotAuthorized;

        public static UuidVerificationResult Empty()        => new(UuidVerificationOutcome.Empty, "");
        public static UuidVerificationResult Valid()        => new(UuidVerificationOutcome.Valid, "Verified with Healthchecks.io.");
        public static UuidVerificationResult MalformedShape()=> new(UuidVerificationOutcome.MalformedShape, "That doesn't look like a valid UUID.");
        public static UuidVerificationResult NotFound()     => new(UuidVerificationOutcome.NotFound, "Healthchecks.io says no check with this UUID exists in your project.");
        public static UuidVerificationResult NotAuthorized()=> new(UuidVerificationOutcome.NotAuthorized, "Healthchecks.io rejected the API key. Check it in Settings.");
        public static UuidVerificationResult NoApiKey()     => new(UuidVerificationOutcome.NoApiKey, "No API key. Set one in Settings (or fill it in below) so we can verify the UUID.");
        public static UuidVerificationResult NetworkError(string detail) => new(UuidVerificationOutcome.NetworkError, $"Couldn't reach Healthchecks.io: {detail}");
    }

    public enum UuidVerificationOutcome { Empty, Valid, MalformedShape, NotFound, NotAuthorized, NoApiKey, NetworkError }

    // ---- pings ----

    /// <summary>Send a "started" ping. Optional — some users prefer final-ping-only.</summary>
    public Task PingStartAsync(string pingUuid, CancellationToken ct) =>
        PingAsync(pingUuid, "/start", null, ct);

    public Task PingSuccessAsync(string pingUuid, string body, CancellationToken ct) =>
        PingAsync(pingUuid, "", body, ct);

    public Task PingFailureAsync(string pingUuid, string body, CancellationToken ct) =>
        PingAsync(pingUuid, "/fail", body, ct);

    private async Task PingAsync(string pingUuid, string suffix, string? body, CancellationToken ct)
    {
        var url = $"https://hc-ping.com/{pingUuid}{suffix}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrEmpty(body))
                req.Content = new StringContent(body, System.Text.Encoding.UTF8, "text/plain");
            // User-Agent: include the app + version so Healthchecks.io's
            // dashboard can show "Backspace" next to the check, but
            // intentionally OMIT Environment.MachineName — that name
            // is identifying PII the user may not want shipped to a
            // third-party telemetry service. The check UUID + body
            // already let the user correlate from their side; the
            // machine name buys nothing for them and is leaky for us.
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
            req.Headers.UserAgent.ParseAdd($"Backspace/{ver}");
            using var resp = await _http.SendAsync(req, ct);
            _log.Debug("Healthchecks.io ping {Suffix} for check (uuid …{UuidTail}): HTTP {Status}",
                string.IsNullOrEmpty(suffix) ? "/success" : suffix, TailOfId(pingUuid), (int)resp.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller asked us to stop — propagate, don't disguise as
            // a network error.
            throw;
        }
        catch (Exception ex)
        {
            // Network errors shouldn't fail the backup result, but we
            // log so the user can see why their watchdog stayed silent.
            _log.Warning(ex, "Healthchecks.io ping {Suffix} for check (uuid …{UuidTail}) failed",
                string.IsNullOrEmpty(suffix) ? "/success" : suffix, TailOfId(pingUuid));
        }
    }

    // ---- schedule translation ----

    private static (bool isCron, string cron) BuildCron(Backup b)
    {
        var s = b.Schedule;
        return s.Frequency switch
        {
            ScheduleFrequency.Daily  => (true, $"{s.TimeOfDay.Minutes} {s.TimeOfDay.Hours} * * *"),
            ScheduleFrequency.Weekly => (true, BuildWeeklyCron(s)),
            ScheduleFrequency.Hourly => (true, $"0 */{Math.Max(1, s.HourlyInterval)} * * *"),
            _ => (false, ""),
        };
    }

    private static string BuildWeeklyCron(BackupSchedule s)
    {
        // Cron days: 0=Sun..6=Sat. We match our bitmask conventions.
        var days = new System.Collections.Generic.List<int>();
        if ((s.WeeklyDaysBitmask & 1)  != 0) days.Add(0);
        if ((s.WeeklyDaysBitmask & 2)  != 0) days.Add(1);
        if ((s.WeeklyDaysBitmask & 4)  != 0) days.Add(2);
        if ((s.WeeklyDaysBitmask & 8)  != 0) days.Add(3);
        if ((s.WeeklyDaysBitmask & 16) != 0) days.Add(4);
        if ((s.WeeklyDaysBitmask & 32) != 0) days.Add(5);
        if ((s.WeeklyDaysBitmask & 64) != 0) days.Add(6);
        var dayStr = days.Count > 0 ? string.Join(",", days) : "*";
        return $"{s.TimeOfDay.Minutes} {s.TimeOfDay.Hours} * * {dayStr}";
    }

    private static double ComputeTimeoutSeconds(Backup b) =>
        b.Schedule.Frequency switch
        {
            ScheduleFrequency.Hourly => TimeSpan.FromHours(Math.Max(1, b.Schedule.HourlyInterval) * 1.5).TotalSeconds,
            ScheduleFrequency.Daily  => TimeSpan.FromHours(30).TotalSeconds,
            ScheduleFrequency.Weekly => TimeSpan.FromDays(8).TotalSeconds,
            _ => TimeSpan.FromDays(2).TotalSeconds,
        };

    // ---- shapes ----

    private sealed class UpsertRequest
    {
        public string? Name { get; set; }
        public string? Tags { get; set; }
        public string? Desc { get; set; }
        public int? Timeout { get; set; }
        public int? Grace { get; set; }
        public string? Schedule { get; set; }
        public string? Tz { get; set; }

        [JsonPropertyName("unique")]
        public string[]? UniqueBy { get; set; }
    }

    public sealed class HealthcheckRecord
    {
        [JsonPropertyName("ping_url")]
        public string? PingUrl { get; set; }

        [JsonPropertyName("slug")]
        public string? Slug { get; set; }

        /// <summary>Convenience extractor: pull the UUID out of the ping_url.</summary>
        public string? ExtractPingUuid()
        {
            if (string.IsNullOrEmpty(PingUrl)) return null;
            var tail = PingUrl.TrimEnd('/').Split('/');
            return tail.Length > 0 ? tail[^1] : null;
        }
    }
}
