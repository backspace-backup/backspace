using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.OAuth;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

[Collection(ScenarioCollection.Name)]
public sealed class S50_ExactFailureNotification : ScenarioBase
{
    protected override string ScenarioName => "S50-exact-failure-notification";
    [AvaloniaFact] public async Task Run_S50() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var backup = new Backup { Name = "Family archive — notification proof" };
        ServiceLocator.Config.Update(c =>
        {
            c.Backups.Add(backup);
            c.Notifications.OnFailure = new NotificationChannels { Toast = true, Sound = false, Email = false };
        });
        var failedLog = Path.Combine(ctx.RootDir, "failed-run.log");
        File.WriteAllText(failedLog, "FAILED RUN — earlier backup\nAccess denied to the archive. This is the run described by the alert.\n");
        var failure = new RunRecord { BackupId = backup.Id, BackupName = backup.Name, Status = BackupRunStatus.Failed,
            StartedUtc = DateTime.UtcNow.AddHours(-2), EndedUtc = DateTime.UtcNow.AddHours(-2).AddMinutes(1),
            Summary = "Earlier backup could not reach archive", LogPath = failedLog, Errors = { "Access denied to the archive" } };
        var success = new RunRecord { BackupId = backup.Id, BackupName = backup.Name, Status = BackupRunStatus.Success,
            StartedUtc = DateTime.UtcNow.AddHours(-1), EndedUtc = DateTime.UtcNow.AddHours(-1).AddMinutes(1), Summary = "Newer backup completed" };
        ServiceLocator.Logs.AppendRun(failure); ServiceLocator.Logs.AppendRun(success);
        using var vm = new MainWindowViewModel();
        var window = ctx.Driver.Show(new MainWindow { DataContext = vm });
        var clicked = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notify = new NotificationService(ServiceLocator.Config)
        {
            NotificationObserved = (_, _, severity, click, _) => { if (severity == NotificationSeverity.Failure && click is not null) clicked.TrySetResult(click); }
        };
        var orchestrator = new BackupOrchestrator(ServiceLocator.Config, ServiceLocator.Secrets, ServiceLocator.Engine,
            ServiceLocator.Logs, ServiceLocator.Healthcheck, ServiceLocator.Mail, notify, ServiceLocator.Progress);
        await orchestrator.SendAlertsAsync(backup, failure);
        var callback = await clicked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        callback(); await ctx.Driver.PumpAsync(150);
        Assert.Equal(NavItem.Logs, vm.SelectedNav);
        Assert.Equal(failure.Id, vm.Logs.SelectedRun?.Id);
        Assert.Equal(2, vm.Logs.History.Count);
        Assert.Contains("earlier backup", vm.Logs.DisplayText);
        ctx.Snapshot(window, "alert-selects-earlier-failed-run", "The actual failure-alert callback opens Activity & logs and selects the earlier failure, although a newer successful run exists.");
        vm.Logs.SelectRunById(backup.Id, "no-longer-in-history");
        Assert.Null(vm.Logs.SelectedRun); // Never substitute a different run.
        ctx.Report.RecordNote("scope of proof", "Invoked the action emitted by the real SendAlertsAsync notification path. Navigation and rendered log selection are exercised; the operating-system popup and a physical toast click are not captured in this headless test.");
        Backspace.App.QuitRequested = true; window.Close(); Backspace.App.QuitRequested = false;
    }
}

[Collection(ScenarioCollection.Name)]
public sealed class S51_OneDriveEditorLoopback : ScenarioBase
{
    protected override string ScenarioName => "S51-onedrive-editor-loopback";
    [AvaloniaFact] public async Task Run_S51() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        ServiceLocator.Config.Update(c =>
        {
            c.RcloneOAuthApps.RemoveAll(a => a.Provider is DestinationKind.OneDrivePersonal or DestinationKind.OneDriveBusiness);
            c.RcloneOAuthApps.Add(new RcloneOAuthApp { Provider = DestinationKind.OneDrivePersonal, ClientId = "fixture-client-id" });
        });
        var requestCount = 0;
        using var fakeHttp = new HttpClient(new FixtureHandler(async (request, ct) =>
        {
            Interlocked.Increment(ref requestCount);
            if (request.RequestUri!.Host == "graph.microsoft.com")
            {
                Assert.Equal("fixture-access", request.Headers.Authorization?.Parameter);
                return Json("{\"id\":\"fixture-drive-id\",\"driveType\":\"personal\"}");
            }
            var form = await request.Content!.ReadAsStringAsync(ct);
            Assert.Contains("code_verifier=", form); Assert.Contains("code=fixture-code", form);
            return Json("{\"access_token\":\"fixture-access\",\"refresh_token\":\"fixture-refresh\",\"token_type\":\"Bearer\",\"expires_in\":3600}");
        }));
        using var loopback = new HttpClient();
        Task<HttpResponseMessage>? callbackRequest = null;
        var denied = false;
        var coordinator = new OAuthFlowCoordinator(fakeHttp, url =>
        {
            var query = new Uri(url).Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2))
                .ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1]));
            Assert.Equal("S256", query["code_challenge_method"]);
            Assert.False(string.IsNullOrWhiteSpace(query["code_challenge"]));
            var response = denied ? "error=access_denied" : "code=fixture-code";
            callbackRequest = loopback.GetAsync(query["redirect_uri"] + "?" + response + "&state=" + Uri.EscapeDataString(query["state"]));
        });
        using var vm = new DestinationEditorViewModel(new Destination { Name = "OneDrive fixture", Kind = DestinationKind.OneDrivePersonal }, true)
        {
            OAuthFlowOverride = coordinator, OneDriveDiscoveryOverride = new OneDriveDiscovery(fakeHttp)
        };
        var window = ctx.Driver.Show(new DestinationEditorWindow { DataContext = vm, Width = 900, Height = 850 });
        Assert.True(ctx.Driver.ClickButton(window, "Connect to OneDrive"));
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while ((!vm.AuthOk || vm.AuthBusy) && DateTime.UtcNow < deadline) await ctx.Driver.PumpAsync(50);
        Assert.True(vm.AuthOk, vm.AuthStatus); Assert.False(vm.AuthBusy);
        Assert.Equal("fixture-drive-id", vm.Working.OneDriveDriveId); Assert.Equal("personal", vm.Working.OneDriveDriveType);
        Assert.Equal(2, requestCount);
        if (callbackRequest is not null) using (await callbackRequest) { }
        ctx.Snapshot(window, "onedrive-connected-through-editor", "The editor's Connect button completed a real local callback, PKCE token exchange and drive discovery using simulated provider responses. Connected status is visible.");
        vm.OAuthTokenInput = "";
        Assert.Null(vm.Working.OneDriveDriveId); Assert.Null(vm.Working.OneDriveDriveType);
        denied = true;
        Assert.True(ctx.Driver.ClickButton(window, "Connect to OneDrive"));
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.AuthBusy && DateTime.UtcNow < deadline) await ctx.Driver.PumpAsync(50);
        Assert.False(vm.AuthOk); Assert.False(vm.AuthBusy); Assert.Contains("refused", vm.AuthStatus);
        Assert.Equal(2, requestCount); // Denied consent never exchanges a token.
        if (callbackRequest is not null) using (await callbackRequest) { }
        ctx.Snapshot(window, "onedrive-denied-consent-is-visible", "Denying consent clears the busy state and displays a sign-in refusal; it never claims the account connected.");
        ctx.Report.RecordNote("scope of proof", "The actual DestinationEditor button command, local callback listener, state/PKCE flow, token exchange, automatic validation and drive discovery run together. External provider HTTP responses are fake; no real Microsoft account or registry client id is validated. The browser-launch seam sends the callback locally instead of launching a real consent page.");
        window.Close();
    }
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
