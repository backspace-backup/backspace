using System;
using System.Net.Http;
using System.Threading.Tasks;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace Backspace.Tests.Services;

/// <summary>
/// Smoke: the bundled Backrest binary serves its built-in web UI
/// (the HTML SPA Backrest ships) on the same port as the Connect-Go
/// RPC. <see cref="SnapshotBrowserLauncher"/> points the user's
/// default browser at that URL when they click "Browse" on a backup
/// card — if anything ever quietly disables the SPA serving
/// (e.g. an upstream flag like <c>--no-webui</c> appears),
/// this test catches it before the button silently breaks.
///
/// Test shape: start a real sidecar, hit the root URL, assert the
/// response looks like HTML (200 + body containing <html or a
/// recognisable Backrest token). Skip cleanly if the binaries
/// aren't on disk.
/// </summary>
public class BackrestWebUiReachableTests
{
    private readonly ITestOutputHelper _out;

    public BackrestWebUiReachableTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task WebUiResponds_On_SidecarPort()
    {
        if (!RealEngineFixture.BinariesPresent)
        {
            _out.WriteLine(RealEngineFixture.MissingBinariesReason());
            return;
        }

        await using var fixture = await RealEngineFixture.TryCreateAsync(_out);
        Assert.NotNull(fixture);
        var port = fixture!.Sidecar.Port;

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5),
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
        };
        // Backrest may or may not require a bearer token for the
        // root HTML; the SPA itself is typically served unauth so
        // browsers can fetch the static bundle, then the JS injects
        // the token on every RPC call.
        var resp = await http.GetAsync("");
        Assert.True(resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.Unauthorized,
            $"Web UI root returned {(int)resp.StatusCode} ({resp.StatusCode}) — Backrest may have changed its bind shape.");

        if (resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            _out.WriteLine($"Web UI root: {body.Length} bytes, content-type='{resp.Content.Headers.ContentType}'.");
            // Some HTML signature has to be present so we know the
            // sidecar is serving the SPA and not a JSON-only error.
            Assert.True(body.Contains("<html", StringComparison.OrdinalIgnoreCase)
                        || body.Contains("backrest", StringComparison.OrdinalIgnoreCase),
                $"Web UI root body didn't look like HTML — first 200 chars: " +
                $"{body[..Math.Min(200, body.Length)]}");
        }
    }
}
