using System;
using System.IO;
using System.Security.Cryptography;
using Avalonia.Headless.XUnit;
using Backspace.Services;
using Backspace.Services.WinFsp;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public class EmbeddedAssetCacheTests : IDisposable
{
    private readonly TempWorkspace _workspace = new("embedded-cache");
    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void Equal_length_stale_copy_is_replaced_but_matching_copy_is_untouched()
    {
        var target = Path.Combine(_workspace.ConfigRoot, "dependency.bin");
        File.WriteAllBytes(target, [1, 2, 3]);
        using var resource = new MemoryStream([4, 5, 6]);
        Assert.True(EmbeddedAssetCache.WriteIfDifferent(resource, target));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(target));
        var earlier = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(target, earlier);
        resource.Position = 0;
        Assert.False(EmbeddedAssetCache.WriteIfDifferent(resource, target));
        Assert.Equal(earlier, File.GetLastWriteTimeUtc(target));
        File.Delete(target);
        resource.Position = 0;
        Assert.True(EmbeddedAssetCache.WriteIfDifferent(resource, target));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(target));
    }

    [Fact]
    public void Locked_target_survives_failed_replacement_without_temporary_leftovers()
    {
        var target = Path.Combine(_workspace.ConfigRoot, "locked.bin");
        File.WriteAllBytes(target, [1, 2, 3]);
        using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var resource = new MemoryStream([4, 5, 6]))
        {
            var error = Record.Exception(() => EmbeddedAssetCache.WriteIfDifferent(resource, target));
            Assert.True(error is IOException or UnauthorizedAccessException,
                $"Expected Windows to reject replacement of the locked file, got {error?.GetType().Name ?? "success"}.");
        }
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(target));
        Assert.Empty(Directory.GetFiles(_workspace.ConfigRoot, "*.tmp"));
    }

    [Theory]
    [InlineData("winfsp-msil.dll")]
    [InlineData("winfsp.msi")]
    public void Real_WinFsp_embedded_files_are_recreated_and_repaired(string name)
    {
        Directory.CreateDirectory(WinFspNativeLoader.ExtractRoot);
        var target = Path.Combine(WinFspNativeLoader.ExtractRoot, name);
        using var embedded = typeof(WinFspNativeLoader).Assembly.GetManifestResourceStream(name);
        Assert.NotNull(embedded);
        var expected = SHA256.HashData(embedded);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            WinFspNativeLoader.ExtractEmbeddedResource(name, target);
            using (var actual = File.OpenRead(target)) Assert.Equal(expected, SHA256.HashData(actual));
            if (attempt == 0)
            {
                // Same length, wrong bytes: a size-only freshness check misses this.
                var bytes = File.ReadAllBytes(target);
                bytes[0] ^= 0xff;
                File.WriteAllBytes(target, bytes);
            }
            else File.Delete(target);
        }
    }
}
