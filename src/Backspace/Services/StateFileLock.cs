using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Backspace.Services;

/// <summary>Serializes an entire read/change/replace transaction across processes.
/// The lock lives beside the state, so GUI, scheduled and elevated runs coordinate
/// even across Windows sessions. Disposing the handle (or process death) releases
/// it; the empty lock file itself is deliberately retained.</summary>
internal static class StateFileLock
{
    [ThreadStatic] private static Dictionary<string, Entry>? _held;
    private sealed class Entry(FileStream stream)
    {
        public FileStream Stream { get; } = stream;
        public int Depth { get; set; } = 1;
    }

    public static IDisposable Acquire(string statePath, TimeSpan? timeout = null)
    {
        var path = Path.GetFullPath(statePath) + ".lock";
        _held ??= new Dictionary<string, Entry>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (_held.TryGetValue(path, out var existing))
        {
            existing.Depth++;
            return new Lease(path);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var watch = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        while (true)
        {
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                _held.Add(path, new Entry(stream));
                return new Lease(path);
            }
            catch (IOException) when (watch.Elapsed < limit)
            {
                Thread.Sleep(25);
            }
        }
    }

    private sealed class Lease(string path) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var entry = _held![path];
            if (--entry.Depth != 0) return;
            _held.Remove(path);
            entry.Stream.Dispose();
        }
    }
}
