using System;
using System.IO;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Backspace.Services.Platform.Windows;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceInstallationSecurityTests
{
    private static bool Trusted(SecurityIdentifier sid) => sid.Value is "S-1-5-18" or "S-1-5-32-544";
    private static void Check(string sddl, bool content = true) =>
        ServiceInstallationSecurity.ValidateDescriptor(new RawSecurityDescriptor(sddl), content, Trusted);

    [Fact]
    public void ProtectedCodeAllowsReadAndExecuteButNotOrdinaryWrites()
        => Check("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFX;;;BU)");

    [Theory]
    [InlineData("FR")]
    [InlineData("GR")]
    [InlineData("FX")]
    public void PrivateDataRejectsReadAndTraversalGrantsToOtherAccounts(string mask)
        => Assert.Throws<SecurityException>(() => ServiceInstallationSecurity.ValidateDescriptor(
            new RawSecurityDescriptor($"O:BAG:BAD:P(A;;FA;;;BA)(A;;{mask};;;BU)"), true, Trusted, confidential: true));

    [Theory]
    [InlineData("O:BUG:BUD:P(A;;FA;;;SY)(A;;FR;;;BU)")]
    [InlineData("O:BAG:BAD:NO_ACCESS_CONTROL")]
    [InlineData("O:BAG:BAD:P(A;;FW;;;BU)")]
    [InlineData("O:BAG:BAD:P(A;;GW;;;BU)")]
    [InlineData("O:BAG:BAD:P(A;;GA;;;BU)")]
    [InlineData("O:BAG:BAD:P(A;;0x2;;;BU)")]
    [InlineData("O:BAG:BAD:P(A;;0x4;;;BU)")]
    [InlineData("O:BAG:BAD:P(A;;0x40;;;BU)")]
    [InlineData("O:BAG:BAD:P(A;;SD;;;BU)")]
    [InlineData("O:BAG:BAD:P(A;;WD;;;BU)")]
    [InlineData("O:BAG:BAD:P(A;;WO;;;BU)")]
    [InlineData("O:BAG:BAD:P(D;;FW;;;BU)(A;;FA;;;BU)")]
    public void UnsafeCodeDescriptorsAreRejected(string descriptor)
        => Assert.Throws<SecurityException>(() => Check(descriptor));

    [Fact]
    public void CreatingAnUnrelatedSiblingDoesNotAllowReplacingProtectedChild()
        => Check("O:BAG:BAD:P(A;;FA;;;BA)(A;;0x6;;;BU)", content: false);

    [Theory]
    [InlineData("0x40")]
    [InlineData("SD")]
    [InlineData("WD")]
    [InlineData("WO")]
    [InlineData("GA")]
    public void AncestorReplacementRightsAreRejected(string mask)
        => Assert.Throws<SecurityException>(() => Check($"O:BAG:BAD:P(A;;{mask};;;BU)", content: false));

    [Fact]
    public void InheritOnlyRuleDoesNotGrantAccessToTheAncestorItself()
        => Check("O:BAG:BAD:P(A;CIIO;GA;;;CO)(A;;FA;;;BA)", content: false);

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
    // These are syntax checks only. Actual code loading separately validates
    // ownership and permissions; a folder's name does not establish safety.
    public void CanonicalLocalSyntaxIsAcceptedRegardlessOfFolderName(string path)
        => Assert.Equal(path, ServiceInstallationSecurity.CanonicalLocalPath(path));

    [Fact]
    public void A_folder_inside_a_shared_writable_parent_is_accepted()
    {
        // The owner chooses where Backspace lives (27 October 2026). A parent
        // like C:\Users\Public\Downloads, where every account may rename and
        // delete children, must not block setup.
        using var workspace = new Backspace.Tests.TestHelpers.TempWorkspace("service-shared-parent");
        var shared = workspace.Sub("Public-Downloads");
        var security = new System.IO.DirectoryInfo(shared).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Modify | FileSystemRights.DeleteSubdirectoriesAndFiles,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new System.IO.DirectoryInfo(shared).SetAccessControl(security);
        var portable = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(shared, "Backspace")).FullName;
        ServiceInstallationSecurity.ValidateServiceLocation(portable);
        // Only the service's own windows-service folder is checked: the copy
        // and Backspace.config keep whatever permissions the owner gave them.
        ServiceInstallationSecurity.ValidateAncestors(portable, portable);
        ServiceInstallationSecurity.ValidateAncestors(shared, portable);
        var control = Directory.CreateDirectory(Path.Combine(portable, "Backspace.config", "windows-service", "control")).FullName;
        Assert.Throws<SecurityException>(() => ServiceInstallationSecurity.ValidateAncestors(control, portable));
    }

    [Fact]
    public void A_drive_root_is_accepted()
        => ServiceInstallationSecurity.ValidateServiceLocation(@"C:\");

    [Fact]
    public void WritableDataCannotReplaceTheEntireCodeTree()
        => Assert.Throws<SecurityException>(() => ServiceInstallationSecurity.ValidateCodeTree(
            @"C:\Portable", @"C:\Portable", "S-1-5-80-100-200-300-400-500"));

    [Fact]
    public void ExcludingADataSubfolderStillRequiresItsPrivateSecurityBoundary()
    {
        using var workspace = new Backspace.Tests.TestHelpers.TempWorkspace("service-data-boundary");
        // A caller cannot hide an ordinary user-writable folder from the code
        // inspection merely by naming it as the data directory.
        Assert.Throws<SecurityException>(() => ServiceInstallationSecurity.ValidateCodeTree(
            workspace.Root, workspace.ConfigRoot, "S-1-5-80-100-200-300-400-500"));
    }
}
