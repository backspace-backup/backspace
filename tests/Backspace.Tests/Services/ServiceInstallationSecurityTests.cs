using System.Security;
using Backspace.Services.Platform.Windows;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceInstallationSecurityTests
{
    [Theory]
    [InlineData(@"relative\service.exe")]
    [InlineData(@"C:service.exe")]
    [InlineData(@"\\server\share\service.exe")]
    [InlineData(@"\\?\C:\protected\service.exe")]
    [InlineData(@"C:\protected\..\other\service.exe")]
    [InlineData(@"C:\protected\service.exe:stream")]
    [InlineData(@"C:\protected.\service.exe")]
    [InlineData(@"C:\protected \service.exe")]
    [InlineData(@"C:\protected/service.exe")]
    public void PathAliasesAndRemoteLocationsAreRejected(string path)
        => Assert.Throws<SecurityException>(() => ServiceInstallationSecurity.CanonicalLocalPath(path));

    [Theory]
    [InlineData(@"C:\Program Files\Backspace\service.exe")]
    [InlineData(@"D:\Tools\My Backup App\service.exe")]
    [InlineData(@"C:\Users\Example\Downloads\Backspace\service.exe")]
    [InlineData(@"C:\")]
    // These are syntax checks only; a folder's name does not establish safety.
    public void CanonicalLocalSyntaxIsAcceptedRegardlessOfFolderName(string path)
        => Assert.Equal(path, ServiceInstallationSecurity.CanonicalLocalPath(path));

    [Fact]
    public void A_local_folder_is_accepted()
    {
        // The owner chooses where Backspace lives (27 October 2026).
        using var workspace = new Backspace.Tests.TestHelpers.TempWorkspace("service-location");
        var portable = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(workspace.Sub("Downloads"), "Backspace")).FullName;
        ServiceInstallationSecurity.ValidateServiceLocation(portable);
    }

    [Fact]
    public void A_drive_root_is_accepted()
        => ServiceInstallationSecurity.ValidateServiceLocation(@"C:\");
}
