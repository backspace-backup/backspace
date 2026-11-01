using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services.OAuth;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Verifies official registrations and user-provided overrides
/// are selected consistently for each cloud provider family.
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

        // -------- OneDrive uses the official registration without a BYO override --------
        {
            var config = new AppConfig();
            var provider = OAuthProviderFactory.For(DestinationKind.OneDrivePersonal, config);
            Assert.IsType<OneDriveOAuthProvider>(provider);
            Assert.Equal(OAuthAppRegistry.OneDriveClientId, provider!.ClientId);
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

        // -------- Google Drive uses the official registration by default --------
        {
            var config = new AppConfig();
            var provider = OAuthProviderFactory.For(DestinationKind.GoogleDrive, config);
            Assert.IsType<GoogleDriveOAuthProvider>(provider);
            Assert.Equal(OAuthAppRegistry.GoogleDriveClientId, provider!.ClientId);
        }

        ctx.Report.RecordNote(
            "factory wiring verified",
            "Dropbox, OneDrive and Google Drive use official registrations. User-provided registrations " +
            "take precedence; OneDrive Personal/Business share a family entry. No real cloud requests run in this scenario.");
        return Task.CompletedTask;
    }
}
