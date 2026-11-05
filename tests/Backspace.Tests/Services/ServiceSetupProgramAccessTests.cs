using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U077: a portable copy in a folder only its owner may read (under the user
/// profile, such as Dropbox) could not be started by the background service:
/// "Cannot start service … Access is denied". Setup now grants the service's
/// account read and execute on the folder and its program files, adding to the
/// existing permissions.
/// </summary>
public sealed class ServiceSetupProgramAccessTests
{
    private const string ServiceSid = "S-1-5-80-1-2-3-4-5";
    private const string OwnerOnly = "O:S-1-5-21-1-2-3-1001D:PAI(A;OICI;FA;;;S-1-5-21-1-2-3-1001)(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";

    private static RawAcl Dacl(string sddl) => new RawSecurityDescriptor(sddl).DiscretionaryAcl!;

    [Fact]
    public void An_owner_only_folder_gains_read_and_execute_for_the_service_and_keeps_its_entries()
    {
        var result = ServiceSetup.WithServiceRead(OwnerOnly, ServiceSid, directory: true);
        var aces = Dacl(result).Cast<CommonAce>().ToList();
        var service = new SecurityIdentifier(ServiceSid);
        // This folder itself (the entry may be merged with the one below).
        Assert.Contains(aces, a => a.SecurityIdentifier == service && a.AceQualifier == AceQualifier.AccessAllowed
            && a.AccessMask == 0x1200a9 && (a.PropagationFlags & PropagationFlags.InheritOnly) == 0);
        // Files placed directly in it later (an updated program), not subfolders.
        Assert.Contains(aces, a => a.SecurityIdentifier == service && a.InheritanceFlags == InheritanceFlags.ObjectInherit
            && (a.PropagationFlags & PropagationFlags.NoPropagateInherit) != 0);
        Assert.DoesNotContain(aces, a => a.SecurityIdentifier == service && (a.InheritanceFlags & InheritanceFlags.ContainerInherit) != 0);
        // Never write access, and the owner's entries are untouched.
        Assert.DoesNotContain(aces, a => a.SecurityIdentifier == service && (a.AccessMask & 0x2) != 0);
        foreach (var original in Dacl(OwnerOnly).Cast<CommonAce>())
            Assert.Contains(aces, a => a.SecurityIdentifier == original.SecurityIdentifier && a.AccessMask == original.AccessMask);
        Assert.Equal(new RawSecurityDescriptor(OwnerOnly).Owner, new RawSecurityDescriptor(result).Owner);
    }

    [Fact]
    public void A_program_file_gains_read_and_execute_only()
    {
        var result = ServiceSetup.WithServiceRead("O:BAD:(A;;FA;;;BA)(A;;FA;;;SY)", ServiceSid, directory: false);
        var service = Dacl(result).Cast<CommonAce>().Where(a => a.SecurityIdentifier.Value == ServiceSid).ToList();
        Assert.Single(service);
        Assert.Equal(0x1200a9, service[0].AccessMask);
    }

    [Fact]
    public void The_settings_folder_gains_access_to_itself_only()
    {
        var result = ServiceSetup.WithServiceRead(OwnerOnly, ServiceSid, directory: true, inheritToFiles: false);
        var service = Dacl(result).Cast<CommonAce>().Where(a => a.SecurityIdentifier.Value == ServiceSid).ToList();
        Assert.Single(service);
        Assert.Equal(InheritanceFlags.None, service[0].InheritanceFlags);
    }

    [Fact]
    public void Applied_to_a_real_owner_only_folder_the_grant_is_readable_back()
    {
        using var ws = new TempWorkspace();
        var folder = ws.Sub("Portable");
        using var pin = new ServiceObjectPermissions(folder, workingFolder: true);
        var before = pin.Read();
        pin.SetDacl(ServiceSetup.WithServiceRead(before, ServiceSid, directory: true));
        var after = new DirectoryInfo(folder).GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Where(r => r.IdentityReference.Value == ServiceSid).ToList();
        Assert.Contains(after, r => r.FileSystemRights.HasFlag(FileSystemRights.ReadAndExecute) && r.AccessControlType == AccessControlType.Allow);
        Assert.DoesNotContain(after, r => r.FileSystemRights.HasFlag(FileSystemRights.WriteData));
        pin.SetDacl(before);
    }
    [Fact]
    public void A_record_held_briefly_by_a_reader_is_replaced_after_it_is_released()
    {
        // The Backspace window or the service reading the record: Windows says
        // "Access is denied" until it lets go (U094).
        var attempts = 0;
        ServiceSetup.ReplaceRetrying(() =>
        {
            if (++attempts < 3) throw new UnauthorizedAccessException("Access to the path is denied.") { HResult = unchecked((int)0x80070005) };
        });
        Assert.Equal(3, attempts);
        // A lasting lock is waited out until the operation is stopped (U135),
        // and other errors are never retried.
        using (var stop = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(600)))
            Assert.ThrowsAny<OperationCanceledException>(() => ServiceSetup.ReplaceRetrying(
                () => throw new UnauthorizedAccessException("Access to the path is denied.") { HResult = unchecked((int)0x80070005) }, null, stop.Token));
        var other = 0;
        Assert.Throws<FileNotFoundException>(() => ServiceSetup.ReplaceRetrying(() => { other++; throw new FileNotFoundException(); }));
        Assert.Equal(1, other);
    }
}
