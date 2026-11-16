using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>Owner, 16 November 2026: a backup sat at "100%" with Stop for an
/// hour while restic still sent the last data over a slow upload.</summary>
public sealed class BackupFinishingTests
{
    [Fact]
    public void Everything_read_but_still_sending_says_finishing_not_100_percent()
    {
        var line = BackupLiveProgress.Format(new BackupLiveProgress.Sample(100, 30_000_000, 30_000_000, 3, 3), counting: false,
            expectedBytes: 0, sentBytes: 1_000_000, sentPerSecond: 1024);
        var shown = BackupLiveProgress.Finishing(line);
        Assert.StartsWith("[progress] 99.9% · Finishing: sending the last data · 3 of 3 files checked", shown);

        // Anything else is left as it is.
        var midway = BackupLiveProgress.Format(new BackupLiveProgress.Sample(40, 30_000_000, 12_000_000, 3, 1), counting: false,
            expectedBytes: 0, sentBytes: null, sentPerSecond: null);
        Assert.Equal(midway, BackupLiveProgress.Finishing(midway));
    }
}
