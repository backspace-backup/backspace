using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace Backspace.Services.Platform.Windows;

/// <summary>Pin one object while changing only its own ACL. Microsoft
/// documents that SetSecurityInfo on a MAXIMUM_ALLOWED handle does not propagate
/// inheritance changes to existing children. Future children still inherit.</summary>
internal sealed class ServiceObjectPermissions : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly bool _directory;
    private readonly bool _workingFolder;

    /// <param name="workingFolder">The folder may be a running program's
    /// current directory: the portable installation and its configuration
    /// folder (Backspace starts there), or an ancestor of a repository.
    /// Windows opens a current directory without delete sharing, so a
    /// MAXIMUM_ALLOWED handle, which includes delete access, fails with a
    /// sharing violation while any such program runs. These folders are
    /// opened only for reading and changing their own permissions instead.</param>
    internal ServiceObjectPermissions(string path, bool directory = true, bool allowDelete = false, bool workingFolder = false)
    {
        _directory = directory;
        _workingFolder = directory && workingFolder;
        // File handles need data-read sharing protection but no write-data
        // access: setup is itself executing Backspace.exe. Open the reparse
        // object rather than following a link supplied in a writable folder.
        // READ_CONTROL | WRITE_DAC | WRITE_OWNER | SYNCHRONIZE | FILE_READ_ATTRIBUTES.
        var access = _workingFolder ? 0x001e0080u : directory ? 0x02000000u : 0x001e0081u | (allowDelete ? 0x10000u : 0);
        _handle = CreateFileW(path, access, directory ? 3u : 1u, IntPtr.Zero, 3, 0x02200000u, IntPtr.Zero);
        if (_handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); _handle.Dispose(); throw new Win32Exception(error); }
        try
        {
            if (!GetFileInformationByHandleEx(_handle, 9, out var info, 8)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory)
                throw new SecurityException("Service setup cannot follow a redirected file or folder.");
            if (!directory)
            {
                if (!GetFileInformationByHandle(_handle, out var file)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (file.Links != 1) throw new SecurityException("Service setup cannot change permissions on a file that is hard-linked elsewhere. Use an independent file copy.");
            }
        }
        catch { _handle.Dispose(); throw; }
    }

    internal string Identity
    {
        get
        {
            if (!GetFileInformationByHandle(_handle, out var file)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return $"{file.Volume:X8}:{file.IndexHigh:X8}{file.IndexLow:X8}";
        }
    }

    internal string Read()
        => Read(_handle);

    internal static string Read(SafeFileHandle handle)
    {
        var error = GetSecurityInfo(handle, 1, 5, out _, out _, out _, out _, out var descriptor);
        try
        {
            if (error != 0) throw new Win32Exception((int)error);
            var bytes = new byte[GetSecurityDescriptorLength(descriptor)];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            return new RawSecurityDescriptor(bytes, 0).GetSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
        }
        finally { if (descriptor != IntPtr.Zero) LocalFree(descriptor); }
    }

    internal void Set(string sddl) => Set(sddl, changeOwner: true);
    internal void SetDacl(string sddl) => Set(sddl, changeOwner: false);

    internal void DeleteOnClose()
    {
        // Delete the inspected object, never a pathname that could have been
        // replaced after its parent's original permissions were restored.
        var disposition = new DeleteDisposition { Delete = 1 };
        if (!SetFileInformationByHandle(_handle, 4, ref disposition, 1))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal void MoveTo(string destination)
    {
        if (!_directory || !System.IO.Path.IsPathFullyQualified(destination)) throw new ArgumentException("A full destination directory is required.");
        var name = System.Text.Encoding.Unicode.GetBytes(destination);
        // FILE_RENAME_INFO uses an aligned HANDLE after its one-byte flag.
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + 4;
        // Some Windows path conversion layers also inspect a trailing NUL,
        // even though FileNameLength excludes it. Never expose uninitialized
        // memory after the requested path.
        var size = Math.Max(24, nameOffset + name.Length + 2);
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            for (var n = 0; n < size; n++) Marshal.WriteByte(memory, n, 0); // ReplaceIfExists=false.
            Marshal.WriteInt32(memory, lengthOffset, name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(memory, nameOffset), name.Length);
            if (!SetFileInformationByHandle(_handle, 3, memory, checked((uint)size)))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private void Set(string sddl, bool changeOwner)
    {
        if (!_directory)
        {
            if (!GetFileInformationByHandle(_handle, out var file)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (file.Links != 1) throw new SecurityException("The file acquired another hard link during setup. Its permissions were not changed.");
        }
        var descriptor = new RawSecurityDescriptor(sddl);
        if (descriptor.DiscretionaryAcl is null || descriptor.Owner is null) throw new SecurityException("A complete service directory access list is required.");
        var bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0);
        var protect = (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0 ? 0x80000000u : 0x20000000u;
        if (_workingFolder)
        {
            // Without MAXIMUM_ALLOWED, SetSecurityInfo would rewrite every
            // existing child. The kernel call changes only this folder; the
            // descriptor is stored exactly as given, including any entries
            // previously read as inherited from the parent. Restoring an
            // auto-inherited list must request that flag explicitly, or the
            // kernel stores the list without it.
            if ((descriptor.ControlFlags & ControlFlags.DiscretionaryAclAutoInherited) != 0)
            {
                descriptor.SetFlags(descriptor.ControlFlags | ControlFlags.DiscretionaryAclAutoInheritRequired);
                bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0);
            }
            if (!SetKernelObjectSecurity(_handle, protect | (changeOwner ? 5u : 4u), bytes))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return;
        }
        var memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            if (!GetSecurityDescriptorOwner(memory, out var owner, out _)
                || !GetSecurityDescriptorDacl(memory, out var present, out var acl, out _) || !present)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var error = SetSecurityInfo(_handle, 1, protect | (changeOwner ? 5u : 4u), changeOwner ? owner : IntPtr.Zero, IntPtr.Zero, acl, IntPtr.Zero);
            if (error != 0) throw new Win32Exception((int)error);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    public void Dispose() => _handle.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { public uint Attributes, Tag; }
    [StructLayout(LayoutKind.Sequential)] private struct DeleteDisposition { public byte Delete; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out AttributeTag information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, ref DeleteDisposition information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, IntPtr information, uint size);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSecurityDescriptorOwner(IntPtr descriptor, out IntPtr owner, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSecurityDescriptorDacl(IntPtr descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present, out IntPtr acl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetKernelObjectSecurity(SafeFileHandle handle, uint information, byte[] descriptor);
    [DllImport("advapi32.dll")] private static extern uint SetSecurityInfo(SafeFileHandle handle, int type, uint information, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(SafeFileHandle handle, int type, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
