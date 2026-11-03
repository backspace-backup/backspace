using System;
using System.Text.Json;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U112 (3 November 2026): a backup showed no progress while it
/// uploaded, because only the operation's state was read. The engine's own
/// progress (restic's status, recorded by Backrest) now becomes a line.</summary>
public sealed class BackupLiveProgressTests
{
    // Backrest's JSON for a running backup: 64-bit numbers arrive as strings.
    private static JsonElement Operation(double fraction, long bytesDone, long totalBytes, long filesDone, long totalFiles)
        => JsonSerializer.SerializeToElement(new
        {
            lastStatus = new
            {
                status = new
                {
                    percentDone = fraction, totalFiles = totalFiles.ToString(), totalBytes = totalBytes.ToString(),
                    filesDone = filesDone.ToString(), bytesDone = bytesDone.ToString(), currentFile = new[] { @"C:\Users\me\a.txt" },
                },
            },
        });

    [Fact]
    public void A_running_backup_reports_percent_bytes_files_and_speed()
    {
        var live = new BackupLiveProgress();
        var start = new DateTime(2026, 11, 3, 7, 30, 0, DateTimeKind.Utc);
        Assert.Equal("[progress] 10.0% · Backing up · 5.0 GB of 50 GB · 1,000 of 10,000 files",
            live.Next(Operation(0.1, 5_000_000_000, 50_000_000_000, 1_000, 10_000), start));
        // Too soon for another line.
        Assert.Null(live.Next(Operation(0.11, 5_500_000_000, 50_000_000_000, 1_100, 10_000), start.AddSeconds(2)));
        // Five seconds later: 150 MB more, so 30 MB/s.
        Assert.Equal("[progress] 10.3% · Backing up · 5.2 GB of 50 GB · 1,200 of 10,000 files · 30 MB/s",
            live.Next(Operation(0.103, 5_150_000_000, 50_000_000_000, 1_200, 10_000), start.AddSeconds(5)));
    }

    [Fact]
    public void Before_anything_is_done_it_says_scanning_and_unchanged_progress_is_not_repeated()
    {
        var live = new BackupLiveProgress();
        var start = DateTime.UtcNow;
        Assert.Equal("[progress] 0.0% · Scanning · 0 B of 12 GB · 0 of 18,000 files",
            live.Next(Operation(0, 0, 12_000_000_000, 0, 18_000), start));
        Assert.Null(live.Next(Operation(0, 0, 12_000_000_000, 0, 18_000), start.AddMinutes(1)));
        Assert.Null(live.Next(null, start.AddMinutes(2)));
        Assert.Null(live.Next(JsonDocument.Parse("{}").RootElement.Clone(), start.AddMinutes(3)));
    }

    [Fact]
    public void The_backup_card_receives_the_percentage_and_the_details()
    {
        var progress = new BackupProgressService();
        progress.RegisterRunCells("b1", ["C:\\"], ["Google Drive"]);
        progress.ReportCellStarted("b1", "C:\\", "Google Drive");
        progress.ReportLine("b1", "C:\\", "Google Drive", "[progress] 42.5% · Backing up · 21 GB of 50 GB · 4,200 of 10,000 files · 31 MB/s");
        var snapshot = progress.Snapshot("b1")!;
        var source = snapshot.Sources["C:\\"];
        Assert.Equal(42.5, source.PercentComplete, 1);
        Assert.Equal("Backing up · 21 GB of 50 GB · 4,200 of 10,000 files · 31 MB/s", source.Detail);
    }
}
