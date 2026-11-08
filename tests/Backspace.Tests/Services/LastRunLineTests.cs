using System;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U160: the card's last backup line is short; the counts are on hover.</summary>
public sealed class LastRunLineTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 20, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void The_owners_example_reads_when_and_what_was_uploaded()
    {
        var (label, hover) = LastRunLine.For("27.42 MB uploaded · 150 new / 195 mod / 0 rem · in 1m 29s", BackupRunStatus.Success, Now.AddMinutes(-15), Now);
        Assert.Equal("15m ago · 27.42 MB uploaded", label);
        Assert.DoesNotContain("mod", label);
        Assert.Contains("took 1m 29s", hover);
        Assert.Contains("150 new, 195 changed and 0 deleted files since the previous backup", hover);
        Assert.Contains("27.42 MB of new data uploaded", hover);
        Assert.DoesNotContain(" mod", hover);
        Assert.DoesNotContain(" rem", hover);
    }

    [Fact]
    public void Warnings_stay_visible_and_failures_keep_their_reason()
    {
        Assert.Equal("2h ago · 3.1 GB uploaded · 1 warning",
            LastRunLine.For("3.1 GB uploaded · 2 new / 0 mod / 0 rem · 1 warning · in 4m", BackupRunStatus.Warning, Now.AddHours(-2), Now).Label);
        var failed = LastRunLine.For("Google Drive sign-in expired", BackupRunStatus.Failed, Now.AddDays(-1), Now);
        Assert.Equal("Google Drive sign-in expired · 1d ago", failed.Label);
    }

    [Fact]
    public void Nothing_uploaded_says_so()
        => Assert.Equal("Just now · nothing new to upload", LastRunLine.For("Completed in 6m 10s", BackupRunStatus.Success, Now.AddSeconds(-20), Now).Label);
}
