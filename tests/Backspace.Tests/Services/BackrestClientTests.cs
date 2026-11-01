using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Verifies that <see cref="BackrestClient"/> shapes Connect-Go RPC
/// calls the way Backrest's HTTP handlers expect: POST with
/// application/json body to <c>/v1.Backrest/{Method}</c>, errors
/// surfaced via <see cref="BackrestRpcException"/>, bearer token
/// applied uniformly.
/// </summary>
public class BackrestClientTests
{
    [Fact]
    public async Task CallAsync_PostsJsonBodyToCorrectEndpoint()
    {
        var captured = new RecordedRequest();
        var handler = new ScriptedHandler(req =>
        {
            captured.Method = req.Method;
            captured.Uri = req.RequestUri;
            captured.Body = req.Content!.ReadAsStringAsync().Result;
            captured.ContentType = req.Content!.Headers.ContentType?.MediaType;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "exists": true, "error": "" }""", Encoding.UTF8, "application/json"),
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9999/") };
        var client = new BackrestClient(http);

        var request = new CheckRepoExistsRequest
        {
            Repo = new BackrestRepo { Id = "test-repo", Uri = "/tmp/test", Password = "p" }
        };

        var resp = await client.CallAsync<CheckRepoExistsRequest, CheckRepoExistsResponse>(
            "/v1.Backrest/CheckRepoExists", request, CancellationToken.None);

        Assert.True(resp.Exists);
        Assert.Equal(HttpMethod.Post, captured.Method);
        Assert.Equal("http://127.0.0.1:9999/v1.Backrest/CheckRepoExists", captured.Uri!.ToString());
        Assert.Equal("application/json", captured.ContentType);

        // Body must include the proto field names (with underscores) and
        // the nested repo object — JsonPropertyName drives this, not the
        // default naming policy.
        using var doc = JsonDocument.Parse(captured.Body!);
        var repo = doc.RootElement.GetProperty("repo");
        Assert.Equal("test-repo", repo.GetProperty("id").GetString());
        Assert.Equal("/tmp/test", repo.GetProperty("uri").GetString());
        Assert.Equal("p", repo.GetProperty("password").GetString());
    }

    [Fact]
    public async Task CallAsync_SurfacesConnectErrorFromBody()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{ "code": "not_found", "message": "no such plan" }""",
                Encoding.UTF8, "application/json"),
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9999/") };
        var client = new BackrestClient(http);

        var ex = await Assert.ThrowsAsync<BackrestRpcException>(() =>
            client.CallVoidAsync("/v1.Backrest/Backup",
                new BackupRequest { Value = "missing" }, CancellationToken.None));

        Assert.Equal("/v1.Backrest/Backup", ex.Endpoint);
        Assert.Equal(404, ex.HttpStatus);
        Assert.Equal("not_found", ex.Code);
        Assert.Contains("no such plan", ex.Message);
    }

    [Fact]
    public async Task CallAsync_FallsBackToHttpStatusWhenBodyHasNoCode()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("oops, not json", Encoding.UTF8, "text/plain"),
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9999/") };
        var client = new BackrestClient(http);

        var ex = await Assert.ThrowsAsync<BackrestRpcException>(() =>
            client.CallVoidAsync("/v1.Backrest/Backup",
                new BackupRequest { Value = "any" }, CancellationToken.None));

        Assert.Equal(500, ex.HttpStatus);
        Assert.Contains("oops", ex.Message);
    }

    [Fact]
    public async Task SetBearerToken_AppliesAuthorizationHeaderOnSubsequentCalls()
    {
        string? capturedAuth = null;
        var handler = new ScriptedHandler(req =>
        {
            capturedAuth = req.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9999/") };
        var client = new BackrestClient(http);
        client.SetBearerToken("a-jwt-value");

        await client.CallVoidAsync("/v1.Backrest/Backup",
            new BackupRequest { Value = "x" }, CancellationToken.None);

        Assert.Equal("Bearer a-jwt-value", capturedAuth);
    }

    [Fact]
    public async Task CallAsync_HandlesEmptyResponseBody()
    {
        // Backrest's google.protobuf.Empty responses come back as either
        // "" or "{}". The client must tolerate both without throwing.
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("", Encoding.UTF8, "application/json"),
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9999/") };
        var client = new BackrestClient(http);

        // CallVoidAsync ignores body content; CallAsync<,> should
        // still produce a default-constructed response.
        var result = await client.CallAsync<BackupRequest, BackrestConfig>(
            "/v1.Backrest/GetConfig", new BackupRequest(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(0, result.Modno);
    }

    // ---- helpers ----

    private sealed class RecordedRequest
    {
        public HttpMethod? Method;
        public Uri? Uri;
        public string? Body;
        public string? ContentType;
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_respond(request));
    }
}
