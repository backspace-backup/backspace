using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

public class SourceSizeProbeTests
{
    [Fact]
    public async Task GetOrComputeAsync_emptyDir_returnsZero()
    {
        using var tmp = new TempDir();
        var probe = new SourceSizeProbe();
        var r = await probe.GetOrComputeAsync(tmp.Path, CancellationToken.None);
        Assert.True(r.Completed);
        Assert.Equal(0, r.Bytes);
        Assert.Equal(0, r.FileCount);
    }

    [Fact]
    public async Task GetOrComputeAsync_sumsFileBytesAndCounts()
    {
        using var tmp = new TempDir();
        File.WriteAllBytes(Path.Combine(tmp.Path, "a.bin"), new byte[1234]);
        File.WriteAllBytes(Path.Combine(tmp.Path, "b.bin"), new byte[5678]);
        Directory.CreateDirectory(Path.Combine(tmp.Path, "sub"));
        File.WriteAllBytes(Path.Combine(tmp.Path, "sub", "c.bin"), new byte[42]);

        var probe = new SourceSizeProbe();
        var r = await probe.GetOrComputeAsync(tmp.Path, CancellationToken.None);
        Assert.True(r.Completed);
        Assert.Equal(1234L + 5678 + 42, r.Bytes);
        Assert.Equal(3, r.FileCount);
        Assert.False(r.Truncated);
    }

    [Fact]
    public async Task GetOrComputeAsync_cachesResult_secondCallNoWalk()
    {
        using var tmp = new TempDir();
        File.WriteAllBytes(Path.Combine(tmp.Path, "x"), new byte[100]);
        var probe = new SourceSizeProbe();
        var r1 = await probe.GetOrComputeAsync(tmp.Path, CancellationToken.None);
        // Add a file AFTER the first probe; cache should hide it.
        File.WriteAllBytes(Path.Combine(tmp.Path, "y"), new byte[100]);
        var r2 = await probe.GetOrComputeAsync(tmp.Path, CancellationToken.None);
        Assert.Equal(r1.Bytes, r2.Bytes);
        Assert.Equal(r1.FileCount, r2.FileCount);
    }

    [Fact]
    public async Task Invalidate_dropsCacheEntry()
    {
        using var tmp = new TempDir();
        File.WriteAllBytes(Path.Combine(tmp.Path, "x"), new byte[100]);
        var probe = new SourceSizeProbe();
        var r1 = await probe.GetOrComputeAsync(tmp.Path, CancellationToken.None);
        File.WriteAllBytes(Path.Combine(tmp.Path, "y"), new byte[100]);
        probe.Invalidate(tmp.Path);
        var r2 = await probe.GetOrComputeAsync(tmp.Path, CancellationToken.None);
        Assert.True(r2.Bytes > r1.Bytes, $"Expected re-probe to see new file. r1={r1.Bytes} r2={r2.Bytes}");
    }

    [Fact]
    public async Task GetOrComputeAsync_missingPath_returnsIncomplete()
    {
        var probe = new SourceSizeProbe();
        var r = await probe.GetOrComputeAsync(@"C:\definitely-not-a-real-path-" + System.Guid.NewGuid().ToString("N"),
            CancellationToken.None);
        Assert.False(r.Completed);
        Assert.Equal(0, r.Bytes);
    }

    [Fact]
    public async Task Hidden_and_system_files_are_counted_like_the_backup_counts_them()
    {
        using var tmp = new TempDir();
        var hidden = Path.Combine(tmp.Path, "hidden.bin");
        var system = Path.Combine(tmp.Path, "system.bin");
        File.WriteAllBytes(hidden, new byte[300]);
        File.WriteAllBytes(system, new byte[700]);
        File.SetAttributes(hidden, FileAttributes.Hidden);
        File.SetAttributes(system, FileAttributes.System);
        try
        {
            var r = await new SourceSizeProbe().GetOrComputeAsync(tmp.Path, CancellationToken.None);
            Assert.Equal(1000, r.Bytes);
            Assert.Equal(2, r.FileCount);
        }
        finally
        {
            File.SetAttributes(hidden, FileAttributes.Normal);
            File.SetAttributes(system, FileAttributes.Normal);
        }
    }

    // U106: a drive root used to be trimmed to "C:", which Windows reads
    // as the current folder on drive C, and the walk stopped after 15 s.
    [Fact]
    public void A_drive_root_is_read_as_the_drive_not_the_current_folder()
    {
        if (!OperatingSystem.IsWindows()) return;
        var drive = Path.GetPathRoot(Environment.SystemDirectory)!;
        Assert.Equal(drive, TreeWalk.Root(drive));
        Assert.True(TreeWalk.IsDriveRoot(drive));
        Assert.False(TreeWalk.IsDriveRoot(Environment.SystemDirectory));
        Assert.Contains(TreeWalk.List(TreeWalk.Root(drive)),
            e => e.IsDirectory && string.Equals(e.FullPath, Path.Combine(drive, "Windows"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal("pagefile.sys", TreeWalk.Relative(TreeWalk.Root(drive), Path.Combine(drive, "pagefile.sys")));
    }

    [Fact]
    public async Task A_drive_too_big_to_read_in_time_falls_back_to_the_space_used_on_it()
    {
        if (!OperatingSystem.IsWindows()) return;
        var drive = Path.GetPathRoot(Environment.SystemDirectory)!;
        var probe = new SourceSizeProbe { MaxScanDuration = TimeSpan.Zero };
        var r = await probe.GetOrComputeAsync(drive, CancellationToken.None);
        var info = new DriveInfo(drive);
        Assert.True(r.Truncated);
        Assert.True(r.Approximate);
        Assert.InRange(r.Bytes, (info.TotalSize - info.TotalFreeSpace) / 2, info.TotalSize);

        // A folder that runs out of time keeps its partial count: it is not a drive.
        using var tmp = new TempDir();
        File.WriteAllBytes(Path.Combine(tmp.Path, "a.bin"), new byte[10]);
        var folder = await probe.GetOrComputeAsync(tmp.Path, CancellationToken.None);
        Assert.True(folder.Truncated);
        Assert.False(folder.Approximate);
        Assert.True(folder.Bytes <= 10);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "backspace-sizeprobe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
