using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Backspace.Services.Platform.Windows;

/// <summary>Keep approved local directory paths from being renamed or changed
/// into junctions while a privileged operation uses them. This does not follow
/// links or grant access, and does not approve any unlisted source.</summary>
internal sealed class ServicePathLease : IDisposable
{
    private readonly List<SafeFileHandle> _handles = new();
    private readonly List<SafeFileHandle> _markers = new();
    internal List<string> MarkerNames { get; } = new();

    internal static ServicePathLease Acquire(IEnumerable<string> paths, CancellationToken cancellation = default)
    {
        var elapsed = Stopwatch.StartNew();
        var lease = new ServicePathLease();
        var opened = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var leaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in paths)
            {
                var full = ServiceInstallationSecurity.CanonicalLocalPath(path);
                leaves.Add(full);
                var components = new Stack<string>();
                for (string? directory = full; directory is not null; directory = Path.GetDirectoryName(directory))
                    components.Push(directory);
                while (components.TryPop(out var directory))
                {
                    if (!opened.Add(directory)) continue;
                    // Open the reparse object itself, never its redirected target.
                    // Initially deny write/delete sharing until every ancestor
                    // has been checked and the terminal guards exist.
                    // A metadata-only FILE_READ_ATTRIBUTES handle does not
                    // participate in the sharing check strongly enough to pin
                    // a directory. Request actual read/list access.
                    var handle = OpenDirectory(directory, 1, elapsed, cancellation);
                    lease._handles.Add(handle);
                    if (!GetFileInformationByHandleEx(handle, 9, out var information, 8))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    if ((information.Attributes & (uint)FileAttributes.ReparsePoint) != 0
                        || (information.Attributes & (uint)FileAttributes.Directory) == 0)
                        throw new SecurityException("An approved backup path is redirected or is no longer a folder: " + directory);
                }
            }

            // A junction/symlink cannot replace a nonempty NTFS directory.
            // Keep each terminal folder nonempty with a unique, undeletable
            // zero-byte file. Ancestors remain nonempty through their pinned
            // child directories. Delete-on-close also cleans up after a crash.
            // Creation happens while the strict handles still prevent redirection.
            foreach (var leaf in leaves)
            {
                var name = ".backspace-path-guard-" + Guid.NewGuid().ToString("N");
                var marker = CreateFile(Path.Combine(leaf, name), 0x80010000, 3,
                    IntPtr.Zero, 1, 0x06200102, IntPtr.Zero);
                if (marker.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    marker.Dispose();
                    throw new IOException("The approved folder could not be protected: " + leaf,
                        new Win32Exception(error));
                }
                lease._markers.Add(marker);
                lease.MarkerNames.Add(name);
            }

            // Allow ordinary child-file renames/atomic saves, but continue
            // denying directory deletion/renaming. Never leave a handle gap.
            var relaxed = new List<SafeFileHandle>();
            try
            {
                foreach (var directory in opened) relaxed.Add(OpenDirectory(directory, 3, elapsed, cancellation));
                foreach (var handle in lease._handles) handle.Dispose();
                lease._handles.Clear();
                lease._handles.AddRange(relaxed);
            }
            catch { foreach (var handle in relaxed) handle.Dispose(); throw; }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    public void Dispose()
    {
        for (var i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose();
        _handles.Clear();
        for (var i = _markers.Count - 1; i >= 0; i--) _markers[i].Dispose();
        _markers.Clear();
    }

    private static SafeFileHandle OpenDirectory(string directory, uint sharing, Stopwatch elapsed, CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var handle = CreateFile(directory, 0x80000000, sharing, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            var error = Marshal.GetLastWin32Error();
            if (elapsed.Elapsed >= TimeSpan.FromSeconds(5) || error is not (32 or 33))
            {
                handle.Dispose();
                throw new IOException("An approved backup folder could not be held safely: " + directory,
                    new Win32Exception(error));
            }
            handle.Dispose();
            // Filter drivers can briefly hold a newly created directory open.
            // Keep the strict sharing requirement; retry only sharing/lock
            // conflicts, within one budget for the entire acquisition.
            if (cancellation.WaitHandle.WaitOne(50)) cancellation.ThrowIfCancellationRequested();
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { public uint Attributes; public uint ReparseTag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out AttributeTag information, uint size);
}
