using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class VerifierCoverageRegressionTests
{
    [Fact]
    public async Task FullVerification_CoversAllSources_ZeroLength_AndMoreThanTenFiles()
    {
        using var workspace = new TempWorkspace("verify-all-sources");
        var first = workspace.Sub("first");
        var second = workspace.Sub("second");
        for (var i = 0; i < 14; i++) File.WriteAllText(Path.Combine(first, $"file-{i}.txt"), $"original-{i}");
        File.WriteAllText(Path.Combine(second, "empty.txt"), "");
        File.WriteAllText(Path.Combine(second, "second.txt"), "second source content");
        var destination = new Destination { Name = "Verification fixture", Kind = DestinationKind.LocalFolder, PathOrSubpath = workspace.Sub("storage"), Encrypted = false };
        var backup = new Backup { Name = "All sources " + Guid.NewGuid().ToString("N"), SourcePaths = { first, second }, UseVss = false,
            Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } } };
        ServiceLocator.Config.Update(config => { config.Backups.Clear(); config.Destinations.Clear(); config.Backups.Add(backup); config.Destinations.Add(destination); });
        foreach (var source in backup.SourcePaths)
            await ServiceLocator.Engine.BackupPruneCheckAsync(backup, source, destination, "default", default);
        var outcome = await ServiceLocator.Verifier.VerifyAsync(backup, default);
        Assert.True(outcome.OverallPass, outcome.Summary);
        Assert.Equal(16, outcome.FilesPicked);
        Assert.All(outcome.Files, file => Assert.Equal(FileVerifyStatus.OK, file.Status));
        Assert.Contains(outcome.Files, file => file.SourcePath == second && file.Path == "empty.txt" && file.ExpectedBytes == 0);

        // Negative control: corrupt only the second source's stored bytes.
        var stored = Directory.GetFiles(destination.PathOrSubpath, "second.txt", SearchOption.AllDirectories).Single();
        var bytes = File.ReadAllBytes(stored);
        bytes[0] ^= 0x7f;
        File.WriteAllBytes(stored, bytes);
        var corrupt = await ServiceLocator.Verifier.VerifyAsync(backup, default);
        Assert.False(corrupt.OverallPass);
        Assert.Contains(corrupt.Files, file => file.SourcePath == second && file.Status == FileVerifyStatus.BytesMismatch);
    }

    [Fact]
    public void CompletenessScan_FindsMissingUnchangedFile_ButHonorsExclusionsAndNewFiles()
    {
        using var workspace = new TempWorkspace("verify-missing");
        var source = workspace.Sub("source");
        File.WriteAllText(Path.Combine(source, "omitted.txt"), "should have been included");
        File.WriteAllText(Path.Combine(source, "excluded.tmp"), "explicitly excluded");
        File.WriteAllText(Path.Combine(source, "new.txt"), "created later");
        var captured = DateTime.UtcNow.AddMinutes(1);
        File.SetLastWriteTimeUtc(Path.Combine(source, "new.txt"), captured.AddMinutes(1));
        var backup = new Backup { FiltersText = "*.tmp" };
        var missing = BackupVerifier.FindMissingSnapshotFiles(backup, source, Array.Empty<RevisionFile>(), captured, CancellationToken.None);
        var item = Assert.Single(missing);
        Assert.Equal("omitted.txt", item.Path);
        Assert.Equal(FileVerifyStatus.RestoreFailed, item.Status);
    }
}
