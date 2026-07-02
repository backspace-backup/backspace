using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// Real-user E2E against Dropbox. Skips with a logged reason when
/// DROPBOX_TEST_TOKEN isn't present in secrets.env so the suite
/// stays green on a clean clone without leaking auth state into
/// the working tree.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E07_DropboxRoundTrip : BackupE2EBase
{
    public E07_DropboxRoundTrip(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E07-dropbox-round-trip";

    [AvaloniaFact] public async Task Run_E07() => await Run();

    protected override Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
        => CloudRoundTripFlow.RunAsync(ctx, CloudCredentialsLoader.Provider.Dropbox, Output);
}
