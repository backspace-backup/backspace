using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ReviewWorkflowRepairTests
{
    [Theory]
    [InlineData("v2.0.0", false, "https://github.com/backspace-backup/backspace/releases/tag/v2.0.0", true)]
    [InlineData("v1.0.0", false, "https://github.com/backspace-backup/backspace/releases/tag/v1.0.0", false)]
    [InlineData("v2.0.0", true, "https://github.com/backspace-backup/backspace/releases/tag/v2.0.0", false)]
    [InlineData("v2.0.0", false, "file:///C:/bad.exe", false)]
    public async Task UpdateCheck_RealResponseWiresReleaseAction_OnlyForNewStableRelease(
        string tag, bool prerelease, string url, bool expected)
    {
        using var handler = new ReleaseHandler($$"""{"tag_name":"{{tag}}","html_url":"{{url}}","prerelease":{{prerelease.ToString().ToLowerInvariant()}}} """);
        using var client = new HttpClient(handler);
        Action? click = null;
        string? opened = null;
        ServiceLocator.Notifier.NotificationObserved = (_, _, _, action, _) => click = action;
        try
        {
            var check = new UpdateChecker(client, target => opened = target, () => new Version(1, 0, 0));
            await check.CheckOnceAsync(CancellationToken.None);
            Assert.Equal(expected, click is not null);
            Assert.Null(opened);
            click?.Invoke();
            Assert.Equal(expected ? url : null, opened);
            Assert.Equal("Backspace-UpdateCheck", handler.UserAgent);
        }
        finally { ServiceLocator.Notifier.NotificationObserved = null; }
    }

    [Fact]
    public void SizedFixture_IsExactAndDeterministic()
    {
        using var work = new TempWorkspace();
        var first = Path.Combine(work.Root, "first");
        var second = Path.Combine(work.Root, "second");
        FixtureFactory.GenerateExactTree(first, 45, 237, 1_000_003);
        FixtureFactory.GenerateExactTree(second, 45, 237, 1_000_003);
        var files = Directory.GetFiles(first, "*", SearchOption.AllDirectories);
        Assert.Equal(237, files.Length);
        Assert.Equal(1_000_003, files.Sum(f => new FileInfo(f).Length));
        foreach (var file in files)
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(second, Path.GetRelativePath(first, file))));
        var report = RestoreValidator.Validate(first, second, new ValidationOptions { VerifyAllBytes = true });
        Assert.Contains("237 files: all match", report.Rows.Single(r => r.Dimension == Dimension.SampledBytes).Summary);
        var corrupted = Path.Combine(second, Path.GetRelativePath(first, files.Last()));
        var bytes = File.ReadAllBytes(corrupted); bytes[0] ^= 0xff; File.WriteAllBytes(corrupted, bytes);
        var negative = RestoreValidator.Validate(first, second, new ValidationOptions { VerifyAllBytes = true });
        Assert.False(negative.Rows.Single(r => r.Dimension == Dimension.SampledBytes).Ok);
        Assert.Throws<ArgumentOutOfRangeException>(() => FixtureFactory.GenerateExactTree(first, 1, 0, 100));
    }

    private sealed class ReleaseHandler(string json) : HttpMessageHandler
    {
        public string? UserAgent { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            UserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
