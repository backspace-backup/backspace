using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// Real-user E2E against Google Drive. Same shape as E07/E08; the
/// engine path goes through rclone's drive backend.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E09_GoogleDriveRoundTrip : BackupE2EBase
{
    public E09_GoogleDriveRoundTrip(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E09-googledrive-round-trip";

    [AvaloniaFact] public async Task Run_E09() => await Run();

    protected override Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
        => CloudRoundTripFlow.RunAsync(ctx, CloudCredentialsLoader.Provider.GoogleDrive, Output);
}
