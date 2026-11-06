using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Backspace.Services.Platform.Windows;

/// <summary>Deletes a file only if it is really at the path named (U152).
/// On a drive other accounts can change, a folder on the way can be swapped
/// for a link, so that deleting by name removes a file elsewhere, with the
/// rights of whoever deletes: Windows itself in the background service. The
/// file is opened without following a link at its own name, its real path is
/// read from the open handle, and it is deleted through that same handle.</summary>
[SupportedOSPlatform("windows")]
internal static class ExactPathDelete
{
    private const uint Delete = 0x00010000, ReadAttributes = 0x0080;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000; // a folder opens too, to be refused
    private const uint ReparsePointAttribute = 0x400, DirectoryAttribute = 0x10;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileAttributeTagInfo info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref byte delete, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo { public uint Attributes; public uint ReparseTag; }

    /// <summary>True when the file at <paramref name="path"/> was deleted;
    /// false when it is absent. Throws when the name leads anywhere else.</summary>
    internal static bool File(string path)
    {
        var expected = Path.GetFullPath(path);
        using var handle = CreateFileW(@"\\?\" + (expected.StartsWith(@"\\", StringComparison.Ordinal) ? "UNC" + expected[1..] : expected),
            Delete | ReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 3) return false; // not found
            throw new Win32Exception(error);
        }
        if (!GetFileInformationByHandleEx(handle, 9 /* FileAttributeTagInfo */, out var info, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & (ReparsePointAttribute | DirectoryAttribute)) != 0)
            throw new IOException($"{expected} is a link or a folder, not Backspace's note; it was left alone.");
        var actual = FinalPath(handle);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"{expected} leads to {actual}; it was left alone.");
        byte deleteFile = 1;
        if (!SetFileInformationByHandle(handle, 4 /* FileDispositionInfo */, ref deleteFile, 1))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return true;
    }

    internal static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder((int)length + 1);
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var text = buffer.ToString();
        if (text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\" + text[7..];
        return text.StartsWith(@"\\?\", StringComparison.Ordinal) ? text[4..] : text;
    }
}
