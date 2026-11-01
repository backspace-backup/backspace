using System;
using System.IO;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backspace.Models;
using Backspace.Services.Platform.Windows;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceRunAuthorizationTests
{
    private const string Owner = "S-1-5-21-100-200-300-1001";
    private const string Revision = "11111111111111111111111111111111";
    private static AppConfig Config() => new()
    {
        Destinations = { new() { Id = "destination", Name = "Approved destination", PathOrSubpath = @"D:\Backups" } },
        Backups = { new() { Id = "backup", Name = "Approved backup", SourcePaths = { @"C:\Documents" },
            Targets = { new() { DestinationId = "destination" } }, CheckSchedule = new(), PruneSchedule = new() } },
    };
    private static byte[] Request(string operation = "backup", string revision = Revision, string id = "backup") =>
        JsonSerializer.SerializeToUtf8Bytes(new ServiceRunAuthorization.Request(1, revision, id, operation), ServiceRunAuthorization.Json);
    private static ServiceRunAuthorization Policy(AppConfig? config = null) => ServiceRunAuthorization.ValidateForTest(
        ServiceRunAuthorization.CreateApproval(Owner, Revision, config ?? Config()), Owner);

    [Theory]
    [InlineData("backup", RunKind.Backup)] [InlineData("check", RunKind.Check)] [InlineData("prune", RunKind.Prune)]
    public void ApprovedOperationsUseOnlyProtectedConfiguration(string operation, RunKind kind)
    {
        var run = Policy().Authorize(Request(operation));
        Assert.Equal(kind, run.Kind);
        Assert.Equal(@"C:\Documents", Assert.Single(run.Backup.SourcePaths));
        Assert.Equal(@"D:\Backups", Assert.Single(run.Configuration.Destinations).PathOrSubpath);
    }

    [Theory]
    [InlineData("restore")] [InlineData("erase")] [InlineData("run-command")] [InlineData("Backup")] [InlineData("")]
    public void UnapprovedOperationsAreRejected(string operation) =>
        Assert.Throws<SecurityException>(() => Policy().Authorize(Request(operation)));

    [Fact]
    public void StaleRevisionUnknownBackupAndDisabledBackupAreRejected()
    {
        var policy = Policy();
        Assert.Throws<SecurityException>(() => policy.Authorize(Request(revision: "22222222222222222222222222222222")));
        Assert.Throws<SecurityException>(() => policy.Authorize(Request(id: "unapproved")));
        var cfg = Config(); cfg.Backups[0].Enabled = false;
        Assert.Throws<SecurityException>(() => Policy(cfg).Authorize(Request()));
    }

    [Fact]
    public void UnscheduledMaintenanceIsNotAnAllowedDesktopRequest()
    {
        var cfg = Config(); cfg.Backups[0].CheckSchedule = null; cfg.Backups[0].PruneSchedule = null;
        var policy = Policy(cfg);
        Assert.Throws<SecurityException>(() => policy.Authorize(Request("check")));
        Assert.Throws<SecurityException>(() => policy.Authorize(Request("prune")));
        Assert.Equal(RunKind.Backup, policy.Authorize(Request()).Kind);
    }

    [Theory]
    [InlineData("command", "cmd.exe")] [InlineData("target", "C:\\Windows")] [InlineData("configuration", "arbitrary")]
    public void ExtraRequestFieldsCannotSmuggleExecutionInputs(string field, string value)
    {
        var request = JsonNode.Parse(Request())!.AsObject(); request[field] = value;
        Assert.Throws<JsonException>(() => Policy().Authorize(Encoding.UTF8.GetBytes(request.ToJsonString())));
    }

    [Theory]
    [InlineData("check", RunKind.Check)]
    [InlineData("prune", RunKind.Prune)]
    public void OwnerCommandCanRequestMaintenanceWithoutCreatingAnAutomaticSchedule(string operation, RunKind kind)
    {
        var cfg = Config(); cfg.Backups[0].CheckSchedule = null; cfg.Backups[0].PruneSchedule = null;
        var policy = Policy(cfg);
        var run = policy.Authorize(Request(operation), ownerCommand: true);
        Assert.Equal(kind, run.Kind);
        Assert.Equal(@"D:\Backups", Assert.Single(run.Configuration.Destinations).PathOrSubpath);
        Assert.Null(run.Backup.CheckSchedule); Assert.Null(run.Backup.PruneSchedule);
        Assert.Throws<SecurityException>(() => policy.Authorize(Request(operation)));
        Assert.Throws<SecurityException>(() => policy.Authorize(Request(operation, revision: "22222222222222222222222222222222"), ownerCommand: true));
        Assert.Throws<SecurityException>(() => policy.Authorize(Request(operation, id: "other"), ownerCommand: true));
        Assert.Throws<SecurityException>(() => policy.Authorize(Request("erase"), ownerCommand: true));
        cfg.Backups[0].Enabled = false;
        Assert.Throws<SecurityException>(() => Policy(cfg).Authorize(Request(operation), ownerCommand: true));
    }

    [Fact]
    public void DuplicateJsonPropertiesCannotChangeRequestInterpretation()
    {
        var request = Encoding.UTF8.GetString(Request()).Replace("\"operation\":\"backup\"", "\"operation\":\"backup\",\"operation\":\"erase\"");
        Assert.Throws<InvalidDataException>(() => Policy().Authorize(Encoding.UTF8.GetBytes(request)));
    }

    [Fact]
    public void AlteredConfigurationAndAnotherOwnersApprovalAreRejected()
    {
        var bytes = ServiceRunAuthorization.CreateApproval(Owner, Revision, Config());
        Assert.Throws<SecurityException>(() => ServiceRunAuthorization.ValidateForTest(bytes, "S-1-5-21-100-200-300-1002"));
        var altered = JsonNode.Parse(bytes)!; altered["configuration"]!["backups"]![0]!["sourcePaths"]![0] = @"C:\Windows";
        Assert.Throws<SecurityException>(() => ServiceRunAuthorization.ValidateForTest(Encoding.UTF8.GetBytes(altered.ToJsonString()), Owner));
    }

    [Fact]
    public void ACompletedRunCannotMutateTheNextApproval()
    {
        var policy = Policy(); var first = policy.Authorize(Request());
        first.Backup.SourcePaths[0] = @"C:\Windows";
        first.Configuration.Destinations[0].PathOrSubpath = @"C:\Other";
        var second = policy.Authorize(Request());
        Assert.Equal(@"C:\Documents", second.Backup.SourcePaths[0]);
        Assert.Equal(@"D:\Backups", second.Configuration.Destinations[0].PathOrSubpath);
    }

    [Theory]
    [InlineData("duplicate-backup")] [InlineData("duplicate-destination")] [InlineData("missing-target")]
    [InlineData("missing-source")] [InlineData("extra-arguments")]
    public void AmbiguousOrUnsafeApprovalsAreRejected(string mutation)
    {
        var cfg = Config();
        switch (mutation)
        {
            case "duplicate-backup": cfg.Backups.Add(cfg.Backups[0]); break;
            case "duplicate-destination": cfg.Destinations.Add(cfg.Destinations[0]); break;
            case "missing-target": cfg.Backups[0].Targets[0].DestinationId = "missing"; break;
            case "missing-source": cfg.Backups[0].SourcePaths.Clear(); break;
            case "extra-arguments": cfg.Backups[0].ExtraBackupArgs = "--password-command cmd.exe"; break;
        }
        Assert.Throws<InvalidDataException>(() => Policy(cfg));
    }

    [Fact]
    public void OversizedRequestAndApprovalAreRejectedBeforeDeserialization()
    {
        Assert.Throws<InvalidDataException>(() => Policy().Authorize(new byte[4097]));
        Assert.Throws<InvalidDataException>(() => ServiceRunAuthorization.ValidateForTest(new byte[ServiceRunAuthorization.MaximumApprovalBytes + 1], Owner));
    }

    /// <summary>An approval saved by an earlier version lacks settings added
    /// since. Verifying it by serializing the configuration again added them
    /// with their defaults, so after an update every approval was "invalid"
    /// and no change could be applied (31 October 2026, lab VM).</summary>
    [Fact]
    public void An_approval_saved_before_a_setting_was_added_still_verifies()
    {
        var current = JsonNode.Parse(ServiceRunAuthorization.CreateApproval(Owner, Revision, Config()))!;
        var configuration = current["configuration"]!;
        Assert.NotNull(configuration["backups"]![0]!["ignoredRecommendationChanges"]);
        // What version 1.0 wrote: no such setting, digest over that text.
        configuration["backups"]![0]!.AsObject().Remove("ignoredRecommendationChanges");
        var savedText = configuration.ToJsonString();
        current["configurationSha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(savedText)));
        var earlier = Encoding.UTF8.GetBytes(current.ToJsonString());
        var policy = ServiceRunAuthorization.ValidateForTest(earlier, Owner);
        Assert.Equal(RunKind.Backup, policy.Authorize(Request()).Kind);
        // Altering the saved text is still detected.
        configuration["backups"]![0]!["sourcePaths"]![0] = @"C:\Windows";
        var altered = Encoding.UTF8.GetBytes(current.ToJsonString());
        Assert.Throws<SecurityException>(() => ServiceRunAuthorization.ValidateForTest(altered, Owner));
    }
}
