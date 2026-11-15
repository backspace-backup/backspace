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
        Assert.Equal("15 min ago · 27.42 MB uploaded", label);
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
        Assert.Equal("2 h ago · 3.1 GB uploaded · 1 warning",
            LastRunLine.For("3.1 GB uploaded · 2 new / 0 mod / 0 rem · 1 warning · in 4m", BackupRunStatus.Warning, Now.AddHours(-2), Now).Label);
        var failed = LastRunLine.For("Google Drive sign-in expired", BackupRunStatus.Failed, Now.AddDays(-1), Now);
        Assert.Equal("Google Drive sign-in expired · 1 d ago", failed.Label);
    }

    /// <summary>U242 (owner, 15 November 2026: "3h - 293 B uploaded: it is
    /// ridiculous to show such a low amount"): under 1 MB the card counts files.</summary>
    [Fact]
    public void A_tiny_upload_counts_files_instead_of_bytes()
    {
        Assert.Equal("3 h ago · no changes",
            LastRunLine.For("293 B uploaded · 0 new / 0 mod / 0 rem · in 12s", BackupRunStatus.Success, Now.AddHours(-3), Now).Label);
        Assert.Equal("3 h ago · 1 file changed",
            LastRunLine.For("412 B uploaded · 0 new / 1 mod / 0 rem · in 12s", BackupRunStatus.Success, Now.AddHours(-3), Now).Label);
        Assert.Equal("3 h ago · 1,204 files changed",
            LastRunLine.For("812.5 KB uploaded · 4 new / 1200 mod / 0 rem · in 12s", BackupRunStatus.Success, Now.AddHours(-3), Now).Label);
        // From 1 MB the amount is shown, as before; the hover keeps the bytes.
        Assert.Equal("3 h ago · 1.2 MB uploaded",
            LastRunLine.For("1.2 MB uploaded · 0 new / 3 mod / 0 rem · in 12s", BackupRunStatus.Success, Now.AddHours(-3), Now).Label);
        Assert.Contains("293 B of new data uploaded",
            LastRunLine.For("293 B uploaded · 0 new / 0 mod / 0 rem · in 12s", BackupRunStatus.Success, Now.AddHours(-3), Now).Hover);
        // Without counts (an older summary) the amount stays.
        Assert.Equal("3 h ago · 293 B uploaded",
            LastRunLine.For("293 B uploaded · in 12s", BackupRunStatus.Success, Now.AddHours(-3), Now).Label);
    }

    [Fact]
    public void Ages_and_durations_use_the_same_units_with_a_space()
    {
        Assert.Equal("just now", HumanFormat.Ago(Now.AddSeconds(-30), Now));
        Assert.Equal("5 min ago", HumanFormat.Ago(Now.AddMinutes(-5), Now));
        Assert.Equal("3 h ago", HumanFormat.Ago(Now.AddHours(-3), Now));
        Assert.Equal("2 d ago", HumanFormat.Ago(Now.AddDays(-2), Now));
        Assert.Equal("45 s", HumanFormat.Duration(TimeSpan.FromSeconds(45)));
        Assert.Equal("4 min 05 s", HumanFormat.Duration(TimeSpan.FromSeconds(245)));
        Assert.Equal("1 h 05 min", HumanFormat.Duration(TimeSpan.FromMinutes(65)));
    }

    [Fact]
    public void Nothing_uploaded_says_so()
        => Assert.Equal("Just now · nothing new to upload", LastRunLine.For("Completed in 6m 10s", BackupRunStatus.Success, Now.AddSeconds(-20), Now).Label);
}
