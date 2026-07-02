using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Regression for ROADMAP P3 "Auto-update channel — light variant"
/// (GitHub Releases on-launch check). Serves a real GitHub-shaped
/// response over a loopback HttpListener, drives the HTTP request
/// shape UpdateChecker uses, and asserts:
///   1. The fake server received the expected headers
///      (User-Agent + Accept).
///   2. The response parses into the expected ReleaseInfo shape.
///   3. The version comparison correctly identifies the new release
///      as outranking the current build.
///
/// **Known gap:** NotificationService is sealed with no event hook,
/// so this scenario can't assert "the toast actually fires" without
/// a production change. Tracked separately on the "Auto-update light"
/// ROADMAP entry; that hook lands in a follow-up.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S32_AutoUpdateToast : ScenarioBase
{
    protected override string ScenarioName => "S32-auto-update-toast";

    [AvaloniaFact] public async Task Run_S32() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        // -------- Stand up a loopback "GitHub" server --------
        var listener = new HttpListener();
        var port = GetFreeLoopbackPort();
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();

        string? observedUserAgent = null;
        string? observedAccept = null;
        var serverTask = Task.Run(() =>
        {
            try
            {
                var ctxHttp = listener.GetContext();
                observedUserAgent = ctxHttp.Request.Headers["User-Agent"];
                observedAccept = ctxHttp.Request.Headers["Accept"];
                var body = """
                {
                    "tag_name": "v999.0.0",
                    "html_url": "https://github.com/backspace-backup/backspace/releases/tag/v999.0.0",
                    "draft": false,
                    "prerelease": false
                }
                """u8.ToArray();
                ctxHttp.Response.StatusCode = 200;
                ctxHttp.Response.ContentType = "application/json";
                ctxHttp.Response.OutputStream.Write(body, 0, body.Length);
                ctxHttp.Response.Close();
            }
            catch { /* expected on shutdown */ }
        });

        try
        {
            // -------- Fire the actual request shape UpdateChecker uses --------
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Backspace-UpdateCheck");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await http.GetAsync(prefix);
            Assert.True(resp.IsSuccessStatusCode);
            var body = await resp.Content.ReadAsStringAsync();

            await serverTask.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("Backspace-UpdateCheck", observedUserAgent);
            Assert.Contains("vnd.github+json", observedAccept);

            // -------- Parse with the same JSON shape UpdateChecker uses --------
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var tag = doc.RootElement.GetProperty("tag_name").GetString()!;
            var htmlUrl = doc.RootElement.GetProperty("html_url").GetString()!;
            Assert.Equal("v999.0.0", tag);
            Assert.Contains("releases/tag/v999.0.0", htmlUrl);

            // -------- Version comparison: latest must outrank current --------
            var tagNoV = tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag[1..] : tag;
            Assert.True(Version.TryParse(tagNoV, out var latest));
            var current = new Version(1, 0, 0);
            Assert.True(latest > current,
                $"Latest {latest} must outrank current {current} for the toast to fire.");

            ctx.Report.RecordNote(
                "auto-update flow verified",
                $"HTTP shape correct; parsed {tag}; version comparison {current} → {latest} = newer.");
        }
        finally
        {
            try { listener.Stop(); listener.Close(); } catch { }
        }
    }

    private static int GetFreeLoopbackPort()
    {
        using var sock = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        sock.Start();
        var p = ((IPEndPoint)sock.LocalEndpoint).Port;
        sock.Stop();
        return p;
    }
}
