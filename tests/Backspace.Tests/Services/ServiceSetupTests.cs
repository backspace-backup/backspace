using System;
using System.IO;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Linq;
using System.Runtime.InteropServices;
using Backspace.Services.Platform.Windows;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceSetupTests
{
    [Fact]
    public void Empty_listener_diagnostics_do_not_block_cancellation_but_any_migration_state_does()
    {
        using var ws = new TempWorkspace();
        var data = ws.Sub("private"); var id = Guid.NewGuid().ToString("N");
        var diagnostic = Path.Combine(data, "migration-" + id + ".receipt.progress");
        File.WriteAllText(diagnostic, "{\"stage\":\"waiting-for-connection\",\"utc\":\"2026-10-20T20:00:00Z\"}");
        Assert.False(ServiceSetupRecovery.HasPrivateState(data, id));
        Assert.True(ServiceSetupRecovery.HasPrivateState(data, Guid.NewGuid().ToString("N")));
        var reservation = Path.Combine(data, "migration-" + id + ".receipt");
        File.WriteAllText(reservation, "reserved");
        Assert.True(ServiceSetupRecovery.HasPrivateState(data, id)); File.Delete(reservation);
        File.WriteAllText(Path.Combine(data, "secrets.bin"), "retained encrypted vault");
        Assert.True(ServiceSetupRecovery.HasPrivateState(data, id));
    }

    [Fact]
    public void Pinned_cleanup_removes_only_the_inspected_tree()
    {
        using var ws = new TempWorkspace();
        var root = ws.Sub("discarded-setup");
        var child = Directory.CreateDirectory(Path.Combine(root, "control")).FullName;
        File.WriteAllText(Path.Combine(child, "journal.json"), "synthetic journal");
        var outside = Path.Combine(ws.Root, "keep.txt"); File.WriteAllText(outside, "keep");
        using (var held = new ServiceObjectPermissions(root))
        {
            ServiceSetup.RemoveNewTree(root, held);
            Assert.Throws<IOException>(() => Directory.Move(root, root + "-moved"));
        }
        Assert.False(Directory.Exists(root)); Assert.Equal("keep", File.ReadAllText(outside));
    }

    [Fact]
    public void Recovery_journal_is_bound_to_one_copy_and_all_original_objects()
    {
        const string root = @"C:\Portable";
        var owner = WindowsIdentity.GetCurrent().User!.Value;
        var binding = new ServiceWorkerBinding(2, "Backspace-" + Guid.NewGuid().ToString("N"), owner, ServicePortableLayout.Data(root));
        var permissions = ServiceSetup.ProgramFiles.Concat(new[] { ".", AppPaths.AppFolderName })
            .Select(p => new ServiceSetup.PermissionRecord(p, "O:BAG:BAD:P(A;;FA;;;BA)")).ToArray();
        var ids = permissions.ToDictionary(p => p.RelativePath, _ => "12345678:0000000000000001");
        var journal = new ServiceSetupRecovery.Journal(1, root, binding, permissions, ids);
        ServiceSetupRecovery.Validate(journal, root);
        Assert.Throws<SecurityException>(() => ServiceSetupRecovery.Validate(journal, @"C:\AnotherCopy"));
        Assert.Throws<SecurityException>(() => ServiceSetupRecovery.Validate(journal with { Binding = binding with { Activated = true } }, root));
        ids.Remove("Backspace.exe");
        Assert.Throws<SecurityException>(() => ServiceSetupRecovery.Validate(journal, root));
    }

    [Fact]
    public void FilePermissionLeaseRefusesHardLinksAndPinsTheInspectedObject()
    {
        using var ws = new TempWorkspace();
        var file = Path.Combine(ws.Root, "program.bin");
        var alias = Path.Combine(ws.Root, "other-copy.bin");
        File.WriteAllText(file, "keep bytes and permissions");
        var original = ServiceSetup.ReadPermissions(file);
        Assert.True(CreateHardLinkW(alias, file, IntPtr.Zero));
        Assert.Throws<SecurityException>(() => new ServiceObjectPermissions(file, directory: false));
        Assert.Equal(original, ServiceSetup.ReadPermissions(file));
        File.Delete(alias);
        using (var held = new ServiceObjectPermissions(file, directory: false))
        {
            // Windows permits creating a hard link despite read-only sharing.
            // It cannot substitute a different object underneath this handle.
            Assert.True(CreateHardLinkW(alias, file, IntPtr.Zero));
            Assert.Throws<SecurityException>(() => held.Set(original));
            Assert.Equal(original, ServiceSetup.ReadPermissions(alias));
            Assert.Throws<IOException>(() => File.Move(file, file + ".moved"));
            Assert.Equal("keep bytes and permissions", File.ReadAllText(file));
        }
        Assert.True(File.Exists(alias));
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLinkW(string alias, string target, IntPtr attributes);

    [Fact]
    public void PermissionRecoveryCannotNameAnotherFolderOrRepeatAnEntry()
    {
        const string acl = "O:BAG:BAD:P(A;;FA;;;BA)";
        var records = ServiceSetup.ProgramFiles.Concat(new[] { ".", "Backspace.config" })
            .Select(path => new ServiceSetup.PermissionRecord(path, acl)).ToArray();
        ServiceSetup.ValidatePermissionRecords(records);
        var original = records[0];
        records[0] = new(@"..\other", acl);
        Assert.Throws<SecurityException>(() => ServiceSetup.ValidatePermissionRecords(records));
        records[0] = original; records[1] = original;
        Assert.Throws<SecurityException>(() => ServiceSetup.ValidatePermissionRecords(records));
    }

    [Fact]
    public void PreparingVersionTwoDoesNotAuthorizeAnyBackup()
    {
        var prepared = new ServiceWorkerBinding(2, "Backspace-test", "owner", "data");
        Assert.False(prepared.CanRun);
        Assert.True((prepared with { Activated = true }).CanRun);
    }
    [Fact]
    public void PrivateDataCannotBeRedirectedOutsideThePortableCopy()
    {
        const string root = @"D:\My portable backup";
        var binding = new ServiceWorkerBinding(2, "Backspace-fixture", "owner", ServicePortableLayout.Data(root));
        ServicePortableLayout.ValidateDataLocation(binding, root);
        Assert.Throws<SecurityException>(() => ServicePortableLayout.ValidateDataLocation(binding with { DataDirectory = @"C:\ProgramData\Backspace" }, root));
        Assert.Throws<SecurityException>(() => ServicePortableLayout.ValidateDataLocation(binding with { DataDirectory = @"D:\My portable backup\Backspace.config\other" }, root));
    }

    [Fact]
    public void ANewBindingNeverMixesInLegacyApprovalOrMigrationFiles()
    {
        using var ws = new TempWorkspace();
        File.WriteAllText(Path.Combine(ws.Root, ServiceWorkerBinding.FileName), "legacy");
        Assert.Equal(Path.Combine(ws.Root, "service-approval.json"), ServicePortableLayout.ExistingRecord(ws.Root, "service-approval.json"));
        Directory.CreateDirectory(ServicePortableLayout.Control(ws.Root));
        File.WriteAllText(ServicePortableLayout.Record(ws.Root, ServiceWorkerBinding.FileName), "new binding, deliberately not parsed here");
        Assert.Equal(ServicePortableLayout.Record(ws.Root, "service-approval.json"), ServicePortableLayout.ExistingRecord(ws.Root, "service-approval.json"));
        Assert.False(File.Exists(ServicePortableLayout.ExistingRecord(ws.Root, "service-approval.json")));
    }

    [Fact]
    public void SetupAcceptsUnrelatedFilesButStillNeedsEveryEngine()
    {
        using var ws = new TempWorkspace();
        var root = ws.Sub("published");
        foreach (var name in ServiceSetup.ProgramFiles) File.WriteAllText(Path.Combine(root, name), "synthetic, never executed");
        var unrelated = Path.Combine(root, "my-document.txt"); File.WriteAllText(unrelated, "keep this");
        var before = ServiceSetup.ReadPermissions(root);
        ServiceSetup.ValidateContents(root);
        File.Delete(Path.Combine(root, "rclone.exe"));
        Assert.Throws<FileNotFoundException>(() => ServiceSetup.ValidateContents(root));
        Assert.Equal(before, ServiceSetup.ReadPermissions(root));
        Assert.Equal("keep this", File.ReadAllText(unrelated));
    }

    [Fact]
    public void ServicePreflightAcceptsAnOrdinaryParentWithoutChangingAnything()
    {
        // TempWorkspace's parent lets the current account replace children.
        // Where Backspace lives is the owner's choice (27 October 2026), so
        // the preflight accepts it and still changes nothing.
        using var ws = new TempWorkspace("service-location-preflight");
        var root = ws.Sub("portable-copy");
        foreach (var name in ServiceSetup.ProgramFiles)
            File.WriteAllText(Path.Combine(root, name), "synthetic, never executed");
        Directory.CreateDirectory(Path.Combine(root, AppPaths.AppFolderName));
        var before = ServiceSetup.ReadPermissions(root);

        Assert.Equal(root, ServiceSetup.ValidatePreparationPreflight(root));
        // Other files may share the folder, for example a Downloads folder.
        File.WriteAllText(Path.Combine(root, "unrelated-download.pdf"), "not Backspace");
        Assert.Equal(root, ServiceSetup.ValidatePreparationPreflight(root));
        File.Delete(Path.Combine(root, "unrelated-download.pdf"));

        Assert.Equal(before, ServiceSetup.ReadPermissions(root));
        Assert.All(ServiceSetup.ProgramFiles, name => Assert.Equal("synthetic, never executed", File.ReadAllText(Path.Combine(root, name))));
        Assert.True(Directory.Exists(Path.Combine(root, AppPaths.AppFolderName)));
    }

    [Fact]
    public void SetupAcceptsTheDesktopLifetimeLockAndUnrelatedFilesButNotANonemptyLock()
    {
        using var ws = new TempWorkspace();
        var root = ws.Sub("desktop-started");
        foreach (var name in ServiceSetup.ProgramFiles) File.WriteAllText(Path.Combine(root, name), "synthetic, never executed");
        using (var desktop = WindowsUpdateRuntime.AcquireShared(root, TimeSpan.FromSeconds(1)))
        {
            Assert.NotNull(desktop);
            ServiceSetup.ValidateContents(root);
            var gate = Path.Combine(root, WindowsUpdateRuntime.GateName);
            using var pin = new ServiceObjectPermissions(gate, directory: false);
            var original = pin.Read();
            pin.Set(original); // Metadata changes remain possible while the desktop holds its shared read lock.
            Assert.Throws<IOException>(() => File.Move(gate, gate + ".replacement"));
            Assert.Null(WindowsUpdateRuntime.TryExclusive(root));
            var unrelated = Path.Combine(root, "unrelated.lock"); File.WriteAllText(unrelated, "keep");
            ServiceSetup.ValidateContents(root);
            Assert.Equal("keep", File.ReadAllText(unrelated));
            File.Delete(unrelated);
        }
        File.WriteAllText(Path.Combine(root, WindowsUpdateRuntime.GateName), "not a lock");
        Assert.Throws<IOException>(() => ServiceSetup.ValidateContents(root));
    }

    [Fact]
    public void PermissionJournalIncludesTheDesktopGateOnlyWhenItWasProtected()
    {
        const string acl = "O:BAG:BAD:P(A;;FA;;;BA)";
        var older = ServiceSetup.ProgramFiles.Concat(new[] { ".", AppPaths.AppFolderName })
            .Select(p => new ServiceSetup.PermissionRecord(p, acl)).ToArray();
        var current = older.Append(new ServiceSetup.PermissionRecord(WindowsUpdateRuntime.GateName, acl)).ToArray();
        ServiceSetup.ValidatePermissionRecords(older);
        ServiceSetup.ValidatePermissionRecords(current);
        Assert.Equal(ServiceSetup.ProgramFiles, ServiceSetup.PermissionFiles(older));
        Assert.Contains(WindowsUpdateRuntime.GateName, ServiceSetup.PermissionFiles(current));
        Assert.Throws<SecurityException>(() => ServiceSetup.ValidatePermissionRecords(current.Append(current[^1]).ToArray()));
        Assert.Throws<SecurityException>(() => ServiceSetup.ValidatePermissionRecords(current.Append(new("other.lock", acl)).ToArray()));
    }

    [Fact]
    public void Installation_folder_permissions_change_while_a_program_runs_from_that_folder()
    {
        // Double-clicking Backspace.exe makes its folder the program's current
        // directory. Windows holds that folder without delete sharing, which
        // made enabling automatic backups fail with "being used by another
        // process" (U023). The folder handle must not need delete access, and
        // the change must still leave existing children untouched.
        using var ws = new TempWorkspace();
        var root = Directory.CreateDirectory(Path.Combine(ws.OrdinaryFolder("user"), "portable")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(root, "Backspace.config")).FullName;
        var child = Path.Combine(nested, "config.json"); File.WriteAllText(child, "preserve");
        var oldRoot = ServiceSetup.ReadPermissions(root);
        var oldNested = ServiceSetup.ReadPermissions(nested);
        var oldChild = ServiceSetup.ReadPermissions(child);
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        using var holder = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c ping -n 30 127.0.0.1 >nul")
            { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            // The original handle kind is blocked once the program has started.
            Exception? blocked = null;
            for (var waited = 0; waited < 10_000 && blocked is null; waited += 100)
            {
                blocked = Record.Exception(() => new ServiceObjectPermissions(root).Dispose());
                if (blocked is null) System.Threading.Thread.Sleep(100);
            }
            Assert.True(blocked is System.ComponentModel.Win32Exception { NativeErrorCode: 32 }, blocked?.ToString() ?? "opened");
            using (var lease = new ServiceObjectPermissions(root, workingFolder: true))
            {
                lease.Set($"O:{sid}D:P(A;OICI;FA;;;{sid})(A;OICIIO;FR;;;BU)");
                var changed = ServiceSetup.ReadPermissions(root);
                Assert.NotEqual(oldRoot, changed);
                var changedDescriptor = new RawSecurityDescriptor(changed);
                Assert.Equal(sid, changedDescriptor.Owner!.Value);
                Assert.True((changedDescriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0);
                Assert.Equal(oldNested, ServiceSetup.ReadPermissions(nested));
                Assert.Equal(oldChild, ServiceSetup.ReadPermissions(child));
                Assert.Throws<IOException>(() => Directory.Move(root, root + "-moved"));
                // New children still inherit the folder's new rules.
                var future = Path.Combine(root, "future.txt"); File.WriteAllText(future, "new");
                Assert.Contains(";;;BU)", ServiceSetup.ReadPermissions(future));
                File.Delete(future);
                lease.Set(oldRoot);
            }
            Assert.Equal(oldRoot, ServiceSetup.ReadPermissions(root));
            Assert.Equal(oldChild, ServiceSetup.ReadPermissions(child));
            Assert.Equal("preserve", File.ReadAllText(child));
        }
        finally { try { holder.Kill(entireProcessTree: true); holder.WaitForExit(5000); } catch (InvalidOperationException) { } }
    }

    /// <summary>Setup no longer changes the program folder's permissions (28 October
    /// 2026), so removal and recovery write back the same permissions they recorded.
    /// That must leave every child exactly as it was, inherited entries included.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restoring_the_recorded_permissions_leaves_children_untouched(bool workingFolder)
    {
        using var ws = new TempWorkspace();
        var root = Directory.CreateDirectory(Path.Combine(ws.OrdinaryFolder("user"), "recorded")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(root, "Backspace.config")).FullName;
        var child = Path.Combine(nested, "config.json"); File.WriteAllText(child, "preserve");
        var before = new[] { root, nested, child }.Select(ServiceSetup.ReadPermissions).ToArray();
        using (var lease = new ServiceObjectPermissions(root, workingFolder: workingFolder)) lease.Set(before[0]);
        // Windows marks a rewritten list auto-inherited (AI); its entries are unchanged.
        Assert.Equal(before.Select(WithoutAutoInheritFlag), new[] { root, nested, child }.Select(ServiceSetup.ReadPermissions).Select(WithoutAutoInheritFlag));
    }

    internal static string WithoutAutoInheritFlag(string sddl) => sddl.Replace("D:AI(", "D:(").Replace("D:PAI(", "D:P(");

    [Fact]
    public void DirectoryPermissionChangeDoesNotRewriteExistingChildPermissions()
    {
        using var ws = new TempWorkspace();
        var root = Directory.CreateDirectory(Path.Combine(ws.OrdinaryFolder("user"), "permissions")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(root, "existing")).FullName;
        var child = Path.Combine(nested, "saved-data.txt"); File.WriteAllText(child, "preserve");
        var oldRoot = ServiceSetup.ReadPermissions(root);
        var oldNested = ServiceSetup.ReadPermissions(nested);
        var oldChild = ServiceSetup.ReadPermissions(child);
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        using (var lease = new ServiceObjectPermissions(root))
        {
            try
            {
                lease.Set($"O:{sid}D:P(A;;FA;;;{sid})(A;OICIIO;FR;;;BU)");
                Assert.NotEqual(oldRoot, ServiceSetup.ReadPermissions(root));
                Assert.Equal(oldNested, ServiceSetup.ReadPermissions(nested));
                Assert.Equal(oldChild, ServiceSetup.ReadPermissions(child));
                Assert.Throws<IOException>(() => Directory.Move(root, root + "-moved"));
                Assert.Equal("preserve", File.ReadAllText(child));
            }
            finally { lease.Set(oldRoot); }
        }
        Assert.Equal(oldRoot, ServiceSetup.ReadPermissions(root));
        Assert.Equal(oldChild, ServiceSetup.ReadPermissions(child));
    }

    [Fact]
    public void PinnedDirectoryRenameUsesTheExactNameAndNeverReplacesExistingData()
    {
        using var ws = new TempWorkspace();
        var root = ws.Sub("retained-service");
        File.WriteAllText(Path.Combine(root, "history.txt"), "keep history");
        var target = Path.Combine(ws.Root, "archive-9585dc67fc6b4cd5a094424b00052059");
        using (var held = new ServiceObjectPermissions(root)) held.MoveTo(target);
        Assert.False(Directory.Exists(root));
        Assert.Equal("keep history", File.ReadAllText(Path.Combine(target, "history.txt")));
        Assert.Single(Directory.GetDirectories(ws.Root, "archive-*"));
        var another = ws.Sub("another-service");
        File.WriteAllText(Path.Combine(another, "other.txt"), "keep other");
        using (var held = new ServiceObjectPermissions(another))
            Assert.Throws<System.ComponentModel.Win32Exception>(() => held.MoveTo(target));
        Assert.Equal("keep other", File.ReadAllText(Path.Combine(another, "other.txt")));
        Assert.Equal("keep history", File.ReadAllText(Path.Combine(target, "history.txt")));
    }

    [Fact]
    public void ServiceCommandIsQuotedAndContainsNoCallerSuppliedArguments()
    {
        Assert.Equal("\"D:\\Portable Apps\\Backspace.exe\" --service-host", ServiceRegistration.Command(@"D:\Portable Apps\Backspace.exe"));
        Assert.Throws<SecurityException>(() => ServiceRegistration.Command(@"D:\Portable\another.exe"));
        Assert.Throws<SecurityException>(() => ServiceRegistration.Command("D:\\Portable\\Backspace.exe\" --other"));
    }
}
