using System.IO;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class StreamingRestoreProcessTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"message_type\":17}")]
    [InlineData("{\"message_type\":\"error\",\"error\":\"not progress\"}")]
    public void UnrelatedJsonDiagnosticsAreNotProgressAndDoNotInterruptRestore(string line)
    {
        Assert.False(ResticBackrestEngine.TryParseRestoreProgress(line, out _));
    }

    [Fact]
    public void UnexpectedProgressFieldTypesDoNotInterruptRestoreOrOverflowTheFileCount()
    {
        Assert.True(ResticBackrestEngine.TryParseRestoreProgress(
            "{\"message_type\":\"status\",\"total_files\":4294967296,\"files_restored\":\"unknown\",\"percent_done\":null}", out var progress));
        Assert.Equal(int.MaxValue, progress.TotalFiles);
        Assert.Equal(0, progress.FilesRestored);
    }

    /// <summary>The restore restic runs itself (U175): each path one
    /// argument, the snapshot after "--" (a name never read as an option), the
    /// backup's limit among the repository's options, the password and the
    /// destination's settings only in its environment, progress twice a
    /// second, whatever this process inherited notwithstanding.</summary>
    [Fact]
    public void CommandKeepsPathsAsSingleArgumentsAndUsesTheRequestedLimitAndRepositoryEnvironment()
    {
        var repository = new ResticRepository("rclone:review:path with spaces", "fixture-password",
            ["RCLONE_CONFIG=C:/review/specific destination.conf"], ResticBackrestEngine.BuildRepoFlagsForBackup(new Backup { BandwidthLimitKBps = 512 }));
        var start = ResticBackrestEngine.BuildStreamingRestoreStartInfo("C:/review/restic.exe", repository,
            "snapshot:/C/source [2026]", "C:/review/output folder", "C:/review/include file.txt");
        Assert.Equal(new[] { "restore", "--json", "--target", "C:/review/output folder", "--include-file", "C:/review/include file.txt",
            "--limit-upload", "512", "--limit-download", "512", "-o", "sftp.args=-oBatchMode=yes", "--", "snapshot:/C/source [2026]" }, start.ArgumentList);
        Assert.DoesNotContain("fixture-password", string.Join(' ', start.ArgumentList));
        Assert.Equal("fixture-password", start.Environment["RESTIC_PASSWORD"]);
        Assert.Equal("rclone:review:path with spaces", start.Environment["RESTIC_REPOSITORY"]);
        Assert.Equal("2", start.Environment["RESTIC_PROGRESS_FPS"]);
        Assert.Equal("C:/review/specific destination.conf", start.Environment["RCLONE_CONFIG"]);
        // The whole source: no selection at all.
        Assert.DoesNotContain("--include-file", ResticBackrestEngine.BuildStreamingRestoreStartInfo("C:/review/restic.exe", repository,
            "snapshot:/C/source", "C:/review/output", null).ArgumentList);
    }

    [Fact]
    public void ProgressDoesNotAccumulateAndDiagnosticTruncationCannotEraseTheErrorVerdict()
    {
        var output = new StreamingRestoreProcess.OutputSummary();
        output.Accept("{\"message_type\":\"error\",\"error\":\"Earlier failure\"}");
        for (var n = 0; n < 100_000; n++)
            output.Accept("{\"message_type\":\"status\",\"bytes_restored\":" + n + "}");
        Assert.True(output.Text.Length < 100);
        Assert.DoesNotContain("bytes_restored", output.Text);
        for (var n = 0; n < 1000; n++) output.Accept(new string('x', 100));
        const string summary = "{\"message_type\":\"summary\",\"files_restored\":17}";
        output.Accept(summary);
        Assert.True(output.HadErrors);
        Assert.DoesNotContain("Earlier failure", output.Text);
        Assert.EndsWith(summary, output.Text);
        Assert.InRange(output.Text.Length, 1, StreamingRestoreProcess.OutputSummary.DiagnosticLimit + summary.Length + 1);
    }

    [Fact]
    public async Task ReaderDeliversSplitUnicodeLinesAndTheFinalLineWithoutANewline()
    {
        var seen = new System.Collections.Generic.List<string>();
        var prefix = new string('a', 4095);
        await StreamingRestoreProcess.ReadLinesAsync(new StringReader(prefix + "é\r\nlast 🐦"), seen.Add);
        Assert.Equal(new[] { prefix + "é", "last 🐦" }, seen);
    }

    [Fact]
    public async Task UnboundedLineFailsBeforeItCanBecomeAnUnboundedBuffer()
    {
        var seen = 0;
        await Assert.ThrowsAsync<IOException>(() => StreamingRestoreProcess.ReadLinesAsync(
            new StringReader(new string('x', StreamingRestoreProcess.MaximumLineCharacters + 1)), _ => seen++));
        Assert.Equal(0, seen);
    }
}
