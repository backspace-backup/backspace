using System.Text.Json;
using Backspace.Services;
using Backspace.Models;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Regressions for the JSON wire-format models <see cref="ResticBackrestEngine"/>
/// exchanges with the Backrest daemon. The bug they pin: a previous
/// version of these models silently dropped unknown JSON fields on
/// round-trip, so a config emitted by Backrest 1.14+ (with
/// <c>sync.identity</c>) would lose those fields when Backspace
/// did <c>GetConfig → mutate → SetConfig</c>, the daemon's
/// in-memory state diverged from what we sent, and the immediate
/// /v1.Backrest/Backup RPC failed with "plan not found".
/// </summary>
public class BackrestRpcModelsTests
{
    [Fact]
    public void PinnedBackrest1141_UsesRepositoryIdForFileListing_AndOperationIdForCancel()
    {
        using var listing = JsonDocument.Parse(JsonSerializer.Serialize(new ListSnapshotFilesRequest
        { RepoId = "documents-default", SnapshotId = "snapshot", Path = "/Documents" }));
        Assert.Equal("documents-default", listing.RootElement.GetProperty("repo_id").GetString());
        Assert.False(listing.RootElement.TryGetProperty("repo_guid", out _));

        const string largeId = "9007199254740993"; // More than JavaScript's exact numeric range.
        using var cancel = JsonDocument.Parse(JsonSerializer.Serialize(new CancelOperationRequest { OperationId = largeId }));
        Assert.Equal(largeId, cancel.RootElement.GetProperty("operationId").GetString());
        Assert.False(cancel.RootElement.TryGetProperty("value", out _));
        var scheduled = JsonSerializer.Deserialize<BackrestScheduledOperationResponse>("{\"operationId\":\"9007199254740993\"}")!;
        Assert.Equal(largeId, scheduled.OperationId);
        Assert.Equal("0", JsonSerializer.Deserialize<BackrestScheduledOperationResponse>("{}")!.OperationId);
    }

    [Fact]
    public void BackrestConfig_roundTripsUnknownFieldsViaExtensionData()
    {
        // A v6 config the way Backrest 1.13.0 emits it — with
        // sync.identity, which our typed model doesn't carry.
        var original = """
        {
          "modno": 0,
          "version": 6,
          "instance": "backspace",
          "auth": { "disabled": true },
          "sync": {
            "identity": {
              "keyId": "ed25519.abcdef",
              "ed25519priv": "secret",
              "ed25519pub": "public"
            }
          }
        }
        """;

        var parsed = JsonSerializer.Deserialize<BackrestConfig>(original)!;
        Assert.NotNull(parsed.AdditionalFields);
        Assert.True(parsed.AdditionalFields!.ContainsKey("sync"));

        // Serialize back — the unknown 'sync' must survive.
        var rt = JsonSerializer.Serialize(parsed);
        Assert.Contains("\"sync\"", rt);
        Assert.Contains("\"identity\"", rt);
        Assert.Contains("\"keyId\"", rt);
    }

    [Fact]
    public void BackrestRepo_roundTripsUnknownFieldsViaExtensionData()
    {
        // A Repo with a hypothetical future field Backrest adds.
        var original = """
        {
          "id": "r1",
          "guid": "00112233-4455-6677-8899-aabbccddeeff",
          "uri": "/tmp/repo",
          "futureField": { "nested": 42 }
        }
        """;
        var parsed = JsonSerializer.Deserialize<BackrestRepo>(original)!;
        Assert.Equal("r1", parsed.Id);
        Assert.Equal("00112233-4455-6677-8899-aabbccddeeff", parsed.Guid);
        Assert.NotNull(parsed.AdditionalFields);
        Assert.True(parsed.AdditionalFields!.ContainsKey("futureField"));

        var rt = JsonSerializer.Serialize(parsed);
        Assert.Contains("\"futureField\"", rt);
        Assert.Contains("\"nested\":42", rt);
    }

    /// <summary>
    /// Regression for the actual root cause of "plan not found":
    /// Backrest's orchestrator REQUIRES a non-empty Guid on every
    /// repo at config-apply time. If <see cref="ResticBackrestEngine.BuildRepoFromDestination"/>
    /// produced empty Guid (the old behaviour), the daemon would
    /// silently drop the repo, the next Backup RPC would fail
    /// "plan not found", and the orchestrator burned the full
    /// retry cascade.
    /// </summary>
    [Fact]
    public void MakeRepoGuid_isStableAcrossCalls()
    {
        var a = ResticBackrestEngine.MakeRepoGuid("dest-A-default");
        var b = ResticBackrestEngine.MakeRepoGuid("dest-A-default");
        Assert.Equal(a, b);
        Assert.False(string.IsNullOrEmpty(a));
    }

    [Fact]
    public void MakeRepoGuid_isDifferentForDifferentRepoIds()
    {
        var a = ResticBackrestEngine.MakeRepoGuid("dest-A-default");
        var b = ResticBackrestEngine.MakeRepoGuid("dest-B-default");
        var c = ResticBackrestEngine.MakeRepoGuid("dest-A-passport");
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(b, c);
    }

    [Fact]
    public void MakeRepoGuid_returns64CharHex()
    {
        // Backrest 1.13.0+ rejects any guid that isn't exactly 64
        // hex chars at SetConfig time: "guid '...' invalid: must
        // be 64 characters". Earlier Backspace builds emitted a
        // canonical UUID (36 chars with dashes); the new shape
        // matches restic's repo-config-id format.
        var g = ResticBackrestEngine.MakeRepoGuid("anything");
        Assert.Matches(@"^[0-9a-f]{64}$", g);
        Assert.True(ResticBackrestEngine.IsValidBackrestGuid(g));
    }

    [Theory]
    [InlineData("",                                                                   false)]
    [InlineData("bf683127-2b82-b9e3-86c6-419f9615a2c5",                               false)] // legacy UUID
    [InlineData("a3b1c2d4e5f60123456789abcdef0123456789abcdef0123456789abcdef0123",   true)]
    [InlineData("a3b1c2d4e5f60123456789abcdef0123456789abcdef0123456789abcdef012",    false)] // 63
    [InlineData("a3b1c2d4e5f60123456789abcdef0123456789abcdef0123456789abcdef01234",  false)] // 65
    [InlineData("z3b1c2d4e5f60123456789abcdef0123456789abcdef0123456789abcdef0123",   false)] // non-hex
    public void IsValidBackrestGuid_acceptsOnly64CharLowerOrMixedHex(string guid, bool expected)
    {
        Assert.Equal(expected, ResticBackrestEngine.IsValidBackrestGuid(guid));
    }

    /// <summary>
    /// Sanity: BuildRepoFromDestination produces a repo with a
    /// non-empty Guid for a vanilla local-folder destination.
    /// Without this the daemon's orchestrator rejects the repo
    /// at SetConfig-apply time and every Backup RPC returns
    /// "plan not found".
    /// </summary>
    /// <summary>
    /// Regression for the silent-success class bug. The previous
    /// implementation defaulted unknown terminal-status strings to
    /// <see cref="Backspace.Models.BackupRunStatus.Success"/> — so a
    /// backup that hadn't actually reported success in the 1.5s pump
    /// window showed green in the UI. Now defaults to Failed: a
    /// backup we can't confirm succeeded is NOT a success.
    /// </summary>
    [Theory]
    [InlineData("STATUS_SUCCESS",        Backspace.Models.BackupRunStatus.Success)]
    [InlineData("STATUS_WARNING",        Backspace.Models.BackupRunStatus.Warning)]
    [InlineData("STATUS_ERROR",          Backspace.Models.BackupRunStatus.Failed)]
    [InlineData("STATUS_USER_CANCELLED", Backspace.Models.BackupRunStatus.Skipped)]
    [InlineData("",                      Backspace.Models.BackupRunStatus.Failed)]
    [InlineData("STATUS_UNKNOWN",        Backspace.Models.BackupRunStatus.Failed)]
    [InlineData("something_weird",       Backspace.Models.BackupRunStatus.Failed)]
    public void StatusFromBackrestTerminal_unknownOrEmpty_defaultsToFailed(string input, Backspace.Models.BackupRunStatus expected)
    {
        Assert.Equal(expected, ResticBackrestEngine.StatusFromBackrestTerminal(input));
    }

    [Fact]
    public void BuildRepoFromDestination_omitsGuid_andLeavesAutoInitializeFalse()
    {
        // Backrest 1.13.0 rejects "guid set with auto_initialize"
        // ("guid implies that repo is already initialized"). The
        // production path therefore emits an EMPTY guid AND
        // AutoInitialize=false. The actual init happens via the
        // explicit /AddRepo call in EnsurePlanAndRepoCoreAsync's
        // Phase 1 (which writes the canonical 64-char Guid back into
        // the persisted config); a follow-up SetConfig leaves the
        // entry untouched. Sending AutoInitialize=true here would be
        // ignored — AddRepo's path always runs `restic init`.
        var engine = new ResticBackrestEngine(
            sidecar: null!,                                  // not exercised by BuildRepo
            secrets: new Backspace.Services.SecretsStore());
        var dest = new Destination
        {
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = System.IO.Path.GetTempPath(),
        };
        var repo = engine.BuildRepoFromDestination(
            repoId: ResticBackrestEngine.MakeRepoId(dest, "default"),
            destination: dest);
        Assert.Equal("", repo.Guid);
        Assert.False(repo.AutoInitialize,
            "AutoInitialize must be false: /AddRepo's handler runs `restic init` and stamps the canonical Guid, " +
            "so this flag would be redundant on the wire (and Backrest would reject the combination).");
    }
}
