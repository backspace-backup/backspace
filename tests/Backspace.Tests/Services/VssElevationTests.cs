using System;
using System.IO;
using System.Linq;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Covers the testable surface of the VSS auto-elevate subsystem:
/// the elevation decision (a pure function) and the child-process
/// argv builder. The actual elevation crossing requires UAC consent
/// from a live user and is exercised by manual smoke-test only.
/// </summary>
public class VssElevationTests
{
    // ---- VssElevationPolicy ----

    [Fact]
    public void Runs_without_access_use_a_copy_without_VSS_and_leave_the_setting()
    {
        // Runs never raise a Windows prompt (U026): without administrator
        // access they back up without VSS. The saved backup keeps VSS on.
        var backup = new Backup { Name = "Documents", UseVss = true, SourcePaths = { @"C:\Docs" } };
        var copy = VssElevationPolicy.WithoutVss(backup);
        Assert.False(copy.UseVss);
        Assert.True(backup.UseVss);
        Assert.Equal(backup.Id, copy.Id);
        Assert.Equal(backup.SourcePaths, copy.SourcePaths);
        Assert.NotSame(backup.SourcePaths, copy.SourcePaths);
        Assert.False(VssElevationPolicy.PromptAllowed);
        Assert.Contains("Allow administrator access", VssElevationPolicy.SkippedWithoutAccessWarning);
        Assert.Contains("Try again", VssElevationPolicy.SkippedPendingApprovalWarning);
        // The test host is not an activated service copy.
        Assert.Equal(VssElevationPolicy.SkippedWithoutAccessWarning, VssElevationPolicy.SkippedWarning());
        Assert.DoesNotContain("VSS", VssElevationPolicy.SkippedWithoutAccessWarning);
    }

    [Fact]
    public void Policy_VssOff_NoElevation()
    {
        var b = new Backup { UseVss = false };
        var d = VssElevationPolicy.Evaluate(b, isWindows: true, isAlreadyElevated: false);
        Assert.False(d.RequiresElevation);
        Assert.Contains("not configured for VSS", d.Reason);
    }

    [Fact]
    public void Policy_VssOn_NonWindows_NoElevation()
    {
        // VSS is Windows-only. The flag is harmless to leave true on a
        // backup that occasionally runs on macOS/Linux (rare but
        // happens for portable installs); the policy must just ignore
        // it on non-Windows.
        var b = new Backup { UseVss = true };
        var d = VssElevationPolicy.Evaluate(b, isWindows: false, isAlreadyElevated: false);
        Assert.False(d.RequiresElevation);
        Assert.Contains("Windows-only", d.Reason);
    }

    [Fact]
    public void Policy_VssOn_WindowsAlreadyElevated_NoElevation()
    {
        // The user launched Backspace as Administrator already (rare
        // for the GUI; common for the unattended CLI under a service
        // account). No second UAC needed — restic --use-fs-snapshot
        // works in-process.
        var b = new Backup { UseVss = true };
        var d = VssElevationPolicy.Evaluate(b, isWindows: true, isAlreadyElevated: true);
        Assert.False(d.RequiresElevation);
        Assert.Contains("already elevated", d.Reason);
    }

    [Fact]
    public void Policy_VssOn_WindowsNotElevated_RequiresElevation()
    {
        var b = new Backup { UseVss = true };
        var d = VssElevationPolicy.Evaluate(b, isWindows: true, isAlreadyElevated: false);
        Assert.True(d.RequiresElevation);
        Assert.Contains("not elevated", d.Reason);
    }

    // ---- VssElevationCoordinator ----

    [Fact]
    public void Coordinator_IsFullyImplemented_Now_True()
    {
        // The helper-mode IPC landed in the same commit this assertion
        // moves to true. If the field flips back to false the
        // orchestrator's elevation decision should short-circuit so a
        // VSS-needing cell falls back gracefully; pin the contract.
        var c = new VssElevationCoordinator();
        Assert.True(c.IsFullyImplemented);
    }

    [Fact]
    public void ParseHelperArgs_AcceptsAllRequiredFields()
    {
        var args = new[]
        {
            VssElevationCoordinator.HelperCliArgument,
            "--backup-id",      "bk-1",
            "--source-path",    "/data",
            "--destination-id", "dest-1",
            "--storage-name",   "default",
            VssElevationCoordinator.ResultFileArgument, "/tmp/result.json",
        };

        var parsed = VssElevationCoordinator.ParseHelperArgs(args);
        Assert.NotNull(parsed);
        Assert.Equal("bk-1",            parsed!.BackupId);
        Assert.Equal("/data",           parsed.SourcePath);
        Assert.Equal("dest-1",          parsed.DestinationId);
        Assert.Equal("default",         parsed.StorageName);
        Assert.Equal("/tmp/result.json", parsed.ResultFile);
    }

    [Fact]
    public void ParseHelperArgs_RejectsMissingFields()
    {
        // Any one missing field is fatal — the helper can't run
        // without all five. Test each omission separately so a
        // regression to "treats missing as default-empty-string" is
        // caught.
        Assert.Null(VssElevationCoordinator.ParseHelperArgs(new[]
        {
            VssElevationCoordinator.HelperCliArgument,
            "--source-path", "/x", "--destination-id", "d", "--storage-name", "n", VssElevationCoordinator.ResultFileArgument, "/t",
        }));
        Assert.Null(VssElevationCoordinator.ParseHelperArgs(new[]
        {
            VssElevationCoordinator.HelperCliArgument,
            "--backup-id", "b", "--destination-id", "d", "--storage-name", "n", VssElevationCoordinator.ResultFileArgument, "/t",
        }));
        Assert.Null(VssElevationCoordinator.ParseHelperArgs(new[]
        {
            VssElevationCoordinator.HelperCliArgument,
            "--backup-id", "b", "--source-path", "/x", "--storage-name", "n", VssElevationCoordinator.ResultFileArgument, "/t",
        }));
        Assert.Null(VssElevationCoordinator.ParseHelperArgs(new[]
        {
            VssElevationCoordinator.HelperCliArgument,
            "--backup-id", "b", "--source-path", "/x", "--destination-id", "d", VssElevationCoordinator.ResultFileArgument, "/t",
        }));
        Assert.Null(VssElevationCoordinator.ParseHelperArgs(new[]
        {
            VssElevationCoordinator.HelperCliArgument,
            "--backup-id", "b", "--source-path", "/x", "--destination-id", "d", "--storage-name", "n",
        }));
    }

    [Fact]
    public void Coordinator_BuildElevatedStartInfo_CarriesIdsOnArgv()
    {
        // Use the current test assembly's exe path; it exists on disk
        // (we're running it) and the builder only validates that the
        // file is present, not that it's the Backspace binary.
        var exe = typeof(VssElevationTests).Assembly.Location;
        var req = new VssElevationRequest(
            new Backup { Id = "bk-uid-123", Name = "Photos", UseVss = true },
            "/data/photos",
            new Destination { Id = "dest-uid-456", Name = "External", Kind = DestinationKind.LocalFolder },
            "primary");

        var psi = VssElevationCoordinator.BuildElevatedStartInfo(exe, req);

        // UseShellExecute + Verb=runas is what triggers UAC. Without
        // both, Windows silently launches the child at the parent's
        // privilege.
        Assert.True(psi.UseShellExecute);
        Assert.Equal("runas", psi.Verb);

        var args = psi.ArgumentList.ToList();
        Assert.Contains(VssElevationCoordinator.HelperCliArgument, args);
        Assert.Contains("bk-uid-123",   args);
        Assert.Contains("dest-uid-456", args);
        Assert.Contains("/data/photos", args);
        Assert.Contains("primary",      args);
        Assert.Contains(VssElevationCoordinator.ConfigRootArgument, args);
        Assert.Contains(Path.GetFullPath(AppPaths.ConfigRoot), args);
    }

    [Fact]
    public void Coordinator_BuildElevatedStartInfo_IncludesResultFileWhenProvided()
    {
        // The parent supplies a result-file path so the helper writes
        // its RunRecord there on exit. The argv must carry that path
        // along with the other identifiers.
        var exe = typeof(VssElevationTests).Assembly.Location;
        var req = new VssElevationRequest(
            new Backup { Id = "b", Name = "Photos", UseVss = true },
            "/data",
            new Destination { Id = "d", Kind = DestinationKind.LocalFolder },
            "default");

        var psi = VssElevationCoordinator.BuildElevatedStartInfo(
            exe, req, resultFilePath: @"C:\tmp\result.json");

        var args = psi.ArgumentList.ToList();
        Assert.Contains(VssElevationCoordinator.ResultFileArgument, args);
        Assert.Contains(@"C:\tmp\result.json", args);
    }

    [Fact]
    public void Coordinator_BuildElevatedStartInfo_MissingExeThrows()
    {
        var nonexistent = Path.Combine(Path.GetTempPath(), "no-such-backspace-" + Guid.NewGuid().ToString("N"));
        var req = new VssElevationRequest(
            new Backup { Id = "b", Name = "x", UseVss = true },
            "/x",
            new Destination { Id = "d", Kind = DestinationKind.LocalFolder },
            "default");

        Assert.Throws<FileNotFoundException>(
            () => VssElevationCoordinator.BuildElevatedStartInfo(nonexistent, req));
    }
}
