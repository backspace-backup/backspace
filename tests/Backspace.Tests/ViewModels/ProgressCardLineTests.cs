using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.ViewModels;

/// <summary>U193 (11 November 2026): the card's running line keeps the files
/// checked, the changes backed up and the time left; the size, the data
/// uploaded and its speed stay in the hover.</summary>
public sealed class ProgressCardLineTests
{
    [Fact]
    public void The_owners_line_fits_one_line_and_keeps_what_moves()
        => Assert.Equal("122,087 of ~428,634 files checked · 1.2 of ~25 GB of changes backed up · about 9 min left",
            BackupCardViewModel.CardLine("Backing up · 122,087 of ~428,634 files checked (~164 GB) · 1.2 of ~25 GB changed files backed up · 181 MB of new data uploaded, 2.0 MB/s · about 9 min left"));

    [Theory]
    // Before the changes are known: the percentage and the size go, Scanning stays.
    [InlineData("12.5% · Scanning · 1,000 of 2,000+ files checked (3.0 GB)", "Scanning · 1,000 of 2,000+ files checked")]
    [InlineData("Backing up · 10 of 20 files checked (1 of 2 GB) · 5 MB of new data uploaded", "10 of 20 files checked")]
    [InlineData("Backing up · 600,001 of 600,001 files checked (176 GB) · no changed files", "600,001 of 600,001 files checked · no changed files")]
    [InlineData("", "")]
    public void Other_lines(string detail, string expected) => Assert.Equal(expected, BackupCardViewModel.CardLine(detail));
}
