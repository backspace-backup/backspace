using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Backspace.Services;

/// <summary>
/// Keeps an update from replacing the app while another of its processes
/// runs, with named mutexes instead of a lock file (U218, owner, 12 November
/// 2026: "Cant you just use a mutex so it is guaranteed gone when software
/// exits?"). Windows releases a mutex when its process ends, even on a crash
/// or power loss, so nothing is ever left behind or stale.
///
/// A process of this copy owns one of <see cref="Slots"/> slot mutexes for
/// its lifetime. An update owns the writer mutex, then every slot: one still
/// owned means another process runs. A process starting while an update
/// holds the writer waits for it. Mutexes belong to the thread that owns
/// them, so each holder keeps a small thread for as long as it holds them.
/// Names are global and derived from the app's folder, so the copies in other
/// folders don't share a gate. A mutex another Windows account created (the
/// background service, which an update stops first) can't be opened: a
/// process skips that slot, and an update counts it as in use.
/// </summary>
internal static class RuntimeGate
{
    internal const int Slots = 64;

    internal static string Name(string root, string part)
    {
        var folder = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder)))[..20];
        return @"Global\Backspace-runtime-" + hash + "-" + part;
    }

    internal static string SlotName(string root, int slot) => Name(root, "slot-" + slot);

    internal static IDisposable? Shared(string root, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var holder = Holder.Start(() =>
        {
            do
            {
                if (WriterFree(root))
                    for (var i = 0; i < Slots; i++)
                        if (TryTake(SlotName(root, i)) is { } slot) return [slot];
                if (DateTime.UtcNow >= deadline) break;
                Thread.Sleep(100);
            } while (true);
            return null;
        });
        return holder ?? throw new IOException("Backspace is being updated. Please try opening it again in a moment.");
    }

    internal static IDisposable? Exclusive(string root) => Holder.Start(() =>
    {
        var taken = new List<Mutex>();
        if (TryTake(Name(root, "writer")) is not { } writer) return null;
        taken.Add(writer);
        for (var i = 0; i < Slots; i++)
        {
            if (TryTake(SlotName(root, i)) is not { } slot) { Holder.Release(taken); return null; }
            taken.Add(slot);
        }
        return taken;
    });

    private static bool WriterFree(string root)
    {
        if (TryTake(Name(root, "writer")) is not { } writer) return false;
        Holder.Release([writer]);
        return true;
    }

    /// <summary>The mutex owned by this thread, or null when another process
    /// owns it or it belongs to another account.</summary>
    private static Mutex? TryTake(string name)
    {
        Mutex mutex;
        try { mutex = new Mutex(false, name); }
        catch (UnauthorizedAccessException) { return null; }
        catch (WaitHandleCannotBeOpenedException) { return null; }
        try
        {
            if (mutex.WaitOne(0)) return mutex;
        }
        catch (AbandonedMutexException) { return mutex; } // Its process ended: owned now.
        mutex.Dispose();
        return null;
    }

    private sealed class Holder : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;
        private List<Mutex>? _held;

        private Holder(Func<List<Mutex>?> take)
        {
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _held = take();
                ready.Set();
                if (_held is null) return;
                _release.Wait();
                Release(_held);
            }) { IsBackground = true, Name = "Backspace runtime gate" };
            _thread.Start();
            ready.Wait();
        }

        internal static Holder? Start(Func<List<Mutex>?> take)
        {
            var holder = new Holder(take);
            return holder._held is null ? null : holder;
        }

        internal static void Release(List<Mutex> mutexes)
        {
            foreach (var mutex in mutexes)
            {
                try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
                mutex.Dispose();
            }
        }

        public void Dispose()
        {
            if (_release.IsSet) return;
            _release.Set();
            _thread.Join();
        }
    }
}
