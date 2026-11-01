using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using System.Text;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class BackrestAuthenticationTests
{
    [Fact]
    public void Missing_engine_configuration_with_backup_does_not_seed_an_empty_replacement()
    {
        using var workspace = new Backspace.Tests.TestHelpers.TempWorkspace();
        var path = Path.Combine(workspace.Root, "engine.json");
        BackrestSidecar.WriteSeedConfig(path);
        File.Move(path, path + ".bak");
        var retained = File.ReadAllBytes(path + ".bak");
        Assert.Throws<IOException>(() => BackrestSidecar.WriteSeedConfig(path));
        Assert.False(File.Exists(path));
        Assert.Equal(retained, File.ReadAllBytes(path + ".bak"));
    }

    [Fact]
    public async Task Real_Backrest_recovers_when_its_own_startup_config_write_is_locked_for_ten_seconds()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!File.Exists(AppPaths.BackrestBinary) || !File.Exists(AppPaths.ResticBinary)) Assert.Skip("Bundled engines are required.");
        using var workspace = new Backspace.Tests.TestHelpers.TempWorkspace("backrest-startup-lock");
        await using var sidecar = new BackrestSidecar(AppPaths.BackrestBinary, AppPaths.ResticBinary, workspace.Root);
        var path = Path.Combine(workspace.Root, "config.json");
        BackrestSidecar.WriteSeedConfig(path); sidecar.PrepareAuthentication();
        var previous = File.ReadAllBytes(path);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(40));
        var start = sidecar.StartAsync(stop.Token);
        try
        {
            await Task.Delay(10_500, stop.Token);
            // The notice appears five seconds after startup first reaches the
            // locked file; on a busy machine startup can take longer to get there.
            for (var waited = 0; waited < 20_000 && !FileAccessRetry.Snapshot().Any(n => n.Path == path); waited += 250)
                await Task.Delay(250, stop.Token);
            Assert.False(start.IsCompleted); Assert.False(sidecar.IsRunning); Assert.Null(sidecar.Client);
            Assert.Equal(previous, File.ReadAllBytes(path));
            Assert.Single(FileAccessRetry.Snapshot(), n => n.Path == path);
            held.Dispose();
            await start.WaitAsync(TimeSpan.FromSeconds(20), stop.Token);
            Assert.True(sidecar.IsRunning); Assert.NotNull(sidecar.Client);
            Assert.DoesNotContain(FileAccessRetry.Snapshot(), n => n.Path == path);
        }
        finally { held.Dispose(); stop.Cancel(); try { await start; } catch (OperationCanceledException) { } }
    }

    [Theory]
    [InlineData("write config file: cannot replace config: Access is denied.", 0, true)]
    [InlineData("write config file: cannot replace config: Access is denied.", -60, false)]
    [InlineData("write config file: cannot replace config: Access is denied.", 60, false)]
    [InlineData("invalid config schema", 0, false)]
    [InlineData("write config file: disk is full", 0, false)]
    public void Only_current_startup_configuration_access_errors_allow_an_engine_restart(string error, int ageSeconds, bool expected)
    {
        using var workspace = new Backspace.Tests.TestHelpers.TempWorkspace();
        var path = Path.Combine(workspace.Root, "engine.log");
        var started = DateTime.UtcNow.AddSeconds(-1);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { level = "fatal", msg = "error loading config", error,
            ts = new DateTimeOffset(DateTime.UtcNow.AddSeconds(ageSeconds)).ToUnixTimeMilliseconds() / 1000d }));
        Assert.Equal(expected, BackrestSidecar.StartupLogShowsConfigLock(path, started));
    }

    [Fact]
    public async Task Cancelled_password_repair_does_not_make_the_next_start_trust_mismatched_credentials()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new Backspace.Tests.TestHelpers.TempWorkspace("auth-repair-lock");
        await using var sidecar = new BackrestSidecar("unused", "unused", workspace.Root);
        var path = Path.Combine(workspace.Root, "config.json");
        BackrestSidecar.WriteSeedConfig(path); sidecar.PrepareAuthentication();
        var protectedConfig = File.ReadAllBytes(path);
        var authFile = Path.Combine(workspace.Root, "sidecar-auth.txt");
        // Simulate a password file that no longer matches the protected config.
        File.WriteAllText(authFile, "mismatched-disposable-test-password");
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(700)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() => sidecar.PrepareAuthentication(stop.Token)));
            Assert.Equal(protectedConfig, File.ReadAllBytes(path));
            Assert.Null(sidecar.Client); Assert.False(sidecar.IsRunning);
        }
        sidecar.PrepareAuthentication();
        var repaired = JsonNode.Parse(File.ReadAllText(path))!;
        var hash = Encoding.UTF8.GetString(Convert.FromBase64String(repaired["auth"]!["users"]![0]!["passwordBcrypt"]!.GetValue<string>()));
        Assert.True(BCrypt.Net.BCrypt.Verify(File.ReadAllText(authFile).Trim(), hash));
        Assert.False(BCrypt.Net.BCrypt.Verify("mismatched-disposable-test-password", hash));
        Assert.False(repaired["auth"]!["disabled"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authentication_setup_waits_for_unlock_or_cancellation_and_preserves_the_previous_file(bool persistent)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "backspace-auth-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var sidecar = new BackrestSidecar("unused", "unused", root);
            var path = Path.Combine(root, "config.json");
            BackrestSidecar.WriteSeedConfig(path);
            var previous = File.ReadAllBytes(path);
            using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var stop = new CancellationTokenSource();
            var prepare = Task.Run(() => sidecar.PrepareAuthentication(stop.Token));
            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.False(prepare.IsCompleted, "Authentication setup cannot finish while its configuration replacement is blocked.");
            Assert.Equal(previous, File.ReadAllBytes(path));
            if (persistent)
            {
                stop.Cancel();
                var error = await Record.ExceptionAsync(() => prepare.WaitAsync(TimeSpan.FromSeconds(6)));
                Assert.True(error is OperationCanceledException, error?.ToString());
                Assert.Equal(previous, File.ReadAllBytes(path));
                Assert.Null(sidecar.Client);
                Assert.False(sidecar.IsRunning);
            }
            else
            {
                held.Dispose();
                await prepare.WaitAsync(TimeSpan.FromSeconds(6));
                Assert.NotEqual(previous, File.ReadAllBytes(path));
                Assert.False(File.Exists(path + ".tmp"));
            }
        }
        finally { ResticBackrestEngine.DeleteRestoreStaging(root); }
    }

    [Theory]
    [InlineData("Login")]
    [InlineData("empty-token")]
    public async Task Failed_authentication_cannot_return_a_usable_client(string failure)
    {
        var root = Path.Combine(Path.GetTempPath(), "backspace-auth-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var sidecar = new BackrestSidecar("unused", "unused", root);
            BackrestSidecar.WriteSeedConfig(Path.Combine(root, "config.json"));
            sidecar.PrepareAuthentication();
            using var handler = new AuthenticationHandler(failure);
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            var client = new BackrestClient(http);
            await Assert.ThrowsAsync<IOException>(() => sidecar.EnsureAuthAsync(client, CancellationToken.None));
            Assert.Null(http.DefaultRequestHeaders.Authorization);
            Assert.Null(sidecar.Client);
            Assert.False(sidecar.IsRunning);
            if (failure is not "empty-token") Assert.Equal(failure, handler.Calls[^1]);
        }
        finally { ResticBackrestEngine.DeleteRestoreStaging(root); }
    }

    [Fact]
    public async Task Successful_authentication_saves_the_password_and_sets_the_bearer()
    {
        var root = Path.Combine(Path.GetTempPath(), "backspace-auth-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var sidecar = new BackrestSidecar("unused", "unused", root);
            BackrestSidecar.WriteSeedConfig(Path.Combine(root, "config.json"));
            sidecar.PrepareAuthentication();
            using var http = new HttpClient(new AuthenticationHandler(null)) { BaseAddress = new Uri("http://localhost/") };
            await sidecar.EnsureAuthAsync(new BackrestClient(http), CancellationToken.None);
            Assert.Equal("Bearer", http.DefaultRequestHeaders.Authorization?.Scheme);
            Assert.Equal("test-token", http.DefaultRequestHeaders.Authorization?.Parameter);
            Assert.True(File.Exists(Path.Combine(root, "sidecar-auth.txt")));
        }
        finally { ResticBackrestEngine.DeleteRestoreStaging(root); }
    }

    private sealed class AuthenticationHandler(string? failure) : HttpMessageHandler
    {
        public List<string> Calls { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var method = request.RequestUri!.Segments[^1];
            Calls.Add(method);
            if (method == failure)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) {
                    Content = new StringContent("{\"code\":\"internal\",\"message\":\"deliberate test failure\"}") });
            var json = method switch {
                "Login" => failure == "empty-token" ? "{\"token\":\"\"}" : "{\"token\":\"test-token\"}",
                _ => throw new InvalidOperationException("Startup must not use unprotected setup RPCs.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
