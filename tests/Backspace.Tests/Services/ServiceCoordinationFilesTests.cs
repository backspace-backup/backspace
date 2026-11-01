using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Threading.Tasks;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceCoordinationFilesTests
{
    private const string Owner = "S-1-5-21-101-202-303-1001";
    private const string Service = "S-1-5-80-101-202-303-404-505";
    private const string Base = "O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)";

    [Fact]
    public void Owner_and_service_may_share_data_access_without_replacing_the_lock()
        => Check(Base + $"(A;;FRFW;;;{Owner})(A;;FRFW;;;{Service})");

    [Theory]
    [InlineData("SD")]
    [InlineData("WD")]
    [InlineData("WO")]
    [InlineData("FA")]
    [InlineData("GA")]
    public void Owner_and_service_cannot_replace_or_repermission_the_lock(string rights)
    {
        Assert.Throws<SecurityException>(() => Check(Base + $"(A;;{rights};;;{Owner})"));
        Assert.Throws<SecurityException>(() => Check(Base + $"(A;;{rights};;;{Service})"));
    }

    [Fact]
    public void Unrelated_users_cannot_write_the_lock_or_own_it()
    {
        Assert.Throws<SecurityException>(() => Check(Base + "(A;;FW;;;BU)"));
        Assert.Throws<SecurityException>(() => Check($"O:{Owner}G:BAD:P(A;;FA;;;BA)"));
        Assert.Throws<SecurityException>(() => Check("O:BAG:BAD:NO_ACCESS_CONTROL"));
    }

    [Fact]
    public void Preparation_waits_for_existing_desktop_operations_and_releases_after_dispose()
    {
        using var ws = new TempWorkspace();
        var root = ws.Sub("portable"); Directory.CreateDirectory(ServicePortableLayout.Config(root));
        var path = Path.Combine(ServicePortableLayout.Config(root), "operations.lock");
        File.WriteAllText(path, "unchanged");
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => ServiceCoordinationFiles.HoldOriginalOperations(root));
        using (ServiceCoordinationFiles.HoldOriginalOperations(root))
            Assert.Throws<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
        Assert.Equal("unchanged", File.ReadAllText(path));
    }

    [Fact]
    public void Preparation_does_not_lock_a_hard_link_to_unrelated_data()
    {
        using var ws = new TempWorkspace();
        var root = ws.Sub("portable"); Directory.CreateDirectory(ServicePortableLayout.Config(root));
        var unrelated = Path.Combine(ws.Root, "unrelated.txt"); File.WriteAllText(unrelated, "unchanged");
        Assert.True(CreateHardLinkW(Path.Combine(ServicePortableLayout.Config(root), "operations.lock"), unrelated, IntPtr.Zero));
        Assert.Throws<SecurityException>(() => ServiceCoordinationFiles.HoldOriginalOperations(root));
        Assert.Equal("unchanged", File.ReadAllText(unrelated));
    }

    [Fact]
    public void A_separate_review_configuration_never_joins_an_installations_service_locks()
    {
        using var ws = new TempWorkspace();
        var root = ws.Sub("portable");
        Directory.CreateDirectory(ServicePortableLayout.Control(root));
        File.WriteAllText(ServicePortableLayout.Record(root, ServiceWorkerBinding.FileName), "invalid binding must not be read");
        Assert.Null(ServiceCoordinationFiles.Resolve(root, ws.Sub("isolated-review")));
    }

    [Fact]
    public async Task Ordinary_portable_gate_remains_compatible_and_can_release_from_another_thread()
    {
        using var ws = new TempWorkspace();
        var name = @"Local\Backspace-coordination-test-" + Guid.NewGuid().ToString("N");
        using var legacy = new System.Threading.Semaphore(1, 1, name);
        using var gate = new CrossProcessGate(name);
        Assert.True(legacy.WaitOne(0)); Assert.False(gate.TryAcquire()); legacy.Release();
        Assert.True(gate.TryAcquire()); Assert.False(legacy.WaitOne(0));
        await Task.Run(gate.Release);
        Assert.True(legacy.WaitOne(0)); legacy.Release();
    }

    private static void Check(string value) => ServiceCoordinationFiles.ValidateDescriptor(new RawSecurityDescriptor(value), Owner, Service);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string path, string existing, IntPtr security);
}
