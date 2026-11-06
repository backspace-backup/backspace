using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Backspace.Services.Platform.Windows;

/// <summary>Deletes a folder Backspace made, and everything in it, without
/// ever following a link (U152). Directory.Delete decides "folder, not link"
/// from the listing and recurses later by name: a folder another account
/// swapped for a link in between would be followed, and its target emptied.
/// Here each folder is opened and checked first, and held so that it can be
/// neither renamed nor replaced while its contents are deleted. A link is
/// removed itself, never what it leads to.</summary>
[SupportedOSPlatform("windows")]
internal static class SafeTreeDelete
{
    private const uint ListDirectory = 0x0001, ReadAttributes = 0x0080, DeleteAccess = 0x00010000, Synchronize = 0x00100000;
    private const uint ShareRead = 0x1, ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000;
    private const uint ReparsePointAttribute = 0x400, DirectoryAttribute = 0x10;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out AttributeTag info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref uint flags, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public uint Attributes; public uint ReparseTag; }

    internal static void Delete(string root) => Delete(root, checkLocation: true);

    private static void Delete(string root, bool checkLocation)
    {
        var full = Path.GetFullPath(root);
        using var folder = Open(full, ListDirectory | ReadAttributes | Synchronize, ShareRead);
        if (folder is null) return; // already gone
        if ((Attributes(folder) & ReparsePointAttribute) != 0 || (Attributes(folder) & DirectoryAttribute) == 0)
        {
            folder.Dispose();
            RemoveEntry(full); // the link itself
            return;
        }
        // Only the last part of the name is opened without following links: a
        // folder above it swapped for a link would lead elsewhere (round 4).
        // The open folder must really be where its name says; held, nothing
        // above it can be renamed until it is deleted. A network drive's
        // letter stands for the share's own name, which Windows gives back.
        if (checkLocation && !IsNetwork(full) && !SamePath(ExactPathDelete.FinalPath(folder), full))
            throw new IOException($"{full} leads to {ExactPathDelete.FinalPath(folder)}; nothing was deleted.");
        // Held: this folder cannot be renamed or replaced while it is emptied.
        foreach (var entry in Directory.EnumerateFileSystemEntries(full))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0)
                Delete(entry, checkLocation: false); // inside a held folder; opened and checked again before anything inside is touched
            else
                RemoveEntry(entry);
        }
        folder.Dispose();
        RemoveEntry(full);
    }

    /// <summary>Deletes the named entry itself, a file, an empty folder or a
    /// link, through a handle opened without following it.</summary>
    private static void RemoveEntry(string path)
    {
        using var handle = Open(path, DeleteAccess | ReadAttributes | Synchronize, ShareAll);
        if (handle is null) return;
        // Delete, POSIX semantics (the name goes at once), ignore read-only.
        uint flags = 0x1 | 0x2 | 0x10;
        if (!SetFileInformationByHandle(handle, 21 /* FileDispositionInfoEx */, ref flags, 4))
        {
            var error = Marshal.GetLastWin32Error();
            throw Failure(error == 145 ? $"{path} is not empty; it was left in place." : $"Could not delete {path}: {new Win32Exception(error).Message}", error);
        }
    }

    private static SafeFileHandle? Open(string path, uint access, uint share)
    {
        var name = path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC" + path[1..] : @"\\?\" + path;
        var handle = CreateFileW(name, access, share, IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error is 2 or 3) return null;
        throw Failure($"Could not open {path}: {new Win32Exception(error).Message}", error);
    }

    /// <summary>Carries the Windows error, so a file another program holds
    /// is waited out by the caller like any other lock.</summary>
    private static IOException Failure(string message, int error) => new(message, unchecked((int)0x80070000) | error);

    private static bool IsNetwork(string full) => full.StartsWith(@"\\", StringComparison.Ordinal)
        || new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network;

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private static uint Attributes(SafeFileHandle handle) =>
        GetFileInformationByHandleEx(handle, 9 /* FileAttributeTagInfo */, out var info, (uint)Marshal.SizeOf<AttributeTag>())
            ? info.Attributes : throw new Win32Exception(Marshal.GetLastWin32Error());
}
