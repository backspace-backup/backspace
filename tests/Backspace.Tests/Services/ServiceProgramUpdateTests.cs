using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceProgramUpdateTests
{
    [Theory]
    [InlineData("v2.0.0", "../outside")]
    [InlineData("../../releases", "11111111111111111111111111111111")]
    [InlineData("v2.0.0?url=other", "11111111111111111111111111111111")]
    [InlineData("v2.0.0-beta", "11111111111111111111111111111111")]
    public void ElevatedRequestCannotSupplyPathsOrAnAlternateEndpoint(string tag, string id)
        => Assert.Throws<SecurityException>(() => ServiceProgramUpdate.ValidateRequest(tag, id));

    [Fact]
    public async Task ElevatedCheckFetchesTheExactOfficialTagAndDoesNotTrustADifferentRelease()
    {
        string? requested = null;
        using var http = new HttpClient(new Reply(request =>
        {
            requested = request.RequestUri!.AbsoluteUri;
            return Release("v999.0.0");
        }));
        var release = await ServiceProgramUpdate.ReadReleaseAsync(http, "v999.0.0", CancellationToken.None);
        Assert.Equal("https://api.github.com/repos/backspace-backup/backspace/releases/tags/v999.0.0", requested);
        Assert.Equal("v999.0.0", release.Tag);
        await Assert.ThrowsAsync<SecurityException>(() => ServiceProgramUpdate.ReadReleaseAsync(http, "v998.0.0", CancellationToken.None));
    }

    [Fact]
    public async Task MissingOrOversizedOfficialMetadataDoesNotAuthorizeInstallation()
    {
        using var missing = new HttpClient(new Reply(_ => new(HttpStatusCode.NotFound)));
        await Assert.ThrowsAsync<HttpRequestException>(() => ServiceProgramUpdate.ReadReleaseAsync(missing, "v999.0.0", CancellationToken.None));
        using var large = new HttpClient(new Reply(_ => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 2 * 1024 * 1024 + 1)) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => ServiceProgramUpdate.ReadReleaseAsync(large, "v999.0.0", CancellationToken.None));
    }

    [Fact]
    public void ServiceUpdateWorkMustStayUnderTheSamePortableControlFolder()
    {
        using var ws = new TempWorkspace();
        var root = ws.Sub("portable");
        var work = Directory.CreateDirectory(Path.Combine(ServicePortableLayout.Control(root), "updates", Guid.NewGuid().ToString("N"))).FullName;
        var plan = new WindowsUpdatePackage.Plan { Root = root, Work = work, ServiceUpdate = true };
        WindowsUpdatePackage.ValidateLocation(plan);
        plan.ServiceUpdate = false;
        Assert.Throws<InvalidDataException>(() => WindowsUpdatePackage.ValidateLocation(plan));
        plan.ServiceUpdate = true; plan.Root = ws.Sub("another-portable-copy");
        Assert.Throws<InvalidDataException>(() => WindowsUpdatePackage.ValidateLocation(plan));
        plan.Root = root; plan.Work = ws.Sub("ordinary-staging");
        Assert.Throws<InvalidDataException>(() => WindowsUpdatePackage.ValidateLocation(plan));
    }

    [Fact]
    public void DesktopHandoffNamesOnlyAnOwnedPreparedDownloadInsideThisCopiesDataFolder()
    {
        using var ws = new TempWorkspace(); var root = ws.Sub("portable");
        var config = Directory.CreateDirectory(ServicePortableLayout.Config(root)).FullName;
        var work = WindowsUpdatePackage.CreateWorkDirectory(config);
        var plan = new WindowsUpdatePackage.Plan { Root = config, Work = work };
        var request = ServiceProgramUpdateDesktop.ValidateStaging(root, "v999.0.0", plan);
        Assert.Equal(root, request.Root); Assert.Equal(Path.GetFileName(work), request.StagingId);
        Assert.Throws<IOException>(() => ServiceProgramUpdateDesktop.ValidateStaging(ws.Sub("other-copy"), "v999.0.0", plan));
        plan.State = "Installing";
        Assert.Throws<IOException>(() => ServiceProgramUpdateDesktop.ValidateStaging(root, "v999.0.0", plan));
    }

    [Fact]
    public void DesktopDoesNotReportAnotherAttemptOrAnOldSuccessAsTheCurrentResult()
    {
        var request = new ServiceProgramUpdateDesktop.Request(@"C:\Portable", "v999.0.0", Guid.NewGuid().ToString("N"));
        var outcome = new ServiceProgramUpdateDesktop.Outcome(true, "Updated", DateTime.UtcNow, request.StagingId);
        Assert.True(ServiceProgramUpdateDesktop.MatchesOutcome(request, outcome, 0));
        Assert.False(ServiceProgramUpdateDesktop.MatchesOutcome(request, outcome, 4));
        Assert.False(ServiceProgramUpdateDesktop.MatchesOutcome(request, outcome with { StagingId = Guid.NewGuid().ToString("N") }, 0));
        Assert.False(ServiceProgramUpdateDesktop.MatchesOutcome(request, null, 0));
        Assert.True(ServiceProgramUpdateDesktop.MatchesOutcome(request, outcome with { Success = false }, 4));
    }

    [Fact]
    public void DownloadCleanupRemovesOnlyThisAttemptAndPreservesRecoveryAndAnotherDownload()
    {
        using var ws = new TempWorkspace(); var root = ws.Sub("portable");
        var config = Directory.CreateDirectory(ServicePortableLayout.Config(root)).FullName;
        var work = WindowsUpdatePackage.CreateWorkDirectory(config);
        var other = WindowsUpdatePackage.CreateWorkDirectory(config);
        File.WriteAllText(Path.Combine(work, "download.zip"), "public download");
        File.WriteAllText(Path.Combine(other, "download.zip"), "other download");
        var recovery = Directory.CreateDirectory(Path.Combine(ServicePortableLayout.Control(root), "updates", Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(recovery, "recovery-runner.exe"), "retained");
        var request = new ServiceProgramUpdateDesktop.Request(root, "v999.0.0", Path.GetFileName(work));
        ServiceProgramUpdateDesktop.CleanupDownload(request);
        Assert.False(Directory.Exists(work));
        Assert.Equal("other download", File.ReadAllText(Path.Combine(other, "download.zip")));
        Assert.Equal("retained", File.ReadAllText(Path.Combine(recovery, "recovery-runner.exe")));
        Assert.Throws<SecurityException>(() => ServiceProgramUpdateDesktop.CleanupDownload(request with { StagingId = "../outside" }));
    }

    [Fact]
    public void CompletedServiceUpdateKeepsLatestRecoveryAndRemovesOnlyMatchingCompletedWork()
    {
        using var ws = new TempWorkspace(); var root = ws.Sub("portable");
        var parent = Directory.CreateDirectory(Path.Combine(ServicePortableLayout.Control(root), "updates")).FullName;
        WindowsUpdatePackage.Plan Create(string state)
        {
            var work = Directory.CreateDirectory(Path.Combine(parent, Guid.NewGuid().ToString("N"))).FullName;
            var plan = new WindowsUpdatePackage.Plan { Root = root, Work = work, ServiceUpdate = true, State = state };
            WindowsUpdatePackage.SavePlan(plan);
            File.WriteAllText(Path.Combine(work, "recovery-runner.exe"), "retained recovery");
            return plan;
        }
        var old = Create("Installed"); var rolledBack = Create("RolledBack");
        var pending = Create("Installing"); var unknown = Create("Prepared");
        var wrongRoot = Create("Installed"); wrongRoot.Root = ws.Sub("other-copy"); WindowsUpdatePackage.SavePlan(wrongRoot);
        var wrongWork = Create("Installed"); var wrongWorkDirectory = wrongWork.Work;
        wrongWork.Work = old.Work;
        File.WriteAllText(Path.Combine(wrongWorkDirectory, "plan.json"), JsonSerializer.Serialize(wrongWork));
        var current = Create("Installed");
        Directory.CreateDirectory(Path.Combine(current.Work, "previous"));
        File.WriteAllText(Path.Combine(current.Work, "previous", "Backspace.exe"), "previous application");
        File.WriteAllText(Path.Combine(current.Work, "download.zip"), "redundant public archive");
        var errors = new System.Collections.Generic.List<Exception>();
        WindowsUpdateRuntime.PruneOlderCompletedUpdates(current, errors.Add);
        // Invalid saved locations are retained and reported, not treated as
        // safely removable merely because their folder name looks familiar.
        Assert.Equal(2, errors.Count);
        Assert.All(errors, error => Assert.True(error is IOException or InvalidDataException));
        Assert.False(Directory.Exists(old.Work)); Assert.False(Directory.Exists(rolledBack.Work));
        Assert.True(Directory.Exists(pending.Work)); Assert.True(Directory.Exists(unknown.Work));
        Assert.True(Directory.Exists(wrongRoot.Work)); Assert.True(Directory.Exists(wrongWorkDirectory));
        Assert.Equal("previous application", File.ReadAllText(Path.Combine(current.Work, "previous", "Backspace.exe")));
        Assert.Equal("retained recovery", File.ReadAllText(Path.Combine(current.Work, "recovery-runner.exe")));
        Assert.False(File.Exists(Path.Combine(current.Work, "download.zip")));
    }

    private static HttpResponseMessage Release(string tag) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            tag_name = tag, html_url = "https://github.com/backspace-backup/backspace/releases/tag/" + tag,
            assets = new[] { new { name = WindowsUpdatePackage.AssetName, browser_download_url = "https://github.com/backspace-backup/backspace/releases/download/" + tag + "/" + WindowsUpdatePackage.AssetName, digest = "sha256:" + new string('a', 64), size = 100 } },
        })),
    };
    private sealed class Reply(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(respond(request));
    }
}
