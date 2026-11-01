using System.IO;
using System.Threading.Tasks;
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

    [Fact]
    public void CommandKeepsPathsAsSingleArgumentsAndUsesTheRequestedLimitAndRepositoryEnvironment()
    {
        var repo = new BackrestRepo { Uri = "rclone:review:path with spaces", Password = "fixture-password",
            Flags = { "--no-cache", "--limit-download=4096" },
            Env = { "RCLONE_CONFIG=C:/review/specific destination.conf", "RESTIC_PROGRESS_FPS=0" } };
        var start = ResticBackrestEngine.BuildStreamingRestoreStartInfo("C:/review/restic.exe", repo,
            "snapshot:/C/source [2026]", "C:/review/output folder", "C:/review/include file.txt", 512);
        Assert.Contains("C:/review/include file.txt", start.ArgumentList);
        Assert.Contains("snapshot:/C/source [2026]", start.ArgumentList);
        Assert.DoesNotContain("--quiet", start.ArgumentList);
        Assert.Equal("512", start.ArgumentList[^1]);
        Assert.Equal("--limit-download", start.ArgumentList[^2]);
        Assert.Equal("fixture-password", start.Environment["RESTIC_PASSWORD"]);
        Assert.Equal("2", start.Environment["RESTIC_PROGRESS_FPS"]);
        Assert.Equal("C:/review/specific destination.conf", start.Environment["RCLONE_CONFIG"]);
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
