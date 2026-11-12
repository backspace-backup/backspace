using System;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.ViewModels;

/// <summary>U203 (owner, 12 November 2026): "In the Backup health it
/// shouldn't say 'before cleanup and checks'. Please be more consistent, it
/// should show the reason on hover like what we did for Last backup."</summary>
public sealed class QueuedCheckLabelTests
{
    [AvaloniaFact]
    public void A_queued_check_says_so_briefly_and_gives_the_reason_on_hover()
    {
        using var card = new BackupCardViewModel(new Backup { Name = "local_d" }, true, false);
        card.RunningOperation = "check";
        card.IsRunning = true;
        card.RunningActivity = "Data check (1 GiB sample)";
        card.SetQueuedReason("waiting for local_c (same destination) before cleanup and checks");
        Assert.Equal("Data check · queued", card.CheckRunLabel);
        Assert.DoesNotContain("before cleanup", card.CheckRunLabel);
        Assert.Contains("Queued: waiting for local_c (same destination) before cleanup and checks", card.CheckTooltip);
        // Once it starts: its name and time, no reason left behind.
        card.SetQueuedReason("");
        Assert.DoesNotContain("queued", card.CheckRunLabel);
        Assert.DoesNotContain("Queued:", card.CheckTooltip);
    }
}
