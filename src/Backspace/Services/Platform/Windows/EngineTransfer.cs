using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// How much a running backup has sent to its storage (U124, 4 November 2026).
/// Restic's progress counts every byte it reads, including files that need
/// nothing sent (unchanged, or already stored), so a speed computed from it
/// read 622 MB/s to Google Drive. Restic writes each pack of new data to a
/// temporary file first, then sends it. Windows counts what a process writes
/// to files and pipes, not what it sends over a network connection. So for a
/// folder, drive or share, and for Google Drive, Dropbox and OneDrive (restic
/// hands the data to rclone over a pipe), the restic process writes twice what
/// it stores; for an S3-compatible service, which restic reaches directly over
/// the network, it writes it once. Measured on restic 0.19.1: 209.8 MB written
/// for 104.9 MB added to a folder, 104.9 MB for 52.4 MB through rclone, and
/// nothing counted for 52.4 MB sent over a socket. Its cache of folder
/// listings adds a little; the figure is an estimate.
/// </summary>
internal static class EngineTransfer
{
    /// <summary>Bytes stored so far by the one restic process started by
    /// <paramref name="parentId"/> (the engine), or null when there isn't
    /// exactly one: two backups at once can't be told apart.</summary>
    internal static long? SentBytes(int parentId, bool direct)
        => OperatingSystem.IsWindows() && OnlyChild(parentId, "restic.exe") is { } restic && Written(restic) is { } written ? Stored(written, direct) : null;

    /// <summary>What restic stored, from what it wrote: <paramref name="direct"/>
    /// when it reaches the storage over the network itself (S3-compatible).</summary>
    internal static long Stored(long written, bool direct) => direct ? written : written / 2;

    /// <summary>The one process named <paramref name="name"/> started by
    /// <paramref name="parentId"/>, or null when there is none or several.</summary>
    internal static int? OnlyChild(int parentId, string name)
    {
        int? found = null;
        foreach (var (id, parent, exe) in Processes())
        {
            if (parent != parentId || !exe.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (found is not null) return null;
            found = id;
        }
        return found;
    }

    /// <summary>The process that started <paramref name="processId"/>, or null.</summary>
    internal static int? ParentOf(int processId)
    {
        foreach (var (id, parent, _) in Processes()) if (id == processId) return parent;
        return null;
    }

    /// <summary>Bytes a process has written so far: to files, pipes and the network.</summary>
    internal static long? Written(int processId)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try { return GetProcessIoCounters(handle, out var counters) ? (long)counters.WriteTransferCount : null; }
        finally { CloseHandle(handle); }
    }

    private static IEnumerable<(int Id, int Parent, string Name)> Processes()
    {
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle) yield break;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                yield return ((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile);
        }
        finally { CloseHandle(snapshot); }
    }

    private const uint SnapProcess = 0x2, QueryLimitedInformation = 0x1000;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll")] private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
