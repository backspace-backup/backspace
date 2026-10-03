using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>Exercises the real ownership-record recovery path with fake process
/// inspection and termination. No machine processes are enumerated or killed.
/// The report identifies the simulation; actual daemon coexistence is covered
/// independently by InfrastructureLifecycleE2ETests.</summary>
[Collection(ScenarioCollection.Name)]
public sealed class S33_RcloneOrphanReaper : ScenarioBase
{
    protected override string ScenarioName => "S33-rclone-orphan-reaper";
    [AvaloniaFact] public async Task Run_S33() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        var owner = new OwnedProcessRegistry.Identity(100, 123, "Backspace.exe");
        var child = new OwnedProcessRegistry.Identity(200, 456, "rclone.exe");
        var record = Path.Combine(ctx.RootDir, "owner.json");
        var cases = new[]
        {
            ("Live owner preserved", OwnedProcessRegistry.Presence.Matching, OwnedProcessRegistry.Presence.Matching, 0),
            ("Uninspectable owner preserved", OwnedProcessRegistry.Presence.Unknown, OwnedProcessRegistry.Presence.Matching, 0),
            ("Reused child PID preserved", OwnedProcessRegistry.Presence.Missing, OwnedProcessRegistry.Presence.Different, 0),
            ("Verified dead-owner orphan recovered", OwnedProcessRegistry.Presence.Missing, OwnedProcessRegistry.Presence.Matching, 1),
        };
        foreach (var (name, ownerState, childState, expected) in cases)
        {
            OwnedProcessRegistry.Write(record, new(owner, child));
            var terminations = 0;
            OwnedProcessRegistry.Reap(record,
                identity => identity.Pid == owner.Pid ? ownerState : childState,
                _ => terminations++);
            Assert.Equal(expected, terminations);
            ctx.Report.RecordNote(name, $"Persisted JSON record; expected {expected} simulated termination(s), observed {terminations}.");
        }
        File.WriteAllText(record, "200|rclone.exe");
        var legacyTerminations = 0;
        OwnedProcessRegistry.Reap(record, _ => OwnedProcessRegistry.Presence.Missing, _ => legacyTerminations++);
        Assert.Equal(0, legacyTerminations);
        ctx.Report.RecordNote("Legacy path-only record preserved", "Observed zero simulated terminations: an executable path never proves process ownership.");
        ctx.Report.RecordNote("Proof boundary", "Process inspection and termination are fakes. No user's daemon or rclone command was touched. The test exercises persisted record parsing and the production recovery decision/action route.");
        return Task.CompletedTask;
    }
}