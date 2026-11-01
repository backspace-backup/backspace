using System;
using System.IO;
using System.Text.Json;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class RestoreComparisonOptionsTests
{
    [Fact]
    public void ComparisonIsOptionalAndSurvivesSavedOptions()
    {
        Assert.False(new TestRestoreOptions().CompareOriginals);
        var viewModel = new RestoreCheckOptionsViewModel(new(Full: true, CompareOriginals: true));
        var reopened = new RestoreCheckOptionsViewModel(JsonSerializer.Deserialize<TestRestoreOptions>(JsonSerializer.Serialize(viewModel.ToOptions())));
        Assert.True(reopened.Full);
        Assert.True(reopened.CompareOriginals);
        reopened.CompareOriginals = false;
        Assert.False(reopened.ToOptions().CompareOriginals);
    }

    [Fact]
    public void RecoveryCheckDoesNotReadOriginalEvenWhenItIsLocked()
    {
        using var ws = new TempWorkspace();
        var source = ws.Sub("source"); var restored = ws.Sub("restored");
        var stamp = new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);
        var path = Path.Combine(source, "file.txt");
        File.WriteAllText(path, "newer"); File.SetLastWriteTimeUtc(path, stamp);
        File.WriteAllText(Path.Combine(restored, "file.txt"), "saved");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = BackupVerifier.VerifyOneFile(new("file.txt", 5, stamp), source, restored, new(), compareOriginals: false);
        Assert.Equal(FileVerifyStatus.Recovered, result.Status);
        Assert.Throws<IOException>(() => BackupVerifier.VerifyOneFile(new("file.txt", 5, stamp), source, restored, new(), compareOriginals: true));
    }

    [Fact]
    public void OptionalComparisonDetectsDifferenceButDoesNotCallItBackupCorruption()
    {
        using var ws = new TempWorkspace();
        var source = ws.Sub("source"); var restored = ws.Sub("restored");
        var stamp = new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(Path.Combine(source, "file.txt"), "newer"); File.SetLastWriteTimeUtc(Path.Combine(source, "file.txt"), stamp);
        File.WriteAllText(Path.Combine(restored, "file.txt"), "saved");
        var result = BackupVerifier.VerifyOneFile(new("file.txt", 5, stamp), source, restored, new(), compareOriginals: true);
        Assert.Equal(FileVerifyStatus.BytesMismatch, result.Status);
        Assert.Contains("does not show which copy changed", result.Detail);
    }

    [Fact]
    public void TurningOffComparisonDoesNotHideMissingOrWrongSizedRecoveredFiles()
    {
        using var ws = new TempWorkspace();
        var source = ws.Sub("absent-source"); var restored = ws.Sub("restored");
        Assert.Equal(FileVerifyStatus.RestoreFailed, BackupVerifier.VerifyOneFile(new("missing", 5, DateTime.UtcNow), source, restored, new(), compareOriginals: false).Status);
        File.WriteAllText(Path.Combine(restored, "wrong-size"), "short");
        Assert.Equal(FileVerifyStatus.SizeMismatch, BackupVerifier.VerifyOneFile(new("wrong-size", 8, DateTime.UtcNow), source, restored, new(), compareOriginals: false).Status);
    }
}
