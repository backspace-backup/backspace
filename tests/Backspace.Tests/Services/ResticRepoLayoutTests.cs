using System.IO;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Pins the on-disk layout shape the "Locate moved repository" flow
/// uses to decide between three outcomes:
///   • ValidRestic    → swap the destination URI silently
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
        // Mirror what restic init writes: an unencrypted config FILE
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
}
