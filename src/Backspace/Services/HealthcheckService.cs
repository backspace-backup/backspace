using System;
using System.Linq;
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
    private readonly HttpClient _http;

    public HealthcheckService(ConfigStore config, SecretsStore secrets, HttpClient? http = null)
    {
        _config = config;
        _secrets = secrets;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    private string BaseUrl => _config.Current.Monitoring.HealthchecksBaseUrl.TrimEnd('/');

    private bool TryGetApiKey(out string key)
    {
        var @ref = _config.Current.Monitoring.HealthchecksApiKeyRef;
        if (!string.IsNullOrEmpty(@ref) && _secrets.TryGet(@ref, out key!) && !string.IsNullOrEmpty(key))
        {
            key = CleanApiKey(key);
            if (key.Length > 0) return true;
        }
        key = "";
        return false;
    }

    /// <summary>The key as Healthchecks.io gave it: a key pasted with a
    /// space, a line break or an invisible character (a zero-width space, a
    /// byte-order mark) around or inside it could not be sent at all
    /// ("Request headers must contain only ASCII characters", U187), so
    /// no check was ever created. A key never contains those characters.</summary>
    internal static string CleanApiKey(string key)
        => new(key.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)
            && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray());

    /// <summary>Why the last attempt to create the checks failed, in plain
    /// words, or null (U187). Settings shows it.</summary>
    public string? LastProblem { get; private set; }

    // ---- management ----

    /// <param name="forChecks">The check for the backup's checks (U211),
    /// not the one for the backup itself.</param>
    public async Task<HealthcheckRecord?> UpsertCheckAsync(Backup backup, CancellationToken ct, bool forChecks = false)
    {
        if (!TryGetApiKey(out var apiKey))
        {
            _log.Information("Skipping Healthchecks.io upsert for {Backup} — no API key configured", backup.Name);
            return null;
        }

        // The expected period is the backup's quiet skipped-run alerts
        // period, with a grace of 3, 7 or 14 days (U211, owner, 12 November
        // 2026): Healthchecks.io then says so when the backup (or its checks)
        // has not succeeded for that long, whatever the reason.
        var body = Desired(backup, forChecks);
        body.UniqueBy = new[] { "name", "tags" };
        body.Channels = "*";

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/v3/checks/");
        req.Headers.Add("X-Api-Key", apiKey);
        req.Content = JsonContent.Create(body, options: Json);

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.Warning("Healthchecks.io upsert for {Backup} returned HTTP {Status}", backup.Name, (int)resp.StatusCode);
                LastProblem = (int)resp.StatusCode is 401 or 403
                    ? "Healthchecks.io refused the API key. Use the project's regular API key, not the read-only one."
                    : $"Healthchecks.io answered with an error (HTTP {(int)resp.StatusCode}).";
                return null;
            }
            var parsed = await resp.Content.ReadFromJsonAsync<HealthcheckRecord>(Json, ct);
            if (parsed is not null && forChecks) backup.HealthcheckChecksPingUrl = parsed.PingUrl;
            else if (parsed is not null) { backup.HealthcheckPingUrl = parsed.PingUrl; backup.HealthcheckChannelsAssigned = true; }
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
            LastProblem = ex is HttpRequestException && ex.Message.Contains("ASCII", StringComparison.Ordinal)
                ? "The API key contains characters a Healthchecks.io key never has. Copy it again from healthchecks.io and paste it here."
                : "Healthchecks.io could not be reached: " + ex.Message;
            return null;
        }
    }

    /// <summary>With Healthchecks on and an API key saved, every backup
    /// without a check gets one, so its runs ping it. Before, a check was
    /// made only by the backup editor's Create button, and turning
    /// Healthchecks on in Settings sent nothing (U187). The upsert is
    /// matched by name and tags, so running it again reuses the same check.
    /// Returns how many backups got a check.</summary>
    /// <summary>Asks Healthchecks.io whether the saved key works (U208): null
    /// when it does, otherwise why not, in plain words.</summary>
    public async Task<string?> VerifyKeyAsync(CancellationToken ct)
    {
        if (!TryGetApiKey(out var apiKey)) return "No API key is saved.";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/v3/checks/");
            req.Headers.Add("X-Api-Key", apiKey);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return null;
            return (int)resp.StatusCode is 401 or 403
                ? "Healthchecks.io refused the API key. Use the project's regular API key, not the read-only one."
                : $"Healthchecks.io answered with an error (HTTP {(int)resp.StatusCode}).";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) when (ex.Message.Contains("ASCII", StringComparison.Ordinal))
        {
            return "The API key contains characters a Healthchecks.io key never has. Copy it again from healthchecks.io and paste it here.";
        }
        catch (Exception ex) { return "Healthchecks.io could not be reached: " + ex.Message; }
    }

    public async Task<int> EnsureChecksAsync(CancellationToken ct)
    {
        var cfg = _config.Current;
        if (!cfg.Monitoring.HealthchecksEnabled || !TryGetApiKey(out _)) return 0;
        var made = 0;
        LastProblem = null;
        foreach (var backup in cfg.Backups.Where(b => string.IsNullOrWhiteSpace(b.HealthcheckPingUrl) && string.IsNullOrWhiteSpace(b.HealthcheckId)).ToList())
        {
            if (ct.IsCancellationRequested) break;
            // The upsert writes the ping address on the backup it gets: a copy, not the live settings.
            var copy = JsonSerializer.Deserialize<Backup>(JsonSerializer.Serialize(backup))!;
            if (await UpsertCheckAsync(copy, ct).ConfigureAwait(false) is not { PingUrl: { Length: > 0 } pingUrl } record) continue;
            _config.Update(c =>
            {
                if (c.Backups.FirstOrDefault(b => b.Id == backup.Id) is not { } live
                    || !string.IsNullOrWhiteSpace(live.HealthcheckPingUrl) || !string.IsNullOrWhiteSpace(live.HealthcheckId)) return;
                live.HealthcheckPingUrl = pingUrl;
                live.HealthcheckId = record.ExtractPingUuid();
                live.HealthcheckChannelsAssigned = true;
            });
            made++;
        }
        // A second check for the backup's checks (U211): down when no check
        // has passed for its period and grace.
        foreach (var backup in _config.Current.Backups.Where(b => b.Checks.Count > 0 && string.IsNullOrWhiteSpace(b.HealthcheckChecksPingUrl)
                     && !string.IsNullOrWhiteSpace(b.HealthcheckPingUrl)).ToList())
        {
            if (ct.IsCancellationRequested) break;
            var copy = JsonSerializer.Deserialize<Backup>(JsonSerializer.Serialize(backup))!;
            if (await UpsertCheckAsync(copy, ct, forChecks: true).ConfigureAwait(false) is not { PingUrl: { Length: > 0 } checksPing }) continue;
            _config.Update(c =>
            {
                if (c.Backups.FirstOrDefault(b => b.Id == backup.Id) is { } live && string.IsNullOrWhiteSpace(live.HealthcheckChecksPingUrl))
                    live.HealthcheckChecksPingUrl = checksPing;
            });
        }
        if (made > 0) _log.Information("Created a Healthchecks.io check for {Count} backup(s)", made);
        // Checks Backspace made before U210 had no integration, so nothing
        // was sent when a backup stopped pinging: once, every integration of
        // the project (its email included) is turned on for them.
        foreach (var backup in _config.Current.Backups.Where(b => !b.HealthcheckChannelsAssigned && Guid.TryParse(b.HealthcheckId, out _)).ToList())
        {
            if (ct.IsCancellationRequested) break;
            if (!await AssignAllChannelsAsync(backup.HealthcheckId!, ct).ConfigureAwait(false)) continue;
            _config.Update(c => { if (c.Backups.FirstOrDefault(b => b.Id == backup.Id) is { } live) live.HealthcheckChannelsAssigned = true; });
        }
        // Periods and graces follow the settings (U211).
        foreach (var backup in _config.Current.Backups.ToList())
        {
            if (ct.IsCancellationRequested) break;
            await SyncAsync(backup, ct).ConfigureAwait(false);
        }
        return made;
    }

    /// <summary>Turns on every integration of the project for this check,
    /// only if Backspace made it (tagged "backspace"); a check you linked
    /// yourself is left as it is.</summary>
    private async Task<bool> AssignAllChannelsAsync(string uuid, CancellationToken ct)
    {
        if (!TryGetApiKey(out var apiKey)) return false;
        try
        {
            if (await TagsAsync(uuid, apiKey, ct).ConfigureAwait(false) is not { } tags) return false;
            if (!tags.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("backspace")) return true;
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/v3/checks/{uuid}");
            req.Headers.Add("X-Api-Key", apiKey);
            req.Content = JsonContent.Create(new { channels = "*" });
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) _log.Warning("Healthchecks.io refused to turn on the integrations of a check (HTTP {Status})", (int)resp.StatusCode);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.Warning(ex, "Could not turn on the integrations of a Healthchecks.io check"); return false; }
    }

    /// <summary>The check's tags, or null when it can't be read.</summary>
    private async Task<string?> TagsAsync(string uuid, string apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/v3/checks/{uuid}");
        req.Headers.Add("X-Api-Key", apiKey);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.String ? tags.GetString() ?? "" : "";
    }

    /// <summary>A removed backup's check goes too (U209, owner, 12 November
    /// 2026), if Backspace made it (tagged "backspace"); one you linked
    /// yourself is kept. Never throws: removing the backup doesn't depend on it.</summary>
    public async Task RemoveCheckOfAsync(Backup backup, CancellationToken ct)
    {
        await RemoveOneAsync(backup, backup.HealthcheckId, ct).ConfigureAwait(false);
        await RemoveOneAsync(backup, Uuid(backup.HealthcheckChecksPingUrl), ct).ConfigureAwait(false);
    }

    private async Task RemoveOneAsync(Backup backup, string? uuid, CancellationToken ct)
    {
        if (!Guid.TryParse(uuid, out _) || !TryGetApiKey(out var apiKey)) return;
        try
        {
            var tags = await TagsAsync(uuid!, apiKey, ct).ConfigureAwait(false);
            if (tags is null || !tags.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("backspace"))
            {
                _log.Information("Kept the Healthchecks.io check of removed backup {Backup}: not found or not made by Backspace", backup.Name);
                return;
            }
            var deleted = await DeleteCheckAsync(uuid!, ct).ConfigureAwait(false);
            if (deleted) _config.Update(c => c.Monitoring.HealthchecksSynced.Remove(uuid!));
            _log.Information(deleted ? "Removed the Healthchecks.io check of removed backup {Backup}" : "Healthchecks.io refused to remove the check of removed backup {Backup}", backup.Name);
        }
        catch (Exception ex) { _log.Warning(ex, "Could not remove the Healthchecks.io check of removed backup {Backup}", backup.Name); }
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
            ? CleanApiKey(inlineApiKey)
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
                var check = await resp.Content.ReadFromJsonAsync<HealthcheckRecord>(Json, ct);
                return UuidVerificationResult.Valid() with { PingUrl = check?.PingUrl };
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
        public string? PingUrl { get; init; }
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
    public Task<bool> PingStartAsync(string pingUuid, CancellationToken ct) =>
        PingAsync(pingUuid, "/start", null, ct);

    public Task<bool> PingSuccessAsync(string pingUuid, string body, CancellationToken ct) =>
        PingAsync(pingUuid, "", body, ct);

    public Task<bool> PingFailureAsync(string pingUuid, string body, CancellationToken ct) =>
        PingAsync(pingUuid, "/fail", body, ct);

    private async Task<bool> PingAsync(string pingUuid, string suffix, string? body, CancellationToken ct)
    {
        try
        {
            var url = BuildPingUrl(pingUuid, suffix);
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
            if (!resp.IsSuccessStatusCode)
            {
                _log.Warning("Healthchecks ping {Suffix} rejected for check …{UuidTail}: HTTP {Status}", suffix, TailOfUuid(url), (int)resp.StatusCode);
                return false;
            }
            _log.Debug("Healthchecks.io ping {Suffix} for check (uuid …{UuidTail}): HTTP {Status}",
                string.IsNullOrEmpty(suffix) ? "/success" : suffix, TailOfId(pingUuid), (int)resp.StatusCode);
            return true;
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
            return false;
        }
    }

    internal string BuildPingUrl(string pingUuidOrUrl, string suffix)
    {
        if (Uri.TryCreate(pingUuidOrUrl, UriKind.Absolute, out var explicitUrl)
            && explicitUrl.Scheme is "https" or "http")
            return explicitUrl.AbsoluteUri.TrimEnd('/') + suffix;
        if (!Guid.TryParse(pingUuidOrUrl, out var uuid))
            throw new ArgumentException("Healthcheck needs a UUID or a valid ping URL.", nameof(pingUuidOrUrl));
        return $"https://hc-ping.com/{uuid:D}{suffix}";
    }

    // ---- schedule translation ----

    internal static string ScheduleTimeZoneId(TimeZoneInfo zone)
    {
        // Healthchecks accepts IANA names. Windows normally supplies names
        // such as "Hawaiian Standard Time", which the live API rejects.
        if (zone.HasIanaId) return zone.Id;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana)) return iana;
        throw new InvalidOperationException("This Windows time zone cannot be translated for Healthchecks. Choose a standard system time zone before connecting the schedule.");
    }

    /// <summary>What a Backspace check on Healthchecks.io should say (U211):
    /// its name and tags, and the expected period with its grace.</summary>
    private static UpsertRequest Desired(Backup backup, bool forChecks)
    {
        var (period, grace) = Timing(forChecks ? ChecksPeriodDays(backup) : QuietDays(backup));
        return new UpsertRequest
        {
            Name = forChecks ? backup.Name + " · checks" : backup.Name,
            Tags = forChecks ? $"backspace backup-{backup.Name} checks" : $"backspace backup-{backup.Name}",
            Desc = forChecks
                ? $"Auto-managed by Backspace: the checks of the backup {backup.Name} (structure, data sample, test restore). Down when none has passed for the period and its grace."
                : $"Auto-managed by Backspace. Down when the backup has not succeeded for the period and its grace. Sources: {string.Join(" ; ", backup.SourcePaths)}",
            Timeout = (int)period.TotalSeconds,
            Grace = (int)grace.TotalSeconds,
        };
    }

    /// <summary>The quiet skipped-run alerts period, at least a day.</summary>
    internal static int QuietDays(Backup backup) => Math.Max(1, backup.SkipNotificationGraceDays);

    /// <summary>The checks' period: the longest time between two runs of a
    /// check (one after each backup counts as the quiet period), and never
    /// shorter than the quiet period, since a check that finds the drive
    /// unplugged waits for the next backup there.</summary>
    internal static int ChecksPeriodDays(Backup backup)
    {
        var days = QuietDays(backup);
        foreach (var check in backup.Checks)
            days = Math.Max(days, check.Schedule?.Frequency switch
            {
                null => QuietDays(backup),
                ScheduleFrequency.Weekly => 7,
                ScheduleFrequency.Monthly => 31,
                ScheduleFrequency.Yearly => 366,
                _ => 1,
            });
        return days;
    }

    /// <summary>The expected period (at most 365 days, the API's limit) and
    /// its grace: 3 days under a month, 7 from a month, 14 from six months
    /// (owner, 12 November 2026).</summary>
    internal static (TimeSpan Period, TimeSpan Grace) Timing(int days)
    {
        var period = TimeSpan.FromDays(Math.Clamp(days, 1, 365));
        var grace = TimeSpan.FromDays(days >= 180 ? 14 : days >= 30 ? 7 : 3);
        return (period, grace);
    }

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

        /// <summary>"*": every integration of the project, the account's
        /// email included (U210, owner: "Can you set Email on by default?").</summary>
        public string? Channels { get; set; }
    }

    /// <summary>Brings Backspace's checks of this backup in line with its
    /// settings (U211): name, period and grace, when they differ from what
    /// was last sent. A check you linked yourself is left as it is.</summary>
    public async Task SyncAsync(Backup backup, CancellationToken ct)
    {
        if (!TryGetApiKey(out var apiKey)) return;
        foreach (var (uuid, forChecks) in new[] { (backup.HealthcheckId, false), (Uuid(backup.HealthcheckChecksPingUrl), true) })
        {
            if (!Guid.TryParse(uuid, out _)) continue;
            var desired = Desired(backup, forChecks);
            var fingerprint = $"{uuid}|{desired.Name}|{desired.Tags}|{desired.Timeout}|{desired.Grace}";
            if (_config.Current.Monitoring.HealthchecksSynced.TryGetValue(uuid!, out var last) && last == fingerprint) continue;
            try
            {
                var tags = await TagsAsync(uuid!, apiKey, ct).ConfigureAwait(false);
                if (tags is null) continue;
                if (!tags.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("backspace")) { Remember(uuid!, fingerprint); continue; }
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/v3/checks/{uuid}");
                req.Headers.Add("X-Api-Key", apiKey);
                req.Content = JsonContent.Create(desired, options: Json);
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) Remember(uuid!, fingerprint);
                else _log.Warning("Healthchecks.io refused to update a check of {Backup} (HTTP {Status})", backup.Name, (int)resp.StatusCode);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.Warning(ex, "Could not update a Healthchecks.io check of {Backup}", backup.Name); }
        }
    }

    /// <summary>Kept in the settings, so a start sends nothing unless a
    /// setting changed while Backspace was closed (U211).</summary>
    private void Remember(string uuid, string fingerprint) =>
        _config.Update(c => c.Monitoring.HealthchecksSynced[uuid] = fingerprint);

    /// <summary>The UUID at the end of a ping address.</summary>
    internal static string? Uuid(string? pingUrl)
        => string.IsNullOrEmpty(pingUrl) ? null : pingUrl.TrimEnd('/').Split('/').Last() is var last && Guid.TryParse(last, out _) ? last : null;

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
