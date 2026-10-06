using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Pins the on-disk layout shape the "Locate moved repository" flow
/// uses to decide between three outcomes:
///   • ValidRestic    → the UI asks before changing the destination URI
///   • EmptyOrUnrelated → confirm "Start fresh?" with the user
///   • Partial / PathMissing → refuse the swap
/// Each row creates a fresh temp tree so collisions across tests
/// can't paper over a real regression.
/// </summary>
public class ResticRepoLayoutTests
{
    [Fact]
    public void Inspect_NonExistentPath_ReturnsPathMissing()
    {
        var p = Path.Combine(Path.GetTempPath(), $"no-such-folder-{System.Guid.NewGuid():N}");
        Assert.Equal(ResticRepoLayout.Verdict.PathMissing, ResticRepoLayout.Inspect(p));
    }

    [Fact]
    public void Inspect_EmptyFolder_ReturnsEmptyOrUnrelated()
    {
        var p = CreateTemp();
        Assert.Equal(ResticRepoLayout.Verdict.EmptyOrUnrelated, ResticRepoLayout.Inspect(p));
    }

    [Fact]
    public void Inspect_FolderWithUnrelatedFiles_ReturnsEmptyOrUnrelated()
    {
        var p = CreateTemp();
        File.WriteAllText(Path.Combine(p, "README.md"), "Hello");
        Directory.CreateDirectory(Path.Combine(p, "subfolder"));
        Assert.Equal(ResticRepoLayout.Verdict.EmptyOrUnrelated, ResticRepoLayout.Inspect(p));
    }

    [Fact]
    public void Inspect_FullyPopulatedResticRepo_ReturnsValidRestic()
    {
        var p = CreateTemp();
        // Mirror the layout restic init writes: a config FILE
        // plus four directories. We don't need to populate the
        // contents — Inspect only checks the entries exist.
        File.WriteAllText(Path.Combine(p, "config"), "{}");
        Directory.CreateDirectory(Path.Combine(p, "data"));
        Directory.CreateDirectory(Path.Combine(p, "keys"));
        Directory.CreateDirectory(Path.Combine(p, "snapshots"));
        Directory.CreateDirectory(Path.Combine(p, "index"));
        Assert.Equal(ResticRepoLayout.Verdict.ValidRestic, ResticRepoLayout.Inspect(p));
    }

    [Fact]
    public void Inspect_HalfPopulated_ReturnsPartial()
    {
        // A folder that has SOME of the entries but not all. This
        // could be a torn copy or a different on-disk shape we
        // don't recognise; refuse the swap rather than risk
        // pointing at corrupted-looking storage.
        var p = CreateTemp();
        File.WriteAllText(Path.Combine(p, "config"), "{}");
        Directory.CreateDirectory(Path.Combine(p, "data"));
        Directory.CreateDirectory(Path.Combine(p, "keys"));
        // Missing: snapshots, index
        Assert.Equal(ResticRepoLayout.Verdict.Partial, ResticRepoLayout.Inspect(p));
    }

    private static string CreateTemp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"restic-layout-test-{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Engine_that_cannot_run_never_confirms_repository_access(bool invalidExecutable)
    {
        var root = CreateTemp();
        var repo = Path.Combine(root, "repository");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "config"), "{}");
        foreach (var entry in ResticRepoLayout.RequiredEntries)
            if (entry != "config") Directory.CreateDirectory(Path.Combine(repo, entry));
        var engine = Path.Combine(root, "unavailable-restic.exe");
        if (invalidExecutable) File.WriteAllText(engine, "This is not an executable.");

        Assert.Equal(ResticRepoLayout.Verdict.ValidRestic, ResticRepoLayout.Inspect(repo));
        Assert.Equal(ResticRepoLayout.Verdict.VerificationUnavailable,
            await ResticRepoLayout.InspectWithDecryptionAsync(repo, "password", engine, CancellationToken.None));
    }
}
