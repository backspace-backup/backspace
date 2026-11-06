using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Backspace.Services.Platform.Windows;

/// <summary>Moves a restored item into place by handle, never by name (U152).
/// A restore folder can be one other accounts may change (a new folder under
/// C:\ or on a second drive lets every account modify it). Moving by name, a
/// folder swapped for a link between the check and the move would carry the
/// owner's own files out of their profile, or put a planted file into it.
/// Here the item and the folder it goes into are opened without following a
/// link, both must really be where the restore put them (inside the restore
/// folder as it was when the restore began), and the item is renamed
/// relative to the open folder. Nothing is ever copied across drives.</summary>
[SupportedOSPlatform("windows")]
internal static class HandleRename
{
    private const uint ReadAttributes = 0x0080, DeleteAccess = 0x00010000, Synchronize = 0x00100000;
    private const uint ShareAll = 0x7, ShareRead = 0x1, ShareWrite = 0x2, OpenExisting = 3, GenericRead = 0x80000000;
    private const uint OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000;
    private const uint ReparsePointAttribute = 0x400, DirectoryAttribute = 0x10;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out AttributeTag info, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public IntPtr Status; public IntPtr Information; }

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle file, out IoStatusBlock status, byte[] info, uint size, int infoClass);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public uint Attributes; public uint ReparseTag; }

    /// <summary>Where an ordinary folder really is, read from an open handle.
    /// Taken once for the restore folder when the restore starts.</summary>
    internal static string FolderLocation(string folder)
    {
        using var handle = Open(folder, ReadAttributes | Synchronize);
        if (handle is null) throw new DirectoryNotFoundException(folder);
        RequireOrdinaryFolder(handle, folder);
        var location = ExactPathDelete.FinalPath(handle);
        // On a local drive the folder is exactly where its name says; a folder
        // above it swapped for a link would show here. A network drive's
        // letter stands for the share's own name, which is what Windows gives.
        var full = Path.GetFullPath(folder);
        if (!IsNetwork(full) && !SamePath(location, full))
            throw new IOException($"The restore folder {full} leads to {location}. Choose an ordinary folder as the destination.");
        return location;
    }

    /// <summary>Opens the restore folder, checked like
    /// <see cref="FolderLocation"/>, and keeps it open for the whole restore
    /// (round 4). While it is open, neither it nor any folder above it can be
    /// renamed, so it can't be swapped for a link while restic writes there or
    /// while Backspace publishes and cleans up. Its contents stay free to
    /// change.</summary>
    internal static SafeFileHandle Pin(string folder)
    {
        // Read access, not only attributes: a metadata-only handle does not
        // reliably keep a folder from being renamed (see ServicePathLease).
        var handle = Open(folder, GenericRead, ShareRead | ShareWrite)
            ?? throw new DirectoryNotFoundException(folder);
        try
        {
            RequireOrdinaryFolder(handle, folder);
            var location = ExactPathDelete.FinalPath(handle);
            var full = Path.GetFullPath(folder);
            if (!IsNetwork(full) && !SamePath(location, full))
                throw new IOException($"The restore folder {full} leads to {location}. Choose an ordinary folder as the destination.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static bool IsNetwork(string full) => full.StartsWith(@"\\", StringComparison.Ordinal)
        || new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network;

    /// <summary>Moves <paramref name="source"/> to <paramref name="destination"/>,
    /// both inside <paramref name="root"/>, whose real location at the start
    /// of the restore was <paramref name="rootLocation"/>. Fails, leaving both
    /// in place, if either leads anywhere else or the destination exists.</summary>
    internal static void Move(string source, string destination, string root, string rootLocation)
    {
        // Restic writes inside the restore folder; an item from elsewhere must be
        // exactly where its own name says (a folder above it swapped for a link
        // shows up as another place).
        var expectedSource = Inside(root, source) ? Expected(root, rootLocation, source) : Path.GetFullPath(source);
        var parent = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        var expectedParent = Expected(root, rootLocation, parent);
        using var item = Open(source, DeleteAccess | ReadAttributes | Synchronize)
            ?? throw new FileNotFoundException("The restored item is missing.", source);
        if (!SamePath(ExactPathDelete.FinalPath(item), expectedSource))
            throw new IOException($"{source} no longer leads into the restore folder; it was left in place.");
        using var folder = Open(parent, ReadAttributes | Synchronize)
            ?? throw new DirectoryNotFoundException(parent);
        RequireOrdinaryFolder(folder, parent);
        if (!SamePath(ExactPathDelete.FinalPath(folder), expectedParent))
            throw new IOException($"{parent} no longer leads into the restore folder; nothing was moved there.");
        Rename(item, folder, Path.GetFileName(destination), destination);
    }

    /// <summary>Throws unless the open file is really at
    /// <paramref name="path"/> inside the restore folder: checked before
    /// anything is written to it.</summary>
    internal static void RequireAt(SafeFileHandle handle, string path, string root, string rootLocation)
    {
        if (!SamePath(ExactPathDelete.FinalPath(handle), Expected(root, rootLocation, path)))
            throw new IOException($"{path} no longer leads into the restore folder; nothing was written to it.");
    }

    private static bool Inside(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string Expected(string root, string rootLocation, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (relative == ".") return rootLocation;
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException($"{path} is outside the restore folder.");
        return Path.Join(rootLocation, relative);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private static void RequireOrdinaryFolder(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandleEx(handle, 9 /* FileAttributeTagInfo */, out var info, (uint)Marshal.SizeOf<AttributeTag>()))
            throw Failure($"Could not inspect {path}", Marshal.GetLastWin32Error());
        if ((info.Attributes & ReparsePointAttribute) != 0 || (info.Attributes & DirectoryAttribute) == 0)
            throw new IOException($"Cannot restore through the linked folder {path}. Choose an ordinary folder as the destination.");
    }

    private static void Rename(SafeFileHandle item, SafeFileHandle folder, string name, string destination)
    {
        // FILE_RENAME_INFORMATION: replace flag (padded to 8), root folder
        // handle, name length, name. kernel32's wrapper treats the name as a
        // full path, so the kernel call is made directly: the name is then
        // taken relative to the open folder.
        var nameBytes = System.Text.Encoding.Unicode.GetBytes(name);
        var size = 24 + nameBytes.Length;
        var buffer = new byte[size];
        var added = false;
        folder.DangerousAddRef(ref added);
        try
        {
            BitConverter.TryWriteBytes(buffer.AsSpan(8), folder.DangerousGetHandle().ToInt64());
            BitConverter.TryWriteBytes(buffer.AsSpan(16), nameBytes.Length);
            nameBytes.CopyTo(buffer, 20);
            var status = NtSetInformationFile(item, out _, buffer, (uint)size, 10 /* FileRenameInformation, never replacing */);
            if (status != 0)
            {
                var error = (int)RtlNtStatusToDosError(status);
                throw Failure(error is 80 or 183 ? $"An item appeared in the restore destination: {destination}"
                    : error == 17 ? $"{destination} is on another drive than the restore folder; nothing was copied."
                    : $"Could not move the restored item to {destination}: {new Win32Exception(error).Message}", error);
            }
        }
        finally { if (added) folder.DangerousRelease(); }
    }

    private static SafeFileHandle? Open(string path, uint access, uint share = ShareAll)
    {
        var full = Path.GetFullPath(path);
        var name = full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC" + full[1..] : @"\\?\" + full;
        var handle = CreateFileW(name, access, share, IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error is 2 or 3) return null;
        throw Failure($"Could not open {path}: {new Win32Exception(error).Message}", error);
    }

    /// <summary>Carries the Windows error, so a file another program holds
    /// is waited out like any other lock.</summary>
    private static IOException Failure(string message, int error) => new(message, unchecked((int)0x80070000) | error);
}
