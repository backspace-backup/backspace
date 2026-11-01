using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.ViewModels;

/// <summary>
/// Regression: user reported "Estimated savings: 0 B on this PC."
/// looked like the savings estimator was broken. The actual computation
/// was correct — the recommended exclusion patterns (Recycle.Bin,
/// pagefile, Windows/, ntuser.dat*) genuinely don't match anything in a
/// narrow source like C:\Users\&lt;them&gt;\Documents. The UX was the
/// bug: "0 B" reads as "broken", not "nothing to exclude". The new
/// label explicitly tells the user when no rule matched anything in
/// their sources.
/// </summary>
public class BackupEditorSavingsLabelTests
{
    [Fact]
    public void Pending_estimate_emits_empty_label()
    {
        using var vm = new BackupEditorViewModel(new Backspace.Models.Backup(), isNew: true);
        vm.RecEstimatedSavingsBytes = -1; // sentinel: not estimated yet
        Assert.Equal("", vm.RecEstimatedSavingsLabel);
    }

    [Fact]
    public void Estimating_emits_progress_label()
    {
        using var vm = new BackupEditorViewModel(new Backspace.Models.Backup(), isNew: true);
        vm.RecEstimatedSavingsBytes = -2;
        Assert.Contains("Estimating", vm.RecEstimatedSavingsLabel);
    }

    [Fact]
    public void Zero_savings_with_scanned_bytes_emits_friendly_no_match_label()
    {
        // The user's bug report: the rules don't match anything but the
        // walk DID find files in the source. Surface that distinction.
        using var vm = new BackupEditorViewModel(new Backspace.Models.Backup(), isNew: true);
        vm.RecEstimatedSavingsBytes = 0;
        vm.RecScannedBytes = 1_000_000_000; // 1 GB of source files seen
        var label = vm.RecEstimatedSavingsLabel;
        Assert.Contains("Nothing", label);
        Assert.Contains("matched", label);
        Assert.DoesNotContain("0 B", label);
    }

    [Fact]
    public void Zero_savings_with_zero_scanned_emits_check_sources_label()
    {
        // Genuinely empty: walk found no files (misconfigured source).
        // Distinct message so the user knows what to check.
        using var vm = new BackupEditorViewModel(new Backspace.Models.Backup(), isNew: true);
        vm.RecEstimatedSavingsBytes = 0;
        vm.RecScannedBytes = 0;
        var label = vm.RecEstimatedSavingsLabel;
        Assert.Contains("Couldn't read any files", label);
    }

    [Fact]
    public void Positive_savings_emits_bytes_label()
    {
        using var vm = new BackupEditorViewModel(new Backspace.Models.Backup(), isNew: true);
        vm.RecEstimatedSavingsBytes = 12L * 1024 * 1024 * 1024; // 12 GiB
        Assert.Contains("Estimated savings:", vm.RecEstimatedSavingsLabel);
        Assert.Contains("12", vm.RecEstimatedSavingsLabel);
    }

    [Fact]
    public void Zero_savings_with_truncated_walk_distinguishes_from_no_match()
    {
        // Adversarial regression: if the walk hit its time cap with 0
        // matches so far, we used to say "Nothing matched — that's
        // normal for narrow folders." But the source ISN'T narrow if
        // we ran out of time scanning it. Distinguish so the user
        // doesn't dismiss a result that may grow on a longer walk.
        using var vm = new BackupEditorViewModel(new Backspace.Models.Backup(), isNew: true);
        vm.RecEstimatedSavingsBytes = 0;
        vm.RecScannedBytes = 5_000_000_000;  // 5 GB walked so far
        vm.RecEstimatedTruncated = true;
        var label = vm.RecEstimatedSavingsLabel;
        Assert.Contains("time cap", label);
        Assert.DoesNotContain("narrow source folders", label);
    }
}
