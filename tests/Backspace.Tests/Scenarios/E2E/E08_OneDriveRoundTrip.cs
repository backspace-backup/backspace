using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// Real-user E2E against OneDrive. Same shape as E07 (Dropbox)
/// modulo the Destination kind and OAuth token blob; the engine
/// path goes through rclone's onedrive backend with the
/// family-fallback logic in RcloneConfigBuilder.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E08_OneDriveRoundTrip : BackupE2EBase
{
    public E08_OneDriveRoundTrip(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E08-onedrive-round-trip";

    [AvaloniaFact] public async Task Run_E08() => await Run();

    protected override Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
        => CloudRoundTripFlow.RunAsync(ctx, CloudCredentialsLoader.Provider.OneDrive, Output);
}
