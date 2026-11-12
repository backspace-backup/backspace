using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.OAuth;
using Backspace.Services.Platform;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class CloudNotificationRepairTests
{
    [Theory]
    [InlineData("Hawaiian Standard Time", "Pacific/Honolulu")]
    [InlineData("Pacific Standard Time", "America/Los_Angeles")]
    [InlineData("Pacific/Honolulu", "Pacific/Honolulu")]
    public void Healthchecks_schedule_uses_provider_timezone_names(string systemId, string expected)
        => Assert.Equal(expected, HealthcheckService.ScheduleTimeZoneId(TimeZoneInfo.FindSystemTimeZoneById(systemId)));

    [Fact]
    public void Healthchecks_does_not_silently_replace_unknown_timezone_with_UTC()
        => Assert.Throws<InvalidOperationException>(() => HealthcheckService.ScheduleTimeZoneId(
            TimeZoneInfo.CreateCustomTimeZone("unknown-review-zone", TimeSpan.FromHours(2), "Review", "Review")));

    /// <summary>U211 (owner, 12 November 2026): the expected period is the
    /// quiet skipped-run alerts period; the grace 3 days under a month, 7
    /// from a month, 14 from six months; the API takes at most 365 days.</summary>
    [Theory]
    [InlineData(7, 7, 3)]
    [InlineData(29, 29, 3)]
    [InlineData(30, 30, 7)]
    [InlineData(179, 179, 7)]
    [InlineData(180, 180, 14)]
    [InlineData(400, 365, 14)]
    [InlineData(0, 1, 3)]
    public async Task Healthchecks_period_is_the_quiet_period_with_its_grace(int quietDays, int periodDays, int graceDays)
    {
        using var setup = new Setup(); var backup = setup.AddBackup();
        setup.Secrets.Set("hc-key", "synthetic-key");
        setup.Config.Update(c => c.Monitoring.HealthchecksApiKeyRef = "hc-key");
        backup.Schedule.Frequency = ScheduleFrequency.Daily;

        backup.Schedule.TimeOfDay = TimeSpan.FromHours(3) + TimeSpan.FromMinutes(17);
        backup.SkipNotificationGraceDays = quietDays;
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(periodDays * 86400, body.RootElement.GetProperty("timeout").GetInt32());
            Assert.Equal(graceDays * 86400, body.RootElement.GetProperty("grace").GetInt32());
            Assert.False(body.RootElement.TryGetProperty("schedule", out _));
            Assert.Equal("*", body.RootElement.GetProperty("channels").GetString()); // every integration, email included (U210)
            Assert.False(body.RootElement.TryGetProperty("unique_by", out _));
            Assert.Equal(new[] { "name", "tags" }, body.RootElement.GetProperty("unique").EnumerateArray().Select(x => x.GetString()));
            return Json("{\"ping_url\":\"https://hc-ping.com/11111111-2222-3333-4444-555555555555\"}");
        }));
        var result = await new HealthcheckService(setup.Config, setup.Secrets, client).UpsertCheckAsync(backup, default);
        Assert.NotNull(result); Assert.Equal(result.PingUrl, backup.HealthcheckPingUrl);
    }

    /// <summary>U187: Healthchecks turned on in Settings with a key, but no
    /// backup had a check, so nothing was ever pinged. Each backup without
    /// one now gets it; one with a check, or Healthchecks off, is left alone.</summary>
    [Fact]
    public async Task Healthchecks_on_gives_each_backup_without_a_check_its_check()
    {
        using var setup = new Setup();
        var existing = setup.AddBackup();
        var bare = new Backup { Name = "no-check-yet", CreatedUtc = DateTime.UtcNow, SkipNotificationGraceDays = 7 };
        setup.Config.Update(c => c.Backups.Add(bare));
        setup.Secrets.Set("hc-key", "synthetic-key");
        var asked = new List<string>();
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            // The existing check is one you linked (no "backspace" tag): left as it is (U210).
            if (request.Method == HttpMethod.Get) return Json("{\"tags\":\"mine\"}");
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            asked.Add(body.RootElement.GetProperty("name").GetString()!);
            return Json("{\"ping_url\":\"https://hc-ping.com/11111111-2222-3333-4444-555555555555\"}");
        }));
        var service = new HealthcheckService(setup.Config, setup.Secrets, client);

        // Off, or on without a key: nothing is created.
        setup.Config.Update(c => { c.Monitoring.HealthchecksEnabled = false; c.Monitoring.HealthchecksApiKeyRef = "hc-key"; });
        Assert.Equal(0, await service.EnsureChecksAsync(default));
        setup.Config.Update(c => { c.Monitoring.HealthchecksEnabled = true; c.Monitoring.HealthchecksApiKeyRef = null; });
        Assert.Equal(0, await service.EnsureChecksAsync(default));
        Assert.Empty(asked);

        setup.Config.Update(c => c.Monitoring.HealthchecksApiKeyRef = "hc-key");
        Assert.Equal(1, await service.EnsureChecksAsync(default));
        Assert.Equal(new[] { "no-check-yet" }, asked);
        var saved = setup.Config.Current.Backups.Single(b => b.Id == bare.Id);
        Assert.Equal("https://hc-ping.com/11111111-2222-3333-4444-555555555555", saved.HealthcheckPingUrl);
        Assert.Equal("11111111-2222-3333-4444-555555555555", saved.HealthcheckId);
        Assert.Equal(existing.HealthcheckPingUrl, setup.Config.Current.Backups.Single(b => b.Id == existing.Id).HealthcheckPingUrl);
        // Run again: every backup has its check, nothing is asked.
        Assert.Equal(0, await service.EnsureChecksAsync(default));
        Assert.Single(asked);
    }

    /// <summary>U187, the owner's log: "Request headers must contain only
    /// ASCII characters" for every backup, so no check was ever created. A
    /// key pasted with an invisible character around it is now sent as the
    /// key alone, through .NET's real HTTP stack to a local stand-in server;
    /// a key still not a key says so in plain words.</summary>
    [Theory]
    [InlineData("​synthetic-key-123 ")]
    [InlineData("﻿synthetic-key-123\r\n")]
    [InlineData(" synthetic-key-123 ")]
    public async Task A_key_pasted_with_invisible_characters_still_creates_the_checks(string pasted)
    {
        using var setup = new Setup();
        var bare = new Backup { Name = "no-check-yet", CreatedUtc = DateTime.UtcNow, SkipNotificationGraceDays = 7 };
        setup.Config.Update(c => c.Backups.Add(bare));
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var keys = new List<string>();
        var server = Task.Run(async () =>
        {
            // Each request on its own connection, until the listener stops.
            while (true)
            {
                System.Net.Sockets.TcpClient socket;
                try { socket = await listener.AcceptTcpClientAsync(); } catch (Exception) { return; }
                using (socket)
                {
                    using var stream = socket.GetStream();
                    var buffer = new byte[64 * 1024]; var read = 0; string text;
                    while (true)
                    {
                        read += await stream.ReadAsync(buffer.AsMemory(read));
                        text = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
                        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (end < 0) continue;
                        // The JSON body is sent with a length, in chunks, or not at all.
                        var length = System.Text.RegularExpressions.Regex.Match(text, @"Content-Length: (\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        var chunked = text.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase);
                        if (length.Success ? read >= end + 4 + int.Parse(length.Groups[1].Value) : !chunked || text.EndsWith("\r\n0\r\n\r\n", StringComparison.Ordinal)) break;
                    }
                    lock (keys) keys.Add(System.Text.RegularExpressions.Regex.Match(text, @"X-Api-Key: ([^\r]*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value);
                    var body = text.StartsWith("GET", StringComparison.Ordinal) ? "{\"tags\":\"my-own\"}" : "{\"ping_url\":\"https://hc-ping.com/11111111-2222-3333-4444-555555555555\"}";
                    var reply = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 201 Created\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
                    await stream.WriteAsync(reply);
                }
            }
        });
        setup.Secrets.Set("hc-key", pasted);
        setup.Config.Update(c => { c.Monitoring.HealthchecksEnabled = true; c.Monitoring.HealthchecksApiKeyRef = "hc-key"; c.Monitoring.HealthchecksBaseUrl = $"http://127.0.0.1:{port}"; });
        using var client = new HttpClient();
        var service = new HealthcheckService(setup.Config, setup.Secrets, client);
        var made = await service.EnsureChecksAsync(default);
        Assert.True(made == 1, "problem: " + service.LastProblem);
        listener.Stop(); await server;
        Assert.NotEmpty(keys); Assert.All(keys, k => Assert.Equal("synthetic-key-123", k));
        Assert.Null(service.LastProblem);
        Assert.Equal("11111111-2222-3333-4444-555555555555", setup.Config.Current.Backups.Single(b => b.Id == bare.Id).HealthcheckId);
    }

    /// <summary>U210 (owner: "Can you set Email on by default?"): a check
    /// Backspace made before had no integration; once, every integration of
    /// the project (email included) is turned on for it. A check you linked
    /// yourself is left as it is, and it happens only once.</summary>
    [Fact]
    public async Task Backspace_checks_get_every_integration_once()
    {
        using var setup = new Setup();
        var ours = setup.AddBackup();
        var linked = setup.AddBackup();
        setup.Secrets.Set("hc-key", "synthetic-key");
        setup.Config.Update(c => { c.Monitoring.HealthchecksEnabled = true; c.Monitoring.HealthchecksApiKeyRef = "hc-key"; });
        var calls = new List<string>();
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            var id = request.RequestUri!.AbsolutePath.Split('/').Last();
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            calls.Add($"{request.Method} {(id == ours.HealthcheckId ? "ours" : id == linked.HealthcheckId ? "linked" : id)} {body}");
            if (request.Method == HttpMethod.Get)
                return Json(id == ours.HealthcheckId ? "{\"tags\":\"backspace backup-notification-proof\"}" : "{\"tags\":\"my-own\"}");
            return Json("{}");
        }));
        var service = new HealthcheckService(setup.Config, setup.Secrets, client);
        Assert.Equal(0, await service.EnsureChecksAsync(default));
        Assert.Equal(new[] { "GET ours ", "POST ours {\"channels\":\"*\"}", "GET linked " }, calls.Take(3));
        // Then the period and grace are brought in line (U211), only on Backspace's own check.
        Assert.Equal(new[] { "GET ours ", "GET linked " }, calls.Skip(3).Where(c => c.StartsWith("GET", StringComparison.Ordinal)));
        Assert.Single(calls.Skip(3), c => c.StartsWith("POST ours {\"name\"", StringComparison.Ordinal));
        Assert.All(setup.Config.Current.Backups, b => Assert.True(b.HealthcheckChannelsAssigned));
        calls.Clear();
        Assert.Equal(0, await service.EnsureChecksAsync(default));
        Assert.Empty(calls);
    }

    /// <summary>U211: the checks' period is the longest time between two runs
    /// of a check, never shorter than the quiet period.</summary>
    [Fact]
    public void The_checks_period_follows_the_slowest_check()
    {
        var backup = new Backup { SkipNotificationGraceDays = 7 };
        Assert.Equal(7, HealthcheckService.ChecksPeriodDays(backup));
        backup.Checks.Add(new BackupCheck { Kind = CheckKind.Structure }); // after each backup
        Assert.Equal(7, HealthcheckService.ChecksPeriodDays(backup));
        backup.Checks.Add(new BackupCheck { Kind = CheckKind.TestRestore, Schedule = new BackupSchedule { Frequency = ScheduleFrequency.Monthly } });
        Assert.Equal(31, HealthcheckService.ChecksPeriodDays(backup));
        Assert.Equal((TimeSpan.FromDays(31), TimeSpan.FromDays(7)), HealthcheckService.Timing(31));
    }

    /// <summary>U211: a backup with checks gets a second check for them; a
    /// changed quiet period updates both, once; a passed check pings the
    /// checks' check, a failed one says so.</summary>
    [Fact]
    public async Task A_second_check_for_the_checks_follows_the_settings()
    {
        using var setup = new Setup();
        var backup = setup.AddBackup();
        var ours = "22222222-3333-4444-5555-666666666666";
        setup.Config.Update(c =>
        {
            var b = c.Backups.Single(x => x.Id == backup.Id);
            b.HealthcheckId = Guid.NewGuid().ToString(); b.HealthcheckChannelsAssigned = true;
            b.Checks.Add(new BackupCheck { Kind = CheckKind.DataSample, Schedule = new BackupSchedule { Frequency = ScheduleFrequency.Weekly } });
            c.Monitoring.HealthchecksEnabled = true; c.Monitoring.HealthchecksApiKeyRef = "hc-key";
        });
        setup.Secrets.Set("hc-key", "synthetic-key");
        var calls = new List<string>();
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath} {body}");
            if (request.Method == HttpMethod.Get) return Json("{\"tags\":\"backspace backup-notification-proof\"}");
            return Json($"{{\"ping_url\":\"https://hc-ping.com/{ours}\"}}");
        }));
        var service = new HealthcheckService(setup.Config, setup.Secrets, client);
        await service.EnsureChecksAsync(default);
        var saved = setup.Config.Current.Backups.Single(b => b.Id == backup.Id);
        Assert.Equal($"https://hc-ping.com/{ours}", saved.HealthcheckChecksPingUrl);
        var created = calls.Single(c => c.StartsWith("POST /api/v3/checks/ ", StringComparison.Ordinal));
        using (var json = System.Text.Json.JsonDocument.Parse(created["POST /api/v3/checks/ ".Length..]))
        {
            Assert.Equal("notification-proof · checks", json.RootElement.GetProperty("name").GetString());
            Assert.Equal("backspace backup-notification-proof checks", json.RootElement.GetProperty("tags").GetString());
        }
        Assert.Contains($"\"timeout\":{7 * 86400}", created);
        Assert.Contains($"\"grace\":{3 * 86400}", created);
        // Both checks were brought in line; nothing more is sent while nothing changes.
        calls.Clear();
        await service.EnsureChecksAsync(default);
        Assert.Empty(calls);
        // The quiet period becomes 45 days: both checks follow, with 7 days of grace.
        setup.Config.Update(c => c.Backups.Single(x => x.Id == backup.Id).SkipNotificationGraceDays = 45);
        await service.EnsureChecksAsync(default);
        var updates = calls.Where(c => c.StartsWith("POST", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, updates.Count);
        Assert.All(updates, u => Assert.Contains($"\"timeout\":{45 * 86400},\"grace\":{7 * 86400}", u.Replace("\"desc\"", "\"desc\"")));
        Assert.Contains(updates, u => u.StartsWith($"POST /api/v3/checks/{ours} ", StringComparison.Ordinal));
    }

    /// <summary>U211: a check run pings the check for the backup's checks:
    /// success when it passed, /fail when it failed.</summary>
    [Fact]
    public async Task A_check_run_pings_the_checks_check()
    {
        using var setup = new Setup(); var backup = setup.AddBackup();
        var destination = new Destination { Name = "Checked fixture", Kind = DestinationKind.LocalFolder, PathOrSubpath = setup.Source() };
        backup.SourcePaths.Add(setup.Source()); backup.Targets.Add(new() { DestinationId = destination.Id, StorageName = "default" });
        backup.HealthcheckChecksPingUrl = "https://hc-ping.com/33333333-4444-5555-6666-777777777777";
        var check = new BackupCheck { Kind = CheckKind.Structure };
        backup.Checks.Add(check);
        setup.Config.Update(c => { c.Backups.Clear(); c.Backups.Add(backup); c.Destinations.Add(destination); });
        var pings = new List<string>();
        using var client = new HttpClient(new Handler((r, _) => { lock (pings) pings.Add(r.RequestUri!.AbsolutePath); return Task.FromResult(Json("{}")); }));
        var run = await setup.Orchestrator(client, client).RunCheckAsync(backup, check, default);
        Assert.Equal(BackupRunStatus.Success, run.Status);
        Assert.Equal(new[] { "/33333333-4444-5555-6666-777777777777" }, pings);
    }

    /// <summary>U209 (owner: "if we delete a backup, make sure you also remove
    /// the healthcheck for it if any"): Backspace's own check is deleted; one
    /// you linked yourself is kept.</summary>
    [Fact]
    public async Task Removing_a_backup_removes_its_own_check_only()
    {
        using var setup = new Setup();
        var ours = setup.AddBackup();
        var linked = setup.AddBackup();
        setup.Secrets.Set("hc-key", "synthetic-key");
        setup.Config.Update(c => { c.Monitoring.HealthchecksEnabled = true; c.Monitoring.HealthchecksApiKeyRef = "hc-key"; });
        var calls = new List<string>();
        using var client = new HttpClient(new Handler((request, _) =>
        {
            var id = request.RequestUri!.AbsolutePath.Split('/').Last();
            calls.Add($"{request.Method} {(id == ours.HealthcheckId ? "ours" : "linked")}");
            return Task.FromResult(request.Method == HttpMethod.Get
                ? Json(id == ours.HealthcheckId ? "{\"tags\":\"backspace backup-x\"}" : "{\"tags\":\"my-own\"}") : Json("{}"));
        }));
        var service = new HealthcheckService(setup.Config, setup.Secrets, client);
        await service.RemoveCheckOfAsync(ours, default);
        await service.RemoveCheckOfAsync(linked, default);
        Assert.Equal(new[] { "GET ours", "DELETE ours", "GET linked" }, calls);
        // No key: nothing is asked, and removing the backup goes on.
        setup.Config.Update(c => c.Monitoring.HealthchecksApiKeyRef = null);
        calls.Clear();
        await service.RemoveCheckOfAsync(ours, default);
        Assert.Empty(calls);
    }

    /// <summary>U208: Settings says green or red whether the key works, also
    /// when every backup already has its check: it asks for the project's checks.</summary>
    [Fact]
    public async Task The_key_is_checked_against_Healthchecks()
    {
        using var setup = new Setup();
        setup.Secrets.Set("hc-key", "synthetic-key");
        setup.Config.Update(c => { c.Monitoring.HealthchecksEnabled = true; c.Monitoring.HealthchecksApiKeyRef = "hc-key"; });
        var status = 200;
        string? asked = null;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            asked = request.Method + " " + request.RequestUri!.AbsolutePath + " " + string.Join("", request.Headers.GetValues("X-Api-Key"));
            return Task.FromResult(Json("{\"checks\":[]}", status));
        }));
        var service = new HealthcheckService(setup.Config, setup.Secrets, client);
        Assert.Null(await service.VerifyKeyAsync(default));
        Assert.Equal("GET /api/v3/checks/ synthetic-key", asked);
        status = 401;
        Assert.Equal("Healthchecks.io refused the API key. Use the project's regular API key, not the read-only one.", await service.VerifyKeyAsync(default));
        setup.Config.Update(c => c.Monitoring.HealthchecksApiKeyRef = null);
        Assert.Equal("No API key is saved.", await service.VerifyKeyAsync(default));
    }

    [Fact]
    public async Task A_key_that_is_not_a_key_says_so()
    {
        using var setup = new Setup();
        setup.Config.Update(c => c.Backups.Add(new Backup { Name = "no-check-yet", CreatedUtc = DateTime.UtcNow, SkipNotificationGraceDays = 7 }));
        setup.Secrets.Set("hc-key", "clé-synthétique");
        // A server that accepts the connection: the key is refused while the request is written, as in the owner's log.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var accepted = listener.AcceptTcpClientAsync();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        setup.Config.Update(c => { c.Monitoring.HealthchecksEnabled = true; c.Monitoring.HealthchecksApiKeyRef = "hc-key"; c.Monitoring.HealthchecksBaseUrl = $"http://127.0.0.1:{port}"; });
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var service = new HealthcheckService(setup.Config, setup.Secrets, client);
        Assert.Equal(0, await service.EnsureChecksAsync(default));
        Assert.Equal("The API key contains characters a Healthchecks.io key never has. Copy it again from healthchecks.io and paste it here.", service.LastProblem);
        listener.Stop(); try { (await accepted).Dispose(); } catch { }
    }

    [Fact]
    public async Task Real_orchestrator_expired_grace_keeps_unmounted_drive_skipped()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var setup = new Setup(); var backup = setup.AddBackup();
        var destination = new Destination { Name = "Absent fixture drive", Kind = DestinationKind.ExternalDrive,
            ExpectedVolumeLabel = "backspace-no-such-volume-" + Guid.NewGuid(), PathOrSubpath = @"Q:\fixture" };
        backup.SourcePaths.Add(setup.Source()); backup.Targets.Add(new() { DestinationId = destination.Id, StorageName = "default" });
        backup.CreatedUtc = DateTime.UtcNow.AddDays(-12);
        setup.Config.Update(c => { c.Backups.Clear(); c.Backups.Add(backup); c.Destinations.Add(destination); });
        var pings = new List<string>();
        using var client = new HttpClient(new Handler((r, _) => { pings.Add(r.RequestUri!.AbsolutePath); return Task.FromResult(Json("{}")); }));
        var run = await setup.Orchestrator(client, client).RunBackupAsync(backup, default);
        Assert.Equal(BackupRunStatus.Skipped, run.Status);
        Assert.Equal(BackupRunStatus.Skipped, setup.Config.Current.Backups.Single().LastRunStatus);
        Assert.All(run.Cells, cell => Assert.Equal(BackupRunStatus.Skipped, cell.Status));
        Assert.Single(pings); Assert.EndsWith("/start", pings[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_orchestrator_missing_share_fails_during_grace_and_retries_rejected_ping_at_completion(bool rejectFirstFailure)
    {
        using var setup = new Setup(); var backup = setup.AddBackup();
        var destination = new Destination { Name = "Wrong fixture path", Kind = DestinationKind.NetworkShare,
            PathOrSubpath = Path.Combine(setup.Source(), "does-not-exist") };
        backup.SourcePaths.Add(setup.Source()); backup.Targets.Add(new() { DestinationId = destination.Id, StorageName = "default" });
        setup.Config.Update(c => { c.Backups.Clear(); c.Backups.Add(backup); c.Destinations.Add(destination); });
        var pings = new List<string>();
        using var client = new HttpClient(new Handler((r, _) =>
        {
            pings.Add(r.RequestUri!.AbsolutePath);
            return Task.FromResult(Json("{}", rejectFirstFailure && pings.Count == 2 ? 503 : 200));
        }));
        var run = await setup.Orchestrator(client, client).RunBackupAsync(backup, default);
        Assert.Equal(BackupRunStatus.Failed, run.Status);
        Assert.Single(run.Cells); Assert.Equal(BackupRunStatus.Failed, run.Cells[0].Status);
        Assert.Equal(rejectFirstFailure ? 3 : 2, pings.Count);
        Assert.EndsWith("/start", pings[0]); Assert.EndsWith("/fail", pings[1]);
        Assert.EndsWith("/fail", pings[^1]);
    }

    [Fact]
    public async Task Shared_rclone_file_preserves_concurrent_destinations_and_refreshed_tokens()
    {
        using var ws = new TempWorkspace();
        var secrets = new SecretsStore();
        var a = Cloud("aaaaaaaa", "a"); var b = Cloud("bbbbbbbb", "b");
        secrets.Set("a", Token("old-a", 1)); secrets.Set("b", Token("old-b", 1));
        var builder = new RcloneConfigBuilder(secrets);
        await Task.WhenAll(Task.Run(() => builder.Materialise(new[] { a })), Task.Run(() => builder.Materialise(new[] { b })));
        var path = RcloneConfigBuilder.DefaultPath;
        var text = File.ReadAllText(path);
        Assert.Contains("[dropbox-app-aaaaaaaa]", text);
        Assert.Contains("[dropbox-app-bbbbbbbb]", text);
        File.WriteAllText(path, text.Replace(Token("old-a", 1), Token("refreshed-a", 3)));
        builder.Materialise(new[] { a });
        Assert.Equal(Token("refreshed-a", 3), secrets.Get("a"));
        Assert.Contains(Token("refreshed-a", 3), File.ReadAllText(path));
        Assert.Contains(Token("old-b", 1), File.ReadAllText(path));
        var separateA = builder.MaterialiseDestination(a);
        var separateB = builder.MaterialiseDestination(b);
        Assert.NotEqual(separateA, separateB);
        Assert.DoesNotContain("bbbbbbbb", File.ReadAllText(separateA));
        Assert.DoesNotContain("aaaaaaaa", File.ReadAllText(separateB));
        Assert.Equal(Token("refreshed-a", 3), secrets.Get("a"));
    }

    [Fact]
    public void New_sign_in_wins_over_stale_materialized_token_even_with_later_expiry()
    {
        using var ws = new TempWorkspace();
        var secrets = new SecretsStore();
        var a = Cloud("aaaaaaaa", "a");
        secrets.Set("a", Token("previous-account", 5));
        var builder = new RcloneConfigBuilder(secrets); var path = builder.MaterialiseDestination(a);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-2));
        a.LastTokenValidatedUtc = DateTime.UtcNow;
        secrets.Set("a", Token("new-account", 1));
        builder.MaterialiseDestination(a);
        Assert.Equal(Token("new-account", 1), secrets.Get("a"));
        Assert.DoesNotContain("previous-account", File.ReadAllText(path));
        Assert.False(RcloneConfigBuilder.IsNewerToken("not-json", Token("new-account", 1)));
        Assert.False(RcloneConfigBuilder.IsNewerToken(Token("older", 0), Token("new-account", 1)));
    }

    [Theory]
    [InlineData("personal", DestinationKind.OneDrivePersonal)]
    [InlineData("business", DestinationKind.OneDriveBusiness)]
    [InlineData("documentLibrary", DestinationKind.OneDriveBusiness)]
    public async Task OneDrive_discovers_actual_drive_and_emits_required_configuration(string type, DestinationKind kind)
    {
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("fake-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("/v1.0/me/drive/special/approot", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json($"{{\"id\":\"app-folder\",\"folder\":{{}},\"parentReference\":{{\"driveId\":\"drive-123\",\"driveType\":\"{type}\"}}}}"));
        }));
        var drive = await new OneDriveDiscovery(client).DiscoverAsync("{\"access_token\":\"fake-token\"}", default);
        var destination = new Destination { Kind = kind, OneDriveDriveId = drive.Id, OneDriveDriveType = drive.DriveType,
            OneDriveAppFolderId = drive.AppFolderId, CloudAppFolderOnly = true };
        var output = RcloneConfigBuilder.Render(new[] { destination }, Array.Empty<RcloneOAuthApp>(), _ => "");
        Assert.Contains("drive_id = drive-123", output);
        Assert.Contains("drive_type = " + type, output);
        Assert.Contains("root_folder_id = app-folder", output);
        Assert.Contains("access_scopes = Files.ReadWrite.AppFolder offline_access", output);
        Assert.DoesNotContain("Files.ReadWrite.All", output);
    }

    [Theory]
    [InlineData(401, "{}")]
    [InlineData(200, "{\"driveType\":\"personal\"}")]
    [InlineData(200, "{\"id\":\"a\",\"driveType\":\"unknown\"}")]
    public async Task OneDrive_rejects_failed_or_incomplete_discovery_without_leaking_credentials(int status, string body)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json(body, status))));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new OneDriveDiscovery(client)
            .DiscoverAsync("{\"access_token\":\"private-test-token\"}", default));
        Assert.DoesNotContain("private-test-token", failure.Message);
    }

    [Fact]
    public void Google_scope_matches_the_grant_and_does_not_claim_full_drive()
    {
        var output = RcloneConfigBuilder.Render(new[] { new Destination { Kind = DestinationKind.GoogleDrive, CloudAppFolderOnly = true } },
            Array.Empty<RcloneOAuthApp>(), _ => "");
        Assert.Contains("scope = drive.appfolder", output);
        Assert.Contains("root_folder_id = appDataFolder", output);
        Assert.Contains("use_trash = false", output);
        Assert.DoesNotContain("scope = drive\n", output.Replace("\r\n", "\n"));
        Assert.EndsWith("/drive.appdata", new GoogleDriveOAuthProvider("fake-client").Scope);
    }

    [Fact]
    public async Task Healthcheck_terminal_runs_ignore_daily_cap_and_precede_slow_email()
    {
        using var setup = new Setup();
        var backup = setup.AddBackup();
        setup.Config.Update(c => { c.Mail.Provider = MailProvider.Resend; c.Mail.ToAddresses.Add("test@example.invalid"); c.Mail.ResendApiKeyRef = "mail"; c.Notifications.OnSuccess.Email = true; });
        setup.Secrets.Set("mail", "fake-key");
        var pings = new List<string>();
        var mailEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowMail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hcClient = new HttpClient(new Handler((r, _) => { pings.Add(r.RequestUri!.AbsoluteUri); return Task.FromResult(Json("{}")); }));
        using var mailClient = new HttpClient(new Handler(async (_, _) => { Assert.Single(pings); mailEntered.TrySetResult(); await allowMail.Task; return Json("{}"); }));
        var orchestrator = setup.Orchestrator(hcClient, mailClient);
        var first = orchestrator.SendAlertsAsync(backup, Run(backup, BackupRunStatus.Success));
        await mailEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(pings); Assert.False(first.IsCompleted);
        allowMail.SetResult(); await first;
        await orchestrator.SendAlertsAsync(backup, Run(backup, BackupRunStatus.Success));
        Assert.Equal(2, pings.Count); // cap suppressed repeat mail, never monitoring.
        Assert.All(pings, url => Assert.StartsWith("https://monitor.example.invalid/ping/", url));
    }

    [Fact]
    public async Task Grace_suppresses_skip_alerts_then_warns_and_manual_reset_suppresses_again()
    {
        using var setup = new Setup(); var backup = setup.AddBackup();
        var seen = new TaskCompletionSource<NotificationSeverity>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        setup.Notify.NotificationObserved = (_, _, severity, _, _) => { Interlocked.Increment(ref count); seen.TrySetResult(severity); };
        using var client = new HttpClient(new Handler((_, _) => throw new Exception("A skipped run must not send a success/failure ping")));
        var orchestrator = setup.Orchestrator(client, client);
        var skipped = Run(backup, BackupRunStatus.Skipped);
        await orchestrator.SendAlertsAsync(backup, skipped);
        Assert.Equal(0, count);
        setup.Config.Update(c => c.Backups.Single().CreatedUtc = DateTime.UtcNow.AddDays(-9));
        await orchestrator.SendAlertsAsync(backup, skipped);
        Assert.Equal(NotificationSeverity.Warning, await seen.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(BackupRunStatus.Skipped, skipped.Status);
        setup.Config.Update(c => { c.Backups.Single().SkipNotificationGraceResetUtc = DateTime.UtcNow; c.Backups.Single().LastNotificationFiredUtc = null; });
        await orchestrator.SendAlertsAsync(backup, skipped);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Missing_email_recipient_produces_visible_nonfatal_delivery_alert()
    {
        using var setup = new Setup(); var backup = setup.AddBackup();
        setup.Config.Update(c => { c.Mail.Provider = MailProvider.Resend; c.Notifications.OnSuccess.Email = true; });
        var warning = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        setup.Notify.NotificationObserved = (title, detail, severity, _, channels) =>
        {
            if (!title.Contains("email alerts")) return;
            Assert.Equal(NotificationSeverity.Warning, severity); Assert.True(channels.Toast); Assert.False(channels.Email);
            warning.TrySetResult(detail);
        };
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json("{}"))));
        var record = Run(backup, BackupRunStatus.Success);
        await setup.Orchestrator(client, client).SendAlertsAsync(backup, record);
        Assert.Contains("TO address", await warning.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(BackupRunStatus.Success, record.Status);
    }

    [Theory]
    [InlineData("Fatal: wrong password or no key found", true)]
    [InlineData("401 Unauthorized", false)]
    [InlineData("Access denied to the archive", false)]
    public async Task Failure_alert_explains_repository_password_errors_without_mislabeling_other_errors(string reason, bool passwordError)
    {
        using var setup = new Setup(); var backup = setup.AddBackup();
        var shown = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        setup.Notify.NotificationObserved = (_, detail, severity, _, _) =>
        {
            if (severity == NotificationSeverity.Failure) shown.TrySetResult(detail);
        };
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json("{}"))));
        var record = Run(backup, BackupRunStatus.Failed);
        record.Errors.Add(passwordError ? new string('x', 500) + reason : reason);
        await setup.Orchestrator(client, client).SendAlertsAsync(backup, record);
        var detail = await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (passwordError) { Assert.Contains("backup password in Destinations", detail); Assert.DoesNotContain(new string('x', 20), detail); }
        else { Assert.Contains(reason, detail); Assert.DoesNotContain("backup password", detail); }
    }

    [Theory]
    [InlineData(503, 3, true)]
    [InlineData(429, 3, true)]
    [InlineData(401, 1, false)]
    [InlineData(-1, 3, true)]
    public async Task Email_retries_transient_failures_but_not_authentication_errors(int initialStatus, int expectedAttempts, bool succeeds)
    {
        using var setup = new Setup();
        setup.Config.Update(c => { c.Mail.Provider = MailProvider.Resend; c.Mail.ToAddresses.Add("test@example.invalid"); c.Mail.ResendApiKeyRef = "mail"; });
        setup.Secrets.Set("mail", "fake-key"); var attempts = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            attempts++;
            if (initialStatus == -1 && attempts < 3) throw new TaskCanceledException("simulated per-request timeout");
            return Task.FromResult(Json("{}", attempts == 3 ? 200 : initialStatus));
        }));
        var mail = new MailService(setup.Config, setup.Secrets, client) { DelayAsync = (_, _) => Task.CompletedTask };
        var result = await mail.SendAsync("test", "test", default);
        Assert.Equal(expectedAttempts, attempts); Assert.Equal(succeeds, result.Succeeded);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mail.SendAsync("test", "test", cancelled.Token));
        Assert.Equal(expectedAttempts, attempts);
    }

    [Fact]
    public async Task Healthcheck_verification_preserves_self_hosted_ping_url()
    {
        using var setup = new Setup();
        var uuid = Guid.NewGuid().ToString();
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Json($"{{\"ping_url\":\"https://pings.example.invalid/{uuid}\"}}"))));
        var service = new HealthcheckService(setup.Config, setup.Secrets, client);
        var verified = await service.VerifyCheckUuidAsync(uuid, "fake-api-key", "https://control.example.invalid");
        Assert.True(verified.IsValid);
        Assert.Equal($"https://pings.example.invalid/{uuid}/fail", service.BuildPingUrl(verified.PingUrl!, "/fail"));
        await service.PingSuccessAsync("invalid", "bad configuration must remain nonfatal", default);
    }

    [Theory]
    [InlineData("M:\\Backups\\Family", "Q:\\", "Q:\\Backups\\Family")]
    [InlineData("M:\\", "Q:\\", "Q:\\")]
    public void Label_matched_drive_rebases_the_actual_repository_path(string original, string mounted, string expected)
    {
        Assert.Equal(expected, ResticUriResolver.RebaseDrivePath(original, mounted));
        Assert.Throws<InvalidOperationException>(() => ResticUriResolver.RebaseDrivePath("relative\\folder", mounted));
    }

    [Fact]
    public void Missing_or_forbidden_share_is_not_classified_as_tolerable_offline()
    {
        Assert.False(BackupOrchestrator.IsTolerableShareFailure(new UnauthorizedAccessException("Access denied")));
        Assert.False(BackupOrchestrator.IsTolerableShareFailure(new DirectoryNotFoundException("wrong share path")));
        Assert.True(BackupOrchestrator.IsTolerableShareFailure(new TimeoutException()));
        Assert.True(BackupOrchestrator.IsTolerableShareFailure(new IOException("offline", unchecked((int)0x800704cf))));
        Assert.False(BackupOrchestrator.IsTolerableShareFailure(new IOException("bad network name", unchecked((int)0x80070043))));
        Assert.True(BackupOrchestrator.IsTolerableShareFailure(new IOException("device unavailable", unchecked((int)0x80070015))));
        Assert.False(BackupOrchestrator.IsTolerableShareFailure(new IOException("media damaged", unchecked((int)0x80070017))));
        Assert.False(BackupOrchestrator.IsTolerableShareFailure(new IOException("storage full", unchecked((int)0x80070070))));
        Assert.False(BackupOrchestrator.IsTolerableShareFailure(new IOException("permission denied", unchecked((int)0x80070005))));
    }

    private static Destination Cloud(string id, string token) => new() { Id = id, Kind = DestinationKind.DropboxAppScoped, OAuthTokenRef = token };
    private static string Token(string access, int days) => $"{{\"access_token\":\"{access}\",\"refresh_token\":\"fake-refresh\",\"expiry\":\"2030-01-{days + 1:00}T00:00:00Z\"}}";
    private static HttpResponseMessage Json(string body, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(body) };
    private static RunRecord Run(Backup backup, BackupRunStatus status) => new() { BackupId = backup.Id, BackupName = backup.Name, Status = status, StartedUtc = DateTime.UtcNow.AddSeconds(-2), EndedUtc = DateTime.UtcNow, Summary = "test run" };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class PlainEncryption : ISecretsEncryption
    {
        public string DisplayName => "isolated-test";
        public byte[] Protect(byte[] plaintext, byte[]? entropy) => plaintext;
        public byte[] Unprotect(byte[] ciphertext, byte[]? entropy) => ciphertext;
    }
    private sealed class Setup : IDisposable
    {
        private readonly TempWorkspace _workspace = new();
        public ConfigStore Config { get; } = new();
        public SecretsStore Secrets { get; } = new();
        public NotificationService Notify { get; }
        public Setup()
        {
            Config.Load(); Notify = new(Config);
            Config.Update(c => { c.Notifications.OnSuccess = new(); c.Notifications.OnFailure = new(); c.Notifications.OnSkipped = new(); });
        }
        public Backup AddBackup()
        {
            var backup = new Backup { Name = "notification-proof", CreatedUtc = DateTime.UtcNow, SkipNotificationGraceDays = 7,
                HealthcheckId = Guid.NewGuid().ToString(), HealthcheckPingUrl = "https://monitor.example.invalid/ping/" + Guid.NewGuid() };
            Config.Update(c => c.Backups.Add(backup)); return backup;
        }
        public string Source() => _workspace.Sub("source");
        public BackupOrchestrator Orchestrator(HttpClient hcClient, HttpClient mailClient) => new(Config, Secrets, new InMemoryBackupEngine(), new LogStore(),
            new HealthcheckService(Config, Secrets, hcClient), new MailService(Config, Secrets, mailClient), Notify, new BackupProgressService());
        public void Dispose() => _workspace.Dispose();
    }
}
