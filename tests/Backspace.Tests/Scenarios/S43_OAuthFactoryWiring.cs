using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services.OAuth;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Regression for ROADMAP P3 "OneDrive + Google Drive OAuth client_ids"
/// at the factory wiring level: the providers are built, the BYO
/// override path is honoured, and the empty-registry sentinel
/// surfaces null (so the UI shows "OAuth not yet wired" instead of
/// dispatching a 400 at authorize-time).
///
/// Unit tests (OAuthProviderTests) verify each provider's properties
/// in isolation. This scenario verifies the FACTORY chooses the
/// right provider given the config, and that an unset client_id at
/// both registry + BYO levels returns null.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S43_OAuthFactoryWiring : ScenarioBase
{
    protected override string ScenarioName => "S43-oauth-factory-wiring";

    [AvaloniaFact] public async Task Run_S43() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        // -------- Dropbox: baked-in client_id always wins --------
        {
            var config = new AppConfig();
            var provider = OAuthProviderFactory.For(DestinationKind.DropboxAppScoped, config);
            Assert.NotNull(provider);
            Assert.IsType<DropboxOAuthProvider>(provider);
            Assert.Equal(OAuthAppRegistry.DropboxClientId, provider!.ClientId);
        }

        // -------- OneDrive Personal: BYO override picks up custom client_id --------
        {
            var config = new AppConfig();
            config.RcloneOAuthApps.Add(new RcloneOAuthApp
            {
                Provider = DestinationKind.OneDrivePersonal,
                ClientId = "test-onedrive-byo-12345",
            });
            var provider = OAuthProviderFactory.For(DestinationKind.OneDrivePersonal, config);
            Assert.NotNull(provider);
            Assert.IsType<OneDriveOAuthProvider>(provider);
            Assert.Equal("test-onedrive-byo-12345", provider!.ClientId);
        }

        // -------- OneDrive Business shares the BYO family entry --------
        {
            var config = new AppConfig();
            config.RcloneOAuthApps.Add(new RcloneOAuthApp
            {
                Provider = DestinationKind.OneDrivePersonal,  // family-matched
                ClientId = "test-onedrive-byo-business",
            });
            var provider = OAuthProviderFactory.For(DestinationKind.OneDriveBusiness, config);
            Assert.NotNull(provider);
            Assert.Equal(DestinationKind.OneDriveBusiness, provider!.Kind);
            Assert.Equal("test-onedrive-byo-business", provider.ClientId);
        }

        // -------- OneDrive with no BYO and empty registry → null --------
        {
            var config = new AppConfig();
            var provider = OAuthProviderFactory.For(DestinationKind.OneDrivePersonal, config);
            Assert.Null(provider);  // empty registry sentinel
        }

        // -------- Google Drive: BYO --------
        {
            var config = new AppConfig();
            config.RcloneOAuthApps.Add(new RcloneOAuthApp
            {
                Provider = DestinationKind.GoogleDrive,
                ClientId = "byo-google.apps.googleusercontent.com",
            });
            var provider = OAuthProviderFactory.For(DestinationKind.GoogleDrive, config);
            Assert.NotNull(provider);
            Assert.IsType<GoogleDriveOAuthProvider>(provider);
            Assert.Equal("byo-google.apps.googleusercontent.com", provider!.ClientId);
        }

        // -------- Google Drive with empty everything → null --------
        {
            var config = new AppConfig();
            var provider = OAuthProviderFactory.For(DestinationKind.GoogleDrive, config);
            Assert.Null(provider);
        }

        ctx.Report.RecordNote(
            "factory wiring verified",
            "Dropbox baked-in; OneDrive Personal/Business share BYO family entry; " +
            "Google Drive honours BYO; empty registry+BYO returns null for the " +
            "'OAuth not yet wired' UI path.");
        return Task.CompletedTask;
    }
}
