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
        // Without a limit it waits as long as it takes: the other Backspace
        // process releases it within moments, and Dropbox or a virus scanner
        // opening the lock file looks the same. It used to give up after 5 s,
        // failing backups and losing history records (U135, 4 November 2026).
        var watch = Stopwatch.StartNew();
        var warned = false;
        while (true)
        {
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                _held.Add(path, new Entry(stream));
                return new Lease(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                && (timeout is null || watch.Elapsed < timeout) && (ex is IOException || FileAccessRetry.IsLock(ex)))
            {
                if (!warned && watch.Elapsed > TimeSpan.FromSeconds(5))
                {
                    warned = true;
                    AppLogger.For("StateFileLock").Warning("Waiting for {Path}: another program or Backspace process holds it. Retrying until it is free.", path);
                }
                Thread.Sleep(watch.Elapsed < TimeSpan.FromSeconds(1) ? 25 : 250);
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
