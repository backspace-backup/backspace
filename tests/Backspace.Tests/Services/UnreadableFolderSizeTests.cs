using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U106 (4 November 2026): "not missing important big folders". A folder the
/// person's account can't open (another account's profile) used to vanish
/// from the estimate silently, although the background service's backup
/// includes it. The estimate now names what it couldn't count and asks the
/// running service, which has the backup's rights, to measure instead.
/// No permission is changed: Windows' own System Volume Information folder,
/// which no account opens without special rights, stands in.
/// </summary>
[Collection(Backspace.Tests.Scenarios.ScenarioCollection.Name)]
public sealed class UnreadableFolderSizeTests
{
    private static string? LockedFolder()
    {
        var folder = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "System Volume Information");
        return OperatingSystem.IsWindows() && Directory.Exists(folder) && TreeWalk.ListOrNull(folder) is null ? folder : null;
    }

    [Fact]
    public void A_folder_that_cannot_be_opened_is_reported_not_silently_skipped()
    {
        if (LockedFolder() is not { } locked) return; // only where Windows really refuses it
        var reported = 0;
        TreeWalk.Walk(locked, (_, _) => null, DateTime.UtcNow.AddMinutes(1), CancellationToken.None, _ => Interlocked.Increment(ref reported));
        Assert.Equal(1, reported);
    }

    [Fact]
    public async Task The_service_measures_when_something_could_not_be_opened_and_only_then()
    {
        var previous = SourceSizeProbe.Elevated;
        var asked = 0;
        SourceSizeProbe.Elevated = (path, _, _) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult<SourceSizeResult?>(new SourceSizeResult(path, 300_000_000_000, 42, true, false, MeasuredByService: true));
        };
        try
        {
            using var workspace = new TempWorkspace("unreadable-size");
            File.WriteAllText(Path.Combine(workspace.Root, "a.txt"), "hello");
            var readable = await new SourceSizeProbe().GetOrComputeAsync(workspace.Root, CancellationToken.None);
            Assert.Equal(0, asked); // everything was read: no need to ask
            Assert.Null(readable.UnreadableNote);
            Assert.False(readable.MeasuredByService);

            if (LockedFolder() is not { } locked) return;
            var measured = await new SourceSizeProbe().GetOrComputeAsync(locked, CancellationToken.None);
            Assert.Equal(1, asked);
            Assert.True(measured.MeasuredByService);
            Assert.Equal(300_000_000_000, measured.Bytes);

            // Without the service, the estimate says what the backup will leave out.
            SourceSizeProbe.Elevated = null;
            var alone = await new SourceSizeProbe().GetOrComputeAsync(locked, CancellationToken.None);
            Assert.Equal(1, alone.UnreadableFolders);
            // The backup leaves it out too, so the size is right without it; the
            // note says the folder won't be backed up, not that the size is short.
            Assert.Equal("1 folder won't be backed up because Backspace can't open it: " + locked + ". Turn on administrator access in Settings to include it.", alone.UnreadableNote);
            Assert.Equal("3 folders won't be backed up because they can't be opened.", (alone with { UnreadableFolders = 3, MeasuredByService = true }).UnreadableNote);
        }
        finally { SourceSizeProbe.Elevated = previous; }
    }

    [Fact]
    public async Task The_service_lists_a_folder_and_returns_only_totals()
    {
        using var workspace = new TempWorkspace("service-measure");
        Directory.CreateDirectory(Path.Combine(workspace.Root, "sub"));
        File.WriteAllBytes(Path.Combine(workspace.Root, "sub", "data.bin"), new byte[1234]);
        File.WriteAllBytes(Path.Combine(workspace.Root, "top.bin"), new byte[66]);
        var measurements = new ServiceSizeMeasurements();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var started = JsonSerializer.Deserialize<ServiceSizeMeasurements.Reply>(measurements.Start(workspace.Root), options)!;
        Assert.Equal("running", started.State);
        ServiceSizeMeasurements.Reply reply;
        do
        {
            await Task.Delay(50);
            reply = JsonSerializer.Deserialize<ServiceSizeMeasurements.Reply>(measurements.Result(started.RunId!), options)!;
        } while (reply.State == "running");
        Assert.Equal("done", reply.State);
        Assert.Equal(1300, reply.Bytes);
        Assert.Equal(2, reply.Files);
        Assert.Equal(started.RunId, reply.RunId);
        // A result is handed out once.
        Assert.Equal("unknown", JsonSerializer.Deserialize<ServiceSizeMeasurements.Reply>(measurements.Result(started.RunId!), options)!.Error);
        Assert.Equal("not-a-folder", JsonSerializer.Deserialize<ServiceSizeMeasurements.Reply>(measurements.Start(Path.Combine(workspace.Root, "missing")), options)!.Error);
    }

    [Fact]
    public void The_service_accepts_a_measurement_request_only_for_a_local_folder()
    {
        using var workspace = new TempWorkspace("service-measure-request");
        ServiceHostRequest Parse(object request) => ServiceHostRequest.Parse(JsonSerializer.SerializeToUtf8Bytes(request));
        Assert.Equal(workspace.Root, Parse(new { version = 1, command = "measure", path = workspace.Root }).Path);
        Assert.Throws<InvalidDataException>(() => Parse(new { version = 1, command = "measure", path = "relative\\folder" }));
        Assert.Throws<InvalidDataException>(() => Parse(new { version = 1, command = "measure", path = @"\\server\share" }));
        Assert.Throws<InvalidDataException>(() => Parse(new { version = 1, command = "measure", path = Path.Combine(workspace.Root, "missing") }));
        // Other requests can't carry a path.
        Assert.Throws<InvalidDataException>(() => Parse(new { version = 1, command = "activity", path = workspace.Root }));
        var id = Guid.NewGuid().ToString("N");
        Assert.Equal(id, Parse(new { version = 1, command = "measure-result", runId = id }).RunId);
        Assert.Throws<InvalidDataException>(() => Parse(new { version = 1, command = "measure-result", runId = "not-an-id" }));
    }

    /// <summary>U106 follow-up: System Volume Information, the page and
    /// hibernation files and the other files useless in any restore are never
    /// backed up, whatever a backup's filter text says, and never counted.</summary>
    [Fact]
    public async Task Useless_system_files_are_always_excluded_and_never_counted()
    {
        // An imported backup with no filters at all still excludes them.
        var plan = ResticBackrestEngine.BuildPlanFromBackup("plan", "repo", new Backspace.Models.Backup { FiltersText = "" }, @"C:\");
        var all = string.Join("\n", plan.Excludes.Concat(plan.IExcludes));
        foreach (var name in new[] { "System Volume Information", "pagefile.sys", "hiberfil.sys", "swapfile.sys" })
            Assert.Contains(name, all);
        Assert.Equal(plan.IExcludes.Count, plan.IExcludes.Distinct().Count());
        // OneDrive's scratch only at the top of the drive, never a folder of that name anywhere.
        Assert.Contains("C:/OneDriveTemp/", all);
        Assert.DoesNotContain("**/OneDriveTemp", all);
        // Things someone may want back are only recommended, never forced.
        foreach (var name in new[] { "Windows.old", "$RECYCLE.BIN", "Recovery", "ntuser.dat", "UsrClass.dat", "/Windows/" })
            Assert.DoesNotContain(name, all);
        // A rule in the text can't bring them back: the essentials come last.
        var withNegation = ResticBackrestEngine.BuildPlanFromBackup("plan", "repo", new Backspace.Models.Backup { FiltersText = "!/hiberfil.sys" }, @"C:\");
        Assert.EndsWith("hiberfil.sys", withNegation.IExcludes.Concat(withNegation.Excludes).Last(p => p.Contains("hiberfil")));

        using var workspace = new TempWorkspace("essential-size");
        File.WriteAllBytes(Path.Combine(workspace.Root, "pagefile.sys"), new byte[5000]);
        Directory.CreateDirectory(Path.Combine(workspace.Root, "System Volume Information"));
        File.WriteAllBytes(Path.Combine(workspace.Root, "System Volume Information", "tracking.log"), new byte[7000]);
        Directory.CreateDirectory(Path.Combine(workspace.Root, "docs"));
        File.WriteAllBytes(Path.Combine(workspace.Root, "docs", "pagefile.sys"), new byte[11]); // not at the root: a user file
        File.WriteAllBytes(Path.Combine(workspace.Root, "notes.txt"), new byte[100]);
        // A folder, not a drive: these are the person's own files and count.
        var size = await new SourceSizeProbe().GetOrComputeAsync(workspace.Root, CancellationToken.None);
        Assert.Equal(12111, size.Bytes);
        Assert.Equal(4, size.FileCount);
        Assert.True(RecommendedFilters.IsAlwaysExcludedAtRoot(@"C:\", @"C:\System Volume Information", isDirectory: true));
        Assert.False(RecommendedFilters.IsAlwaysExcludedAtRoot(@"C:\", @"C:\System Volume Information", isDirectory: false));
        Assert.False(RecommendedFilters.IsAlwaysExcludedAtRoot(@"C:\", @"C:\Users\me\pagefile.sys", isDirectory: false));
    }
}
