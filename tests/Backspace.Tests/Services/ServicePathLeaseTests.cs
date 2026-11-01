using System;
using System.IO;
using System.Diagnostics;
using System.Security;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServicePathLeaseTests
{
    [Fact]
    public async System.Threading.Tasks.Task A_temporary_writer_can_release_without_weakening_the_guard()
    {
        using var ws = new TempWorkspace(); var source = ws.Sub("temporarily-busy");
        using var writer = CreateFile(source, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        Assert.False(writer.IsInvalid);
        var release = System.Threading.Tasks.Task.Run(async () =>
        { await System.Threading.Tasks.Task.Delay(2000); writer.Dispose(); });
        using (ServicePathLease.Acquire(new[] { source }))
            Assert.Throws<IOException>(() => Directory.Move(source, source + "-moved"));
        await release;
        AfterRelease(() => Directory.Move(source, source + "-moved"));
    }

    [Fact]
    public void A_busy_directory_wait_can_be_cancelled_and_releases_its_ancestors()
    {
        using var ws = new TempWorkspace(); var source = ws.Sub("busy");
        using var writer = CreateFile(source, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        Assert.False(writer.IsInvalid);
        using var stop = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        Assert.Throws<OperationCanceledException>(() => ServicePathLease.Acquire(new[] { source }, stop.Token));
        writer.Dispose();
        AfterRelease(() => Directory.Move(source, source + "-moved"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExistingJunctionInSourceOrAncestorIsRejected(bool child)
    {
        using var ws = new TempWorkspace();
        var target = ws.Sub("other-tree");
        Directory.CreateDirectory(Path.Combine(target, "child"));
        var sentinel = Path.Combine(target, "untouched.txt");
        File.WriteAllText(sentinel, "keep this synthetic file");
        var link = Path.Combine(ws.Root, "redirected");
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path " + Quote(link) + " -Target " + Quote(target) + " | Out-Null");
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(10000), "Synthetic junction creation timed out.");
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        try
        {
            Assert.Throws<SecurityException>(() => ServicePathLease.Acquire(new[] { child ? Path.Combine(link, "child") : link }));
            Assert.Equal("keep this synthetic file", File.ReadAllText(sentinel));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void PinnedSourceAndAncestorsCannotBeRenamedButNormalFileChangesStillWork()
    {
        using var ws = new TempWorkspace();
        var parent = ws.Sub("parent");
        var source = Directory.CreateDirectory(Path.Combine(parent, "source")).FullName;
        var file = Path.Combine(source, "document.txt");
        File.WriteAllText(file, "first");
        using (ServicePathLease.Acquire(new[] { source, source }))
        {
            Assert.Throws<IOException>(() => Directory.Move(source, Path.Combine(parent, "replacement")));
            Assert.Throws<IOException>(() => Directory.Move(parent, Path.Combine(ws.Root, "other-parent")));
            File.WriteAllText(file, "second");
            File.WriteAllText(Path.Combine(source, "new.txt"), "new");
            Directory.CreateDirectory(Path.Combine(source, "new-folder"));
            Assert.Equal("second", File.ReadAllText(file));
            var pending = Path.Combine(source, "pending.txt");
            File.WriteAllText(pending, "atomic save");
            File.Replace(pending, file, null);
            Assert.Equal("atomic save", File.ReadAllText(file));
            File.Move(file, Path.Combine(source, "renamed.txt"));
            File.Move(Path.Combine(source, "renamed.txt"), file);
        }
        AfterRelease(() => Directory.Move(source, Path.Combine(parent, "replacement")));
        Assert.True(File.Exists(Path.Combine(parent, "replacement", "document.txt")));
        Assert.Empty(Directory.GetFiles(Path.Combine(parent, "replacement"), ".backspace-path-guard-*"));
    }

    [Fact]
    public void EmptySourceStaysNonemptyAndGuardCannotBeDeletedWhileHeld()
    {
        using var ws = new TempWorkspace();
        var source = ws.Sub("empty");
        using (var lease = ServicePathLease.Acquire(new[] { source }))
        {
            var marker = Path.Combine(source, Assert.Single(lease.MarkerNames));
            Assert.Contains(marker, Directory.GetFiles(source));
            Assert.ThrowsAny<IOException>(() => File.Delete(marker));
            Assert.ThrowsAny<IOException>(() => File.Move(marker, marker + ".moved"));
        }
        // Delete-on-close completes after the last reader releases its handle;
        // Windows filters can briefly retain a compatible read handle.
        System.Threading.SpinWait.SpinUntil(() => Directory.GetFiles(source).Length == 0, TimeSpan.FromSeconds(2));
        Assert.Empty(Directory.GetFiles(source));
    }

    [Fact]
    public void HeldEmptySourceRejectsJunctionButSameMutationSucceedsAfterRelease()
    {
        using var ws = new TempWorkspace();
        var source = ws.Sub("empty");
        var target = ws.Sub("other-tree");
        var sentinel = Path.Combine(target, "untouched.txt");
        File.WriteAllText(sentinel, "unapproved tree");
        using (ServicePathLease.Acquire(new[] { source }))
            Assert.NotEqual(0, TrySetJunction(source, target));
        Assert.True(System.Threading.SpinWait.SpinUntil(() => TrySetJunction(source, target) == 0, TimeSpan.FromSeconds(2)),
            "The same junction mutation must succeed after our lease and pending guard deletion finish.");
        try { Assert.Equal("unapproved tree", File.ReadAllText(sentinel)); }
        finally { AfterRelease(() => Directory.Delete(source)); }
    }

    private static int TrySetJunction(string source, string target)
    {
        using var handle = CreateFile(source, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) return Marshal.GetLastWin32Error();
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var display = Encoding.Unicode.GetBytes(target);
        var buffer = new byte[16 + substitute.Length + 2 + display.Length + 2];
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)(buffer.Length - 8)).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buffer, 10);
        BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(buffer, 12);
        BitConverter.GetBytes((ushort)display.Length).CopyTo(buffer, 14);
        substitute.CopyTo(buffer, 16);
        display.CopyTo(buffer, 18 + substitute.Length);
        return DeviceIoControl(handle, 0x900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero)
            ? 0 : Marshal.GetLastWin32Error();
    }

    private static void AfterRelease(Action action)
    {
        // Windows filters may briefly retain a just-closed file or directory.
        // A leaked lease still fails this bounded assertion; never hide an
        // indefinite lock or retry the rejection assertions while held.
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try { action(); return; }
            catch (IOException ex) when ((ex.HResult & 0xffff) == 32 && elapsed.Elapsed < TimeSpan.FromSeconds(2))
            { System.Threading.Thread.Sleep(25); }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int count, IntPtr output, int outCount, out int used, IntPtr overlapped);

    [Fact]
    public void FailureReleasesAlreadyOpenedParents()
    {
        using var ws = new TempWorkspace();
        var parent = ws.Sub("parent");
        Assert.Throws<IOException>(() => ServicePathLease.Acquire(new[] { Path.Combine(parent, "missing") }));
        Directory.Move(parent, Path.Combine(ws.Root, "renamed"));
    }

    [Fact]
    public void AFileCannotReplaceAnApprovedDirectory()
    {
        using var ws = new TempWorkspace();
        var file = Path.Combine(ws.Root, "not-a-folder");
        File.WriteAllText(file, "synthetic");
        Assert.Throws<SecurityException>(() => ServicePathLease.Acquire(new[] { file }));
        File.Delete(file); // Every acquired handle must have been released on rejection.
    }
}
