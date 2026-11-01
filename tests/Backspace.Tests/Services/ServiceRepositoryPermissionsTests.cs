using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Backspace.Models;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceRepositoryPermissionsTests
{
    private static readonly SecurityIdentifier Service = new("S-1-5-80-101-202-303-404-505");

    [Fact]
    public void Grant_and_removal_preserve_ownership_contents_and_other_accounts()
    {
        using var ws = new TempWorkspace();
        var user = ws.OrdinaryFolder("user");
        var root = Directory.CreateDirectory(Path.Combine(user, "repository")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(root, "keys")).FullName;
        var file = Path.Combine(nested, "key"); File.WriteAllText(file, "unchanged encrypted content");
        var sibling = Directory.CreateDirectory(Path.Combine(user, "unrelated")).FullName; var unrelated = ServiceSetup.ReadPermissions(sibling);
        var paths = new[] { root, nested, file };
        var original = paths.Select(ServiceSetup.ReadPermissions).ToArray();
        using (var held = new ServiceObjectPermissions(root))
        {
            ServiceRepositoryPermissions.ChangeTree(root, held, Service, grant: true, 0);
            for (var i = 0; i < paths.Length; i++)
            {
                var after = ServiceSetup.ReadPermissions(paths[i]);
                Assert.Contains(Service.Value, after);
                Assert.Equal(new RawSecurityDescriptor(original[i]).Owner, new RawSecurityDescriptor(after).Owner);
                Assert.Equal(WithoutService(original[i]), WithoutService(after));
            }
            var future = Path.Combine(nested, "future"); File.WriteAllText(future, "future data");
            Assert.Contains(Service.Value, ServiceSetup.ReadPermissions(future));
            ServiceRepositoryPermissions.ChangeTree(root, held, Service, grant: false, 0);
            Assert.DoesNotContain(Service.Value, ServiceSetup.ReadPermissions(future));
        }
        // Windows marks a rewritten list auto-inherited (AI); its entries are restored exactly.
        for (var i = 0; i < paths.Length; i++) Assert.Equal(ServiceSetupTests.WithoutAutoInheritFlag(original[i]), ServiceSetupTests.WithoutAutoInheritFlag(ServiceSetup.ReadPermissions(paths[i])));
        Assert.Equal(unrelated, ServiceSetup.ReadPermissions(sibling));
        Assert.Equal("unchanged encrypted content", File.ReadAllText(file));
    }

    [Fact]
    public void A_hard_link_cannot_extend_the_grant_to_an_unrelated_file()
    {
        using var ws = new TempWorkspace();
        var root = ws.Sub("repository"); var outside = Path.Combine(ws.Root, "unrelated.bin"); File.WriteAllText(outside, "keep");
        var before = ServiceSetup.ReadPermissions(outside);
        Assert.True(CreateHardLinkW(Path.Combine(root, "aliased-pack"), outside, IntPtr.Zero));
        using var held = new ServiceObjectPermissions(root);
        Assert.Throws<SecurityException>(() => ServiceRepositoryPermissions.ChangeTree(root, held, Service, grant: true, 0));
        Assert.Equal(before, ServiceSetup.ReadPermissions(outside));
        Assert.Equal("keep", File.ReadAllText(outside));
    }

    [Fact]
    public void A_junction_cannot_extend_the_grant_to_an_unrelated_directory()
    {
        using var ws = new TempWorkspace();
        var root = ws.Sub("repository"); var outside = ws.Sub("unrelated");
        var sentinel = Path.Combine(outside, "keep.txt"); File.WriteAllText(sentinel, "keep");
        var before = ServiceSetup.ReadPermissions(outside); var fileBefore = ServiceSetup.ReadPermissions(sentinel);
        var link = Path.Combine(root, "redirected");
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path " + Quote(link) + " -Target " + Quote(outside) + " | Out-Null");
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(10000)); Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        try
        {
            // Windows can briefly retain a sharing lock after the junction
            // creation process exits. Wait only for fixture preparation; the
            // actual grant must still reject the junction, not merely hit it busy.
            var prepared = Stopwatch.StartNew();
            ServiceObjectPermissions held;
            while (true)
            {
                try { held = new ServiceObjectPermissions(root); break; }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 32 or 33 && prepared.Elapsed < TimeSpan.FromSeconds(2))
                { System.Threading.Thread.Sleep(25); }
            }
            using var rootHandle = held;
            Assert.Throws<SecurityException>(() => ServiceRepositoryPermissions.ChangeTree(root, held, Service, grant: true, 0));
            Assert.Equal(before, ServiceSetup.ReadPermissions(outside));
            Assert.Equal(fileBefore, ServiceSetup.ReadPermissions(sentinel));
            Assert.Equal("keep", File.ReadAllText(sentinel));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void Broad_destination_roots_and_nonservice_identities_are_rejected()
    {
        const string owner = "O:BAG:BAD:P(A;;FA;;;BA)";
        Assert.Throws<SecurityException>(() => ServiceRepositoryPermissions.ChangeDescriptor(owner,
            new SecurityIdentifier(WellKnownSidType.WorldSid, null), true, true));
        var destination = new Destination { Id = "destination", Kind = DestinationKind.LocalFolder, PathOrSubpath = @"C:\" };
        var backup = new Backup { Id = "backup", SourcePaths = new() { @"C:\Docs" }, Targets = new() { new() { DestinationId = destination.Id } } };
        var config = new AppConfig { Backups = new() { backup }, Destinations = new() { destination } };
        Assert.Throws<SecurityException>(() => ServiceRepositoryPermissions.RepositoryRoots(@"C:\Backspace", config).ToArray());
        destination.PathOrSubpath = @"C:\Docs";
        Assert.Throws<SecurityException>(() => ServiceRepositoryPermissions.RepositoryRoots(@"C:\Backspace", config).ToArray());
        destination.PathOrSubpath = @"D:\Backups";
        Assert.Equal(new[] { @"D:\Backups" }, ServiceRepositoryPermissions.RepositoryRoots(@"C:\Backspace", config));
    }

    private static string WithoutService(string sddl)
    {
        var descriptor = new RawSecurityDescriptor(sddl);
        var acl = descriptor.DiscretionaryAcl!;
        for (var i = acl.Count - 1; i >= 0; i--)
            if (acl[i] is KnownAce ace && ace.SecurityIdentifier == Service) acl.RemoveAce(i);
        return ServiceSetupTests.WithoutAutoInheritFlag(descriptor.GetSddlForm(AccessControlSections.Owner | AccessControlSections.Access));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLinkW(string alias, string target, IntPtr attributes);
}
