using System.Reflection;
using System.Runtime.Versioning;
using Backspace.Services.WebDav;
using Backspace.Services.WinFsp;
using Fsp;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U167: WinFsp.Net does not expose the time a request may wait for
/// its answer; Backspace raises it through the volume parameters. Fails if a
/// WinFsp.Net update renames the field (the mount would then keep WinFsp's
/// 5-minute default and log a warning).</summary>
[SupportedOSPlatform("windows")]
public sealed class WinFspReadTimeoutTests
{
    [Fact]
    public void The_read_timeout_is_raised_to_WinFsps_maximum()
    {
        if (!WinFspRuntimeDetector.IsInstalled() || !WinFspNativeLoader.EnsureRegistered())
            Assert.Skip("WinFsp is not installed on this machine.");
        var host = new FileSystemHost(new WinFspSnapshotFs(new DirectorySnapshotTreeProvider(System.IO.Path.GetTempPath())));
        try
        {
            var field = typeof(FileSystemHost).GetField("_VolumeParams", BindingFlags.Instance | BindingFlags.NonPublic)!;
            uint Timeout() => (uint)field.FieldType.GetField("IrpTimeout", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(field.GetValue(host))!;
            Assert.NotEqual(600_000u, Timeout());
            Assert.True(WinFspSnapshotFs.RaiseIrpTimeout(host));
            Assert.Equal(600_000u, Timeout());
        }
        finally { host.Dispose(); }
    }
}
