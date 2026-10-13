using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class BackrestAuthenticationTests
{
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
        finally { Directory.Delete(root, true); }
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
        finally { Directory.Delete(root, true); }
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
