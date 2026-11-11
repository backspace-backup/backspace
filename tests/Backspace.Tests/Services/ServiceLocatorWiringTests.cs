using System.Runtime.Versioning;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Pins down a few service-locator wiring assumptions that are
/// otherwise invisible from the outside: when the consumer is the
/// XAML binding "{Binding CanMountSnapshot}", a missing registration
/// silently renders a dead button. These tests fail loud if the wiring
/// breaks.
/// </summary>
public class ServiceLocatorWiringTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void SnapshotMountRegistry_IsRegisteredOnWindows()
    {
        // The "Mount" button binds IsVisible to CanMountSnapshot which
        // checks ServiceLocator.SnapshotMounts is non-null. The Windows
        // TFM uses #if WINDOWS to gate the assignment — if that symbol
        // ever stops being auto-defined by the SDK, this test catches
        // it before the button silently disappears.
        if (!System.OperatingSystem.IsWindows()) return;
        Assert.NotNull(ServiceLocator.SnapshotMounts);
    }
}
