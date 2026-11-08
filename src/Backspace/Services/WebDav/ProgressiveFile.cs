using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Backspace.Services.WebDav;

/// <summary>
/// One file of a mounted snapshot while it downloads into the mount's cache
/// (U167). Its bytes are written, in order, to a temporary file outside the
/// cache's file tree; a read waits only until the range it asks for has
/// arrived, never for the whole file, so a copy out of the drive starts at
/// once and moves with the download, as from a slow network share. Every
/// reader of the file shares the one download.
///
/// <para>The temporary file becomes the cached file only when the download
/// has ended successfully with exactly the size the snapshot lists. A failed
/// or stopped download fails every read and leaves nothing in the cache.
/// When the last reader closes the file before it has arrived, the download
/// stops after <c>idleGrace</c>, unless someone opens it again meanwhile.</para>
/// </summary>
internal sealed class ProgressiveFile
{
    /// <summary>Writes the file's bytes, in order, to the given stream.</summary>
    internal delegate Task Fill(Stream output, CancellationToken ct);

    private readonly object _gate = new();
    private readonly string _partialPath;
    private readonly string _finalPath;
    private readonly CancellationTokenSource _stop;
    private readonly TimeSpan _idleGrace;
    private readonly Action<ProgressiveFile> _ended;
    private readonly SafeFileHandle _read;
    private TaskCompletionSource _advanced = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _available;
    private bool _complete;
    private Exception? _failure;
    private int _readers;
    private int _idleGeneration;
    private bool _readClosed;

    public long Length { get; }

    /// <summary>Ends when the download does, successfully or not.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    /// <summary>Bytes from the start of the file that can be read now.</summary>
    public long Available { get { lock (_gate) return _available; } }

    private ProgressiveFile(string partialPath, string finalPath, long length, CancellationToken lifetime,
        TimeSpan idleGrace, Action<ProgressiveFile> ended, SafeFileHandle read)
    {
        _partialPath = partialPath;
        _finalPath = finalPath;
        Length = length;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _idleGrace = idleGrace;
        _ended = ended;
        _read = read;

    }

    /// <summary>Starts the download of <paramref name="length"/> bytes into a
    /// new file in <paramref name="partialDir"/>, which becomes
    /// <paramref name="finalPath"/> once complete. <paramref name="ended"/>
    /// hears when the download has ended, whatever the outcome.</summary>
    public static ProgressiveFile Start(string partialDir, string finalPath, long length, Fill fill,
        CancellationToken lifetime, TimeSpan idleGrace, Action<ProgressiveFile> ended)
    {
        Directory.CreateDirectory(partialDir);
        var partial = Path.Combine(partialDir, Guid.NewGuid().ToString("N") + ".part");
        // Readers and the rename need FILE_SHARE_DELETE on every handle: the
        // finished file is renamed into the cache while readers still use it.
        var writer = new FileStream(partial, FileMode.CreateNew, FileAccess.Write,
            FileShare.Read | FileShare.Delete, bufferSize: 0);
        SafeFileHandle read;
        try { read = File.OpenHandle(partial, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch { writer.Dispose(); try { File.Delete(partial); } catch { } throw; }
        var file = new ProgressiveFile(partial, finalPath, length, lifetime, idleGrace, ended, read);
        file.Completion = Task.Run(() => file.RunAsync(writer, fill));
        return file;
    }

    /// <summary>A stream over this file for one reader, or null once the
    /// download has failed or the file has been let go (open the cached file,
    /// or start again, instead). Disposing it releases the reader.</summary>
    public ProgressiveFileStream? TryOpenReader()
    {
        lock (_gate)
        {
            if (_readClosed || _failure is not null) return null;
            _readers++;
            _idleGeneration++;
        }
        return new ProgressiveFileStream(this);
    }

    internal void Release()
    {
        int generation;
        bool closeNow, waitIdle;
        lock (_gate)
        {
            _readers--;
            generation = ++_idleGeneration;
            var ended = _complete || _failure is not null;
            closeNow = _readers == 0 && ended && !_readClosed;
            if (closeNow) _readClosed = true;
            waitIdle = _readers == 0 && !ended;
        }
        if (closeNow) _read.Dispose();
        if (waitIdle)
            _ = Task.Delay(_idleGrace).ContinueWith(_ =>
            {
                lock (_gate)
                    if (_readers > 0 || generation != _idleGeneration || _complete || _failure is not null) return;
                try { _stop.Cancel(); } catch (ObjectDisposedException) { }
            }, TaskScheduler.Default);
    }

    /// <summary>Copies bytes at <paramref name="offset"/> when that whole range
    /// (or the rest of the file) has arrived; false when it has not yet.</summary>
    public bool TryRead(long offset, Span<byte> destination, out int read)
    {
        read = 0;
        if (offset >= Length || destination.Length == 0) return true;
        var end = Math.Min(Length, offset + destination.Length);
        lock (_gate)
        {
            ThrowIfFailed();
            if (_available < end) return false;
        }
        read = ReadArrived(offset, destination[..(int)(end - offset)]);
        return true;
    }

    /// <summary>Waits until bytes at <paramref name="offset"/> have arrived,
    /// for the whole range asked (or the rest of the file), then copies them.
    /// Cancelling stops only this wait, not the download.</summary>
    public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken ct)
    {
        if (offset >= Length || destination.Length == 0) return 0;
        var end = Math.Min(Length, offset + destination.Length);
        while (true)
        {
            Task advanced;
            lock (_gate)
            {
                ThrowIfFailed();
                if (_available >= end) break;
                advanced = _advanced.Task;
            }
            await advanced.WaitAsync(ct).ConfigureAwait(false);
        }
        return ReadArrived(offset, destination.Span[..(int)(end - offset)]);
    }

    private int ReadArrived(long offset, Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var n = RandomAccess.Read(_read, destination[total..], offset + total);
            if (n <= 0) throw new IOException("The downloaded part of this file could not be read back from the cache.");
            total += n;
        }
        return total;
    }

    private void ThrowIfFailed()
    {
        if (_failure is not null) throw new IOException(_failure.Message, _failure);
    }

    private async Task RunAsync(FileStream writer, Fill fill)
    {
        try
        {
            await using (writer)
            {
                var sink = new Sink(this, writer);
                await fill(sink, _stop.Token).ConfigureAwait(false);
                _stop.Token.ThrowIfCancellationRequested();
                if (sink.Written != Length)
                    throw new IOException($"The backup engine returned {sink.Written:N0} bytes for this file; the snapshot lists {Length:N0}.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_finalPath)!);
            try { File.Move(_partialPath, _finalPath); }
            catch (IOException) when (File.Exists(_finalPath)) { TryDelete(_partialPath); } // the same immutable bytes, already cached
            Finish(null);
        }
        catch (Exception ex)
        {
            TryDelete(_partialPath);
            Finish(ex is OperationCanceledException
                ? new IOException("The download of this file stopped before it finished.", ex)
                : ex);
        }
        finally { _stop.Dispose(); }
    }

    private void Finish(Exception? failure)
    {
        TaskCompletionSource advanced;
        bool close;
        lock (_gate)
        {
            if (failure is null) _complete = true; else _failure = failure;
            advanced = _advanced;
            close = _readers == 0 && !_readClosed;
            if (close) _readClosed = true;
        }
        advanced.TrySetResult();
        if (close) _read.Dispose();
        try { _ended(this); } catch { }
    }

    private void Arrived(int count)
    {
        TaskCompletionSource advanced;
        lock (_gate)
        {
            _available += count;
            advanced = _advanced;
            _advanced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        advanced.TrySetResult();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    /// <summary>Where the engine writes: straight to the temporary file
    /// (unbuffered, so other handles see each write at once), counting.</summary>
    private sealed class Sink(ProgressiveFile file, FileStream writer) : Stream
    {
        public long Written { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Written; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            writer.Write(buffer);
            Advance(buffer.Length);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Check(buffer.Length);
            await writer.WriteAsync(buffer, ct).ConfigureAwait(false);
            Advance(buffer.Length);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        private void Check(int count)
        {
            if (Written + count > file.Length)
                throw new IOException($"The backup engine returned more bytes for this file than the {file.Length:N0} the snapshot lists.");
        }

        private void Advance(int count)
        {
            if (count == 0) return;
            Written += count;
            file.Arrived(count);
        }
    }
}

/// <summary>A reader's view of a <see cref="ProgressiveFile"/>: seekable, with
/// the snapshot's length from the start; a read waits for its range.</summary>
internal sealed class ProgressiveFileStream : Stream
{
    private ProgressiveFile? _file;
    private long _position;

    internal ProgressiveFileStream(ProgressiveFile file) => _file = file;

    internal ProgressiveFile File => _file ?? throw new ObjectDisposedException(nameof(ProgressiveFileStream));

    public override bool CanRead => _file is not null;
    public override bool CanSeek => _file is not null;
    public override bool CanWrite => false;
    public override long Length => File.Length;
    public override long Position { get => _position; set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)); }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var read = await File.ReadAsync(_position, buffer, ct).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => Length + offset,
    };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Interlocked.Exchange(ref _file, null)?.Release();
        base.Dispose(disposing);
    }
}
