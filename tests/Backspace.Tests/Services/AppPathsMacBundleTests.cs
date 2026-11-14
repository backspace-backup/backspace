using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U245: on macOS the program runs inside Backspace.app; its
/// portable folder belongs beside the app, never inside the bundle. An app
/// macOS runs from a read-only copy (App Translocation) is recognised.</summary>
public sealed class AppPathsMacBundleTests
{
    private static string Slashes(string path) => path.Replace('\\', '/');

    [Theory]
    [InlineData("/Applications/Backspace.app/Contents/MacOS", "/Applications")]
    [InlineData("/Users/me/Backups/Backspace.app/Contents/MacOS/", "/Users/me/Backups")]
    [InlineData("/Users/me/Backups", "/Users/me/Backups")]
    [InlineData("/opt/backspace", "/opt/backspace")]
    // Not a bundle: a folder merely named MacOS, or Contents/MacOS outside an .app.
    [InlineData("/Users/me/MacOS", "/Users/me/MacOS")]
    [InlineData("/Users/me/Stuff/Contents/MacOS", "/Users/me/Stuff/Contents/MacOS")]
    public void The_portable_folder_sits_beside_the_app_bundle(string directory, string expected) =>
        Assert.Equal(expected, Slashes(AppPaths.OutsideAppBundle(directory)).TrimEnd('/'));

    [Fact]
    public void An_app_run_from_a_read_only_copy_is_recognised()
    {
        Assert.True(AppPaths.IsTranslocated("/private/var/folders/xy/abc/T/AppTranslocation/0A1B-2C3D/d"));
        Assert.False(AppPaths.IsTranslocated("/Applications"));
        Assert.False(AppPaths.IsTranslocated("/Users/me/Downloads"));
    }
}
