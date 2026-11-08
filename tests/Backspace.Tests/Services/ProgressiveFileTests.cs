using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.WebDav;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U167: a file copied out of the mounted drive downloads progressively. A
/// read waits only for its own range; every reader shares one download; a
/// failed or short download is never served as the complete file.
/// </summary>
public sealed class ProgressiveFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "backspace-progressive-" + Guid.NewGuid().ToString("N"));
    private string Partial => Path.Combine(_root, "partial");
    private string Final => Path.Combine(_root, "files", "dir", "film.bin");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static byte[] Bytes(int length)
    {
        var bytes = new byte[length];
        new Random(167).NextBytes(bytes);
        return bytes;
    }

    /// <summary>A download the test releases chunk by chunk.</summary>
    private sealed class Feed
    {
        private readonly SemaphoreSlim _go = new(0);
        public Exception? FailAfter { get; set; }
        public int Starts;
        public ProgressiveFile.Fill Fill(byte[] content, int chunk, int? stopAt = null) => async (output, ct) =>
        {
            Interlocked.Increment(ref Starts);
            var end = stopAt ?? content.Length;
            for (var at = 0; at < end; at += chunk)
            {
                await _go.WaitAsync(ct);
                await output.WriteAsync(content.AsMemory(at, Math.Min(chunk, end - at)), ct);
            }
            if (FailAfter is { } failure) throw failure;
        };
        public void Release(int chunks = 1) => _go.Release(chunks);
    }

    private ProgressiveFile Start(byte[] content, Feed feed, int chunk, int? stopAt = null, TimeSpan? grace = null, Action<ProgressiveFile>? ended = null)
        => ProgressiveFile.Start(Partial, Final, content.Length, feed.Fill(content, chunk, stopAt),
            CancellationToken.None, grace ?? TimeSpan.FromMinutes(1), ended ?? (_ => { }));

    [Fact]
    public async Task Reads_wait_only_for_their_own_range_in_any_order()
    {
        var content = Bytes(4000);
        var feed = new Feed();
        var file = Start(content, feed, chunk: 1000);
        using var reader = file.TryOpenReader()!;

        var late = new byte[500];
        var lateRead = file.ReadAsync(3200, late, CancellationToken.None).AsTask();
        var early = new byte[800];
        var earlyRead = file.ReadAsync(100, early, CancellationToken.None).AsTask();
        Assert.False(file.TryRead(0, new byte[10], out _)); // nothing has arrived
        await Task.Delay(100);
        Assert.False(earlyRead.IsCompleted);

        feed.Release(); // bytes 0-999
        Assert.Equal(800, await earlyRead.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(content.AsSpan(100, 800).ToArray(), early);
        Assert.True(file.TryRead(0, new byte[1000], out var now) && now == 1000);
        // The range straddling the second and third chunks waits for both.
        var straddle = file.ReadAsync(1900, new byte[200], CancellationToken.None).AsTask();
        feed.Release();
        await Task.Delay(100);
        Assert.False(straddle.IsCompleted);
        Assert.False(lateRead.IsCompleted);
        feed.Release();
        Assert.Equal(200, await straddle.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(lateRead.IsCompleted);
        feed.Release();
        // Past the end: only what the file holds.
        Assert.Equal(500, await lateRead.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(content.AsSpan(3200, 500).ToArray(), late);
        var tail = new byte[100];
        Assert.Equal(50, await file.ReadAsync(3950, tail, CancellationToken.None));
        Assert.Equal(0, await file.ReadAsync(4000, tail, CancellationToken.None));

        await file.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(content, File.ReadAllBytes(Final));
        Assert.Empty(Directory.GetFiles(Partial));
    }

    [Fact]
    public async Task Cancelling_a_read_stops_that_read_only_not_the_download()
    {
        var content = Bytes(2000);
        var feed = new Feed();
        var file = Start(content, feed, chunk: 1000);
        using var reader = file.TryOpenReader()!;
        using var cancel = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => file.ReadAsync(0, new byte[10], cancel.Token).AsTask());
        feed.Release(2);
        await file.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var all = new byte[2000];
        Assert.Equal(2000, await reader.ReadAsync(all));
        Assert.Equal(content, all);
        Assert.Equal(1, feed.Starts);
    }

    [Fact]
    public async Task Every_reader_shares_the_one_download()
    {
        var content = Bytes(3000);
        var feed = new Feed();
        var file = Start(content, feed, chunk: 1000);
        var readers = Enumerable.Range(0, 4).Select(_ => file.TryOpenReader()!).ToList();
        var reads = readers.Select(r => Task.Run(async () =>
        {
            var buffer = new byte[3000];
            await r.ReadExactlyAsync(buffer);
            return buffer;
        })).ToList();
        feed.Release(3);
        foreach (var read in reads) Assert.Equal(content, await read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, feed.Starts);
        foreach (var r in readers) await r.DisposeAsync();
    }

    [Fact]
    public async Task A_failed_download_fails_every_read_and_leaves_nothing_in_the_cache()
    {
        var content = Bytes(3000);
        var feed = new Feed { FailAfter = new IOException("the cloud went away") };
        var ended = new TaskCompletionSource<ProgressiveFile>();
        var file = Start(content, feed, chunk: 1000, stopAt: 1000, ended: f => ended.TrySetResult(f));
        using var reader = file.TryOpenReader()!;
        var waiting = file.ReadAsync(2000, new byte[100], CancellationToken.None).AsTask();
        feed.Release();
        var error = await Assert.ThrowsAsync<IOException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("the cloud went away", error.Message);
        Assert.Same(file, await ended.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        // Even the part that had arrived is no longer served.
        Assert.Throws<IOException>(() => file.TryRead(0, new byte[10], out _));
        Assert.Null(file.TryOpenReader());
        Assert.False(File.Exists(Final));
        Assert.Empty(Directory.GetFiles(Partial));
    }

    [Fact]
    public async Task A_short_or_long_download_is_never_served_as_complete()
    {
        var content = Bytes(3000);
        var shortFeed = new Feed();
        var file = Start(content, shortFeed, chunk: 1000, stopAt: 2000); // ends early, no error
        using (var reader = file.TryOpenReader()!)
        {
            var last = file.ReadAsync(2500, new byte[10], CancellationToken.None).AsTask();
            shortFeed.Release(2);
            var error = await Assert.ThrowsAsync<IOException>(() => last.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("the snapshot lists", error.Message);
        }
        Assert.False(File.Exists(Final));

        // More bytes than the snapshot lists: stopped at the first extra byte.
        var longer = Bytes(3500);
        var longFeed = new Feed();
        var second = ProgressiveFile.Start(Partial, Final, 3000, longFeed.Fill(longer, 3500),
            CancellationToken.None, TimeSpan.FromMinutes(1), _ => { });
        longFeed.Release();
        await second.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<IOException>(() => second.TryRead(0, new byte[10], out _));
        Assert.False(File.Exists(Final));
        Assert.Empty(Directory.GetFiles(Partial));
    }

    [Fact]
    public async Task The_download_stops_once_nobody_has_had_the_file_open_for_the_grace_period()
    {
        var content = Bytes(3000);
        var feed = new Feed();
        var file = Start(content, feed, chunk: 1000, grace: TimeSpan.FromMilliseconds(300));
        var first = file.TryOpenReader()!;
        feed.Release();
        await first.DisposeAsync();
        // Reopened within the grace period: it continues.
        await Task.Delay(100);
        var second = file.TryOpenReader()!;
        await Task.Delay(500);
        Assert.False(file.Completion.IsCompleted);
        await second.DisposeAsync();
        await file.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<IOException>(() => file.TryRead(0, new byte[10], out _));
        Assert.False(File.Exists(Final));
        Assert.Empty(Directory.GetFiles(Partial));
    }

    // ---- through the mounted snapshot's provider ----

    /// <summary>The in-memory engine, plus a dump that delivers the file in
    /// chunks the test releases, counting downloads.</summary>
    public class DumpingEngine : DispatchProxy
    {
        internal InMemoryBackupEngine Inner = null!;
        internal readonly SemaphoreSlim Go = new(0);
        internal int Dumps;
        internal string? SourceRoot;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name != nameof(IBackupEngine.DumpSnapshotFileAsync))
                return method.Invoke(Inner, args);
            Interlocked.Increment(ref Dumps);
            var bytes = File.ReadAllBytes(Path.Combine(SourceRoot!, ((string)args![5]!).Replace('/', Path.DirectorySeparatorChar)));
            var output = (Stream)args[6]!;
            var ct = (CancellationToken)args[7]!;
            return Task.Run(async () =>
            {
                for (var at = 0; at < bytes.Length; at += 1000)
                {
                    await Go.WaitAsync(ct);
                    await output.WriteAsync(bytes.AsMemory(at, Math.Min(1000, bytes.Length - at)), ct);
                }
                return true;
            });
        }
    }

    [Fact]
    public async Task Opening_returns_at_once_and_readers_of_the_file_share_one_download()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        var content = Bytes(5000);
        File.WriteAllBytes(Path.Combine(source, "film.bin"), content);
        File.WriteAllBytes(Path.Combine(source, "empty.txt"), Array.Empty<byte>());
        var inner = new InMemoryBackupEngine();
        var backup = new Backup { Id = "bk-u167", Name = "U167", SourcePaths = { source } };
        var destination = new Destination { Id = "dst-u167", Name = "Local", Kind = DestinationKind.LocalFolder, PathOrSubpath = Path.Combine(_root, "storage") };
        Directory.CreateDirectory(destination.PathOrSubpath);
        await inner.BackupPruneCheckAsync(backup, source, destination, "default", CancellationToken.None);
        var engine = DispatchProxy.Create<IBackupEngine, DumpingEngine>();
        var proxy = (DumpingEngine)(object)engine;
        proxy.Inner = inner;
        proxy.SourceRoot = source;

        using var provider = new EngineBackedSnapshotTreeProvider(engine, backup, source, destination, "default", 1, Path.Combine(_root, "cache"));
        Assert.NotNull(await provider.LoadTreeAsync(CancellationToken.None));

        // Nothing to download for an empty file.
        await using (var empty = await provider.OpenAsync("empty.txt", CancellationToken.None))
            Assert.Equal(0, await empty.ReadAsync(new byte[10]));
        Assert.Equal(0, proxy.Dumps);

        var first = await provider.OpenAsync("film.bin", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        var second = await provider.OpenAsync("FILM.BIN", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(5000, first.Length);
        var head = new byte[1000];
        var headRead = first.ReadAsync(head).AsTask();
        second.Seek(4000, SeekOrigin.Begin);
        var tail = new byte[1000];
        var tailRead = second.ReadAsync(tail).AsTask();
        proxy.Go.Release();
        Assert.Equal(1000, await headRead.WaitAsync(TimeSpan.FromSeconds(5)));
        await Task.Delay(100);
        Assert.False(tailRead.IsCompleted); // only the first 1,000 bytes have arrived
        proxy.Go.Release(4);
        Assert.Equal(1000, await tailRead.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(content.AsSpan(0, 1000).ToArray(), head);
        Assert.Equal(content.AsSpan(4000).ToArray(), tail);
        Assert.Equal(1, proxy.Dumps);
        Assert.DoesNotContain(inner.Calls, c => c.StartsWith("RestoreFiles"));
        await first.DisposeAsync();
        await second.DisposeAsync();

        // Afterwards it is read from the cache.
        var cached = Path.Combine(_root, "cache", "files", "film.bin");
        for (var i = 0; i < 100 && !File.Exists(cached); i++) await Task.Delay(20);
        Assert.Equal(content, File.ReadAllBytes(cached));
        await using (var again = await provider.OpenAsync("film.bin", CancellationToken.None))
        {
            var all = new byte[5000];
            await again.ReadExactlyAsync(all);
            Assert.Equal(content, all);
        }
        Assert.Equal(1, proxy.Dumps);
    }
}
