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

    [Theory]
    [InlineData(7, 604800)]
    [InlineData(365, 31536000)]
    [InlineData(400, 31536000)]
    public async Task Healthchecks_upsert_sends_IANA_zone_without_changing_integration_assignments(int days, int seconds)
    {
        using var setup = new Setup(); var backup = setup.AddBackup();
        setup.Secrets.Set("hc-key", "synthetic-key");
        setup.Config.Update(c => c.Monitoring.HealthchecksApiKeyRef = "hc-key");
        backup.Schedule.Frequency = ScheduleFrequency.Daily;
        backup.SkipNotificationGraceDays = days;
        backup.Schedule.TimeOfDay = TimeSpan.FromHours(3) + TimeSpan.FromMinutes(17);
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("17 3 * * *", body.RootElement.GetProperty("schedule").GetString());
            Assert.Equal(seconds, body.RootElement.GetProperty("grace").GetInt32());
            Assert.Equal(HealthcheckService.ScheduleTimeZoneId(TimeZoneInfo.Local), body.RootElement.GetProperty("tz").GetString());
            Assert.False(body.RootElement.TryGetProperty("channels", out _));
            Assert.False(body.RootElement.TryGetProperty("unique_by", out _));
            Assert.Equal(new[] { "name", "tags" }, body.RootElement.GetProperty("unique").EnumerateArray().Select(x => x.GetString()));
            return Json("{\"ping_url\":\"https://hc-ping.com/11111111-2222-3333-4444-555555555555\"}");
        }));
        var result = await new HealthcheckService(setup.Config, setup.Secrets, client).UpsertCheckAsync(backup, default);
        Assert.NotNull(result); Assert.Equal(result.PingUrl, backup.HealthcheckPingUrl);
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
        var secrets = new SecretsStore(new PlainEncryption());
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
        var secrets = new SecretsStore(new PlainEncryption());
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
        public SecretsStore Secrets { get; } = new(new PlainEncryption());
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
