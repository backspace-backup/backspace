using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services.WebDav;
using Backspace.Services.WinFsp;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class WinFspSnapshotFsTests
{
    [Fact]
    public async Task Concurrent_reads_on_one_handle_keep_their_requested_offsets()
    {
        if (!OperatingSystem.IsWindows()) return;
        var bytes = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        using var stream = new OverlappingSeekStream(bytes);
        var fs = new WinFspSnapshotFs(new StreamProvider(stream));
        Assert.Equal(0, fs.Open("\\content.bin", 0, 0, out var node, out var handle, out _, out _));
        try
        {
            var first = Task.Factory.StartNew(() => Read(0), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(stream.FirstSeek.Wait(TimeSpan.FromSeconds(5)));
            var second = Task.Factory.StartNew(() => Read(32), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.Equal(bytes.Take(8), await first);
            Assert.Equal(bytes.Skip(32).Take(8), await second);
        }
        finally { fs.Close(node!, handle!); }

        byte[] Read(ulong offset)
        {
            var buffer = Marshal.AllocHGlobal(8);
            try
            {
                Assert.Equal(0, fs.Read(node!, handle!, buffer, offset, 8, out var read));
                Assert.Equal(8u, read);
                var actual = new byte[8]; Marshal.Copy(buffer, actual, 0, 8); return actual;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    private sealed class OverlappingSeekStream(byte[] data) : MemoryStream(data)
    {
        private int _seeks;
        public ManualResetEventSlim FirstSeek { get; } = new();
        private readonly ManualResetEventSlim _secondSeek = new();
        public override long Position
        {
            get => base.Position;
            set
            {
                base.Position = value;
                if (Interlocked.Increment(ref _seeks) == 1)
                {
                    FirstSeek.Set();
                    // Without the per-handle read lock, the second request
                    // moves Position before the first request consumes bytes.
                    _secondSeek.Wait(TimeSpan.FromSeconds(2));
                }
                else _secondSeek.Set();
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { FirstSeek.Dispose(); _secondSeek.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private sealed class StreamProvider(Stream stream) : ISnapshotTreeProvider
    {
        public Task<IReadOnlyList<WebDavEntry>?> ListAsync(string path, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WebDavEntry>?>(Array.Empty<WebDavEntry>());
        public Task<WebDavEntry?> StatAsync(string path, CancellationToken ct) =>
            Task.FromResult<WebDavEntry?>(new WebDavEntry(path, false, stream.Length, DateTime.UtcNow));
        public Task<Stream> OpenAsync(string path, CancellationToken ct) => Task.FromResult(stream);
    }

    [Fact]
    public void Directory_requests_restart_and_resume_without_losing_an_unreturned_entry()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        var source = workspace.Sub("source");
        foreach (var name in new[] { "Alpha.txt", "Beta.txt", "Gamma.txt" })
            File.WriteAllText(Path.Combine(source, name), name);
        var fs = new WinFspSnapshotFs(new DirectorySnapshotTreeProvider(source));
        Assert.Equal(0, fs.Open("\\", 0, 0, out var node, out var handle, out _, out _));
        try
        {
            object? context = null;
            Assert.True(fs.ReadDirectoryEntry(node!, handle!, null, null, ref context, out var first, out _));
            Assert.Equal("Alpha.txt", first);
            // The base class fetches an entry before checking whether it fits
            // in Windows' buffer. Beta was fetched but NOT returned to Windows.
            Assert.True(fs.ReadDirectoryEntry(node!, handle!, null, null, ref context, out var unreturned, out _));
            Assert.Equal("Beta.txt", unreturned);
            context = null;
            Assert.Equal(new[] { "Beta.txt", "Gamma.txt" }, Read("Alpha.txt", ref context));
            // FindNext/FindFirst can restart on the same open directory handle.
            context = null;
            Assert.Equal(new[] { "Alpha.txt", "Beta.txt", "Gamma.txt" }, Read(null, ref context));
            context = null;
            Assert.Empty(Read("Gamma.txt", ref context));
        }
        finally { fs.Close(node!, handle!); }

        List<string> Read(string? marker, ref object? context)
        {
            var names = new List<string>();
            while (fs.ReadDirectoryEntry(node!, handle!, null, marker, ref context, out var name, out _))
                names.Add(name!);
            return names;
        }
    }
}
