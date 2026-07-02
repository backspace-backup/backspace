using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Pins <see cref="AppPaths.Sanitize"/> behaviour. The function is the
/// single source of truth for converting a free-form user name into a
/// filesystem-safe slug for log dirs, repo dirs, scheduled-task names.
/// Drift here breaks all of those.
/// </summary>
public class AppPathsSanitizeTests
{
    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("Con")]
    [InlineData("PRN")]
    [InlineData("NUL")]
    [InlineData("AUX")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    public void Sanitize_remapsWindowsReservedNames(string reserved)
    {
        var slug = AppPaths.Sanitize(reserved);
        Assert.NotEqual(reserved, slug);
        Assert.Contains("_", slug);
    }

    [Theory]
    [InlineData("CON.log")]
    [InlineData("PRN.txt")]
    [InlineData("NUL.bak")]
    public void Sanitize_remapsReservedNamesEvenWithExtension(string reservedWithExt)
    {
        // Windows rejects the basename match regardless of extension —
        // CON.log fails to create just like CON.
        var slug = AppPaths.Sanitize(reservedWithExt);
        Assert.DoesNotMatch("^(CON|PRN|NUL|AUX|COM\\d|LPT\\d)\\.", slug);
    }

    [Theory]
    [InlineData("CONFIG")]
    [InlineData("PRINTER")]
    [InlineData("NULL")]
    [InlineData("AUDIO")]
    public void Sanitize_leavesNonReservedNamesAlone(string normalName)
    {
        // Embedded reserved tokens are fine — only the bare reserved
        // name + first-component-of-extension match should be remapped.
        Assert.Equal(normalName, AppPaths.Sanitize(normalName));
    }

    [Theory]
    [InlineData("name.")]
    [InlineData("name ")]
    [InlineData("name. ")]
    public void Sanitize_stripsTrailingDotsAndSpaces(string input)
    {
        // Windows silently strips trailing dots / spaces during path
        // resolution, which would otherwise let "name" and "name." collide.
        var slug = AppPaths.Sanitize(input);
        Assert.Equal("name", slug);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData(" ")]
    [InlineData("...")]
    public void Sanitize_emptyOrAllStrippedReturnsUnderscore(string input)
    {
        Assert.Equal("_", AppPaths.Sanitize(input));
    }

    [Fact]
    public void Sanitize_replacesInvalidCharsWithUnderscores()
    {
        // The pre-existing contract: any char in Path.GetInvalidFileNameChars
        // becomes '_'. Pin a representative subset so a future change
        // that swaps the strategy fails loudly.
        var slug = AppPaths.Sanitize("my/backup:name?");
        Assert.DoesNotContain("/", slug);
        Assert.DoesNotContain(":", slug);
        Assert.DoesNotContain("?", slug);
        Assert.Contains("_", slug);
    }

    [Fact]
    public void Sanitize_preservesValidNamesUnchanged()
    {
        Assert.Equal("documents", AppPaths.Sanitize("documents"));
        Assert.Equal("my-backup", AppPaths.Sanitize("my-backup"));
        Assert.Equal("my_backup", AppPaths.Sanitize("my_backup"));
        Assert.Equal("BackupOf2026", AppPaths.Sanitize("BackupOf2026"));
    }
}
