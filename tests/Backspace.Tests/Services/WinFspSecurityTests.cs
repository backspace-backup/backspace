using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using Backspace.Services.WinFsp;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U152: a mounted snapshot is readable by the person who mounted it
/// and by Windows, not by every account on the computer.</summary>
public sealed class WinFspSecurityTests
{
    [Fact]
    public void Mounted_snapshot_grants_read_to_its_owner_and_system_only()
    {
        const string owner = "S-1-5-21-1000-2000-3000-1001";
        var sd = new RawSecurityDescriptor(WinFspSnapshotFs.Descriptor(owner), 0);
        var aces = sd.DiscretionaryAcl!.Cast<CommonAce>().ToList();
        Assert.Equal(new[] { owner, "S-1-5-18" }, aces.Select(a => a.SecurityIdentifier.Value));
        Assert.All(aces, a => Assert.Equal(AceQualifier.AccessAllowed, a.AceQualifier));
        const int write = 0x0002 | 0x0004 | 0x0010 | 0x0100 | 0x10000 | 0x40000 | 0x80000; // data, append, EA, attributes, delete, DACL, owner
        Assert.All(aces, a => Assert.Equal(0, a.AccessMask & write));
        Assert.DoesNotContain(aces, a => a.SecurityIdentifier.Value == "S-1-1-0"); // Everyone
        Assert.Equal(owner, sd.Owner!.Value);
        // The real descriptor names the account running the app.
        var current = new RawSecurityDescriptor(WinFspSnapshotFs.BuildOwnerReadDescriptor(), 0);
        Assert.Equal(WindowsIdentity.GetCurrent().User!.Value, current.Owner!.Value);
    }
}
