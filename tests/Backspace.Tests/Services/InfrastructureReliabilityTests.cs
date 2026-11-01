using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class InfrastructureReliabilityTests
{
    [Fact]
    public void Config_failed_replacement_retains_pending_settings_and_does_not_change_current()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        var store = new ConfigStore(); store.Load();
        store.Update(c => c.Logging.RetentionDays = 44);
        var prior = File.ReadAllBytes(AppPaths.ConfigFile);
        using (var held = new FileStream(AppPaths.ConfigFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => store.Update(c => c.Logging.RetentionDays = 88));
        Assert.Equal(44, store.Current.Logging.RetentionDays);
        Assert.Equal(prior, File.ReadAllBytes(AppPaths.ConfigFile));
        var pending = Assert.Single(Directory.GetFiles(AppPaths.ConfigRoot, "config.json.*.tmp"));
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(pending));
        Assert.Equal(88, document.RootElement.GetProperty("logging").GetProperty("retentionDays").GetInt32());
        store.Update(c => c.Logging.RetentionDays = 99);
        Assert.Equal(prior, File.ReadAllBytes(AppPaths.ConfigFile + ".bak"));
        Assert.Equal(99, store.Current.Logging.RetentionDays);
    }

    [Fact]
    public void Config_missing_primary_with_backup_never_initializes_defaults()
    {
        using var workspace = new TempWorkspace();
        var store = new ConfigStore(); store.Load();
        File.Move(AppPaths.ConfigFile, AppPaths.ConfigFile + ".bak");
        var retained = File.ReadAllBytes(AppPaths.ConfigFile + ".bak");
        var reopened = new ConfigStore(); reopened.Load();
        Assert.False(reopened.IsLoaded);
        Assert.Throws<InvalidOperationException>(() => reopened.Update(c => c.Backups.Add(new Backup())));
        Assert.False(File.Exists(AppPaths.ConfigFile));
        Assert.Equal(retained, File.ReadAllBytes(AppPaths.ConfigFile + ".bak"));
    }

    [Fact]
    public async Task Config_on_demand_refresh_loads_external_changes_once_and_preserves_good_state_on_read_failure()
    {
        using var workspace = new TempWorkspace();
        var gui = new ConfigStore(); gui.Load();
        var external = new ConfigStore(); external.Load();
        var notifications = 0;
        gui.Changed += (_, _) => notifications++;
        Assert.False(await gui.ReloadIfChangedAsync());
        external.UpdateQuiet(c => c.Logging.RetentionDays = 83);
        Assert.True(await gui.ReloadIfChangedAsync());
        Assert.Equal(83, gui.Current.Logging.RetentionDays);
        Assert.Equal(1, notifications);
        Assert.False(await gui.ReloadIfChangedAsync());
        Assert.Equal(1, notifications);
        if (OperatingSystem.IsWindows())
        {
            using var held = new FileStream(AppPaths.ConfigFile, FileMode.Open, FileAccess.Read, FileShare.None);
            await Assert.ThrowsAsync<IOException>(() => gui.ReloadIfChangedAsync());
            Assert.Equal(83, gui.Current.Logging.RetentionDays);
            Assert.Equal(1, notifications);
        }
        external.UpdateQuiet(c => c.Logging.RetentionDays = 19);
        Assert.True(await gui.ReloadIfChangedAsync());
        Assert.Equal(19, gui.Current.Logging.RetentionDays);
        Assert.Equal(2, notifications);
        File.WriteAllText(AppPaths.ConfigFile, "not valid configuration");
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => gui.ReloadIfChangedAsync());
        Assert.Equal(19, gui.Current.Logging.RetentionDays);
        Assert.Equal(2, notifications);
    }

    [Fact]
    public void Config_independent_stale_instances_preserve_each_others_edits()
    {
        using var workspace = new TempWorkspace();
        var gui = new ConfigStore(); gui.Load();
        var task = new ConfigStore(); task.Load();
        gui.Update(c => c.Backups.Add(new Backup { Id = "one", Name = "User backup" }));
        task.UpdateQuiet(c => c.Logging.RetentionDays = 72);
        gui.Update(c => c.Backups.Add(new Backup { Id = "two", Name = "Another backup" }));
        task.Save(); // A stale Save must not replace newly added backups.
        var reopened = new ConfigStore(); reopened.Load();
        Assert.Equal(new[] { "one", "two" }, reopened.Current.Backups.Select(x => x.Id));
        Assert.Equal(72, reopened.Current.Logging.RetentionDays);
    }

    [Fact]
    public async Task Config_concurrent_instances_do_not_lose_additions()
    {
        using var workspace = new TempWorkspace();
        var initial = new ConfigStore(); initial.Load();
        var stores = Enumerable.Range(0, 16).Select(_ => new ConfigStore()).ToArray();
        foreach (var store in stores) store.Load();
        await Task.WhenAll(stores.Select((store, i) => Task.Run(() =>
            store.Update(c => c.Backups.Add(new Backup { Id = i.ToString(), Name = $"Backup {i}" })))));
        initial.Load();
        Assert.Equal(16, initial.Current.Backups.Count);
        Assert.Equal(16, initial.Current.Backups.Select(x => x.Id).Distinct().Count());
    }

    [Fact]
    public void Config_failed_mutator_and_quiet_save_never_dirty_current_or_disk()
    {
        using var workspace = new TempWorkspace();
        var store = new ConfigStore(); store.Load();
        store.Update(c => c.Logging.RetentionDays = 44);
        Assert.Throws<InvalidOperationException>(() => store.UpdateQuiet(c =>
        {
            c.Logging.RetentionDays = 99;
            throw new InvalidOperationException("injected mutation failure");
        }));
        Assert.Equal(44, store.Current.Logging.RetentionDays);
        if (OperatingSystem.IsWindows())
        {
            using var held = new FileStream(AppPaths.ConfigFile, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.Throws<IOException>(() => store.UpdateQuiet(c => c.Logging.RetentionDays = 88));
            Assert.Equal(44, store.Current.Logging.RetentionDays);
        }
        var reopened = new ConfigStore(); reopened.Load();
        Assert.Equal(44, reopened.Current.Logging.RetentionDays);
    }

    [Fact]
    public void Config_unloaded_update_is_a_visible_failure_and_does_not_run_mutator()
    {
        using var workspace = new TempWorkspace();
        var ran = false;
        var store = new ConfigStore();
        Assert.Throws<InvalidOperationException>(() => store.Update(_ => ran = true));
        Assert.False(ran);
        Assert.False(File.Exists(AppPaths.ConfigFile));
    }

    [Fact]
    public void Saving_editor_draft_preserves_new_run_results_but_keeps_user_edits()
    {
        using var workspace = new TempWorkspace();
        var gui = new ConfigStore(); gui.Load();
        var draft = new Backup { Id = "edited", Name = "Before", SnapshotIdSeed = "original", SourcePaths = { "keep", "remove" } };
        gui.SaveBackupEdit(draft);
        var task = new ConfigStore(); task.Load();
        var completed = DateTime.UtcNow;
        task.UpdateQuiet(c =>
        {
            var backup = c.Backups.Single();
            backup.LastRunStatus = BackupRunStatus.Success;
            backup.LastRunEndUtc = completed;
            backup.LastSuccessUtc = completed;
            backup.LastRunSummary = "New completed run";
            backup.LastVerifyPass = true;
            backup.LastVerifyWasSample = true;
            backup.LastVerifyUtc = completed;
            backup.LastNotificationFiredUtc = completed;
            backup.LastNotifiedStatus = BackupRunStatus.Success;
            backup.SourceLastSizeBytes["keep"] = 456;
            backup.SourceLastSizeBytes["remove"] = 789;
        });
        draft.Name = "After";
        draft.SourcePaths.Remove("remove");
        draft.SkipNotificationGraceDays = 12;
        draft.SkipNotificationGraceResetUtc = completed.AddHours(1); // explicit new user reset
        draft.SnapshotIdSeed = "must-not-replace-identity";
        gui.SaveBackupEdit(draft);
        var saved = gui.Current.Backups.Single();
        Assert.Equal("After", saved.Name);
        Assert.Equal(12, saved.SkipNotificationGraceDays);
        Assert.Equal(completed.AddHours(1), saved.SkipNotificationGraceResetUtc);
        Assert.Equal("original", saved.SnapshotIdSeed);
        Assert.Equal(BackupRunStatus.Success, saved.LastRunStatus);
        Assert.Equal(completed, saved.LastRunEndUtc);
        Assert.Equal(completed, saved.LastSuccessUtc);
        Assert.Equal("New completed run", saved.LastRunSummary);
        Assert.True(saved.LastVerifyPass);
        Assert.True(saved.LastVerifyWasSample);
        Assert.Equal(completed, saved.LastVerifyUtc);
        Assert.Equal(completed, saved.LastNotificationFiredUtc);
        Assert.Equal(456, saved.SourceLastSizeBytes["keep"]);
        Assert.False(saved.SourceLastSizeBytes.ContainsKey("remove"));

        // A user's explicit erase/reset must not resurrect draft's old success.
        gui.Update(c => c.Backups.Single().LastRunStatus = BackupRunStatus.NeverRun);
        draft.LastRunStatus = BackupRunStatus.Success;
        gui.SaveBackupEdit(draft);
        Assert.Equal(BackupRunStatus.NeverRun, gui.Current.Backups.Single().LastRunStatus);
    }

    [Fact]
    public void Secrets_stale_instances_reload_for_reads_writes_and_removes()
    {
        using var workspace = new TempWorkspace();
        var first = new SecretsStore(new PlainEncryption());
        var second = new SecretsStore(new PlainEncryption());
        Assert.False(first.TryGet("a", out _));
        Assert.False(second.TryGet("b", out _));
        first.Set("a", "first");
        second.Set("b", "second");
        Assert.Equal("first", second.Get("a"));
        Assert.Equal("second", first.Get("b"));
        second.Remove("a");
        first.Set("c", "third");
        Assert.False(first.TryGet("a", out _));
        Assert.Equal("second", first.Get("b"));
        Assert.Equal("third", second.Get("c"));
    }

    [Fact]
    public void Secrets_transient_locked_file_is_neither_corruption_nor_empty_vault()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        var first = new SecretsStore(new PlainEncryption());
        first.Set("existing", "keep-me");
        var original = File.ReadAllBytes(AppPaths.SecretsFile);
        var second = new SecretsStore(new PlainEncryption());
        using (var held = new FileStream(AppPaths.SecretsFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => second.Get("existing"));
            Assert.Throws<IOException>(() => second.Set("new", "must-not-persist"));
            Assert.Null(second.LoadErrorMessage);
            Assert.Null(second.PreservedCorruptPath);
        }
        Assert.Equal(original, File.ReadAllBytes(AppPaths.SecretsFile));
        Assert.Empty(Directory.GetFiles(AppPaths.ConfigRoot, "*.unreadable-*"));
        Assert.Equal("keep-me", second.Get("existing"));
        Assert.False(second.TryGet("new", out _));
        second.Set("new", "retry-success");
        Assert.Equal("keep-me", first.Get("existing"));
    }

    [Fact]
    public async Task Secrets_temporary_replacement_lock_preserves_existing_and_new_values()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        var store = new SecretsStore(new PlainEncryption());
        store.Set("existing", "synthetic-first");
        var held = new FileStream(AppPaths.SecretsFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        var saving = Task.Run(() => store.Set("new", "synthetic-second"));
        try { await Task.Delay(150, TestContext.Current.CancellationToken); Assert.False(saving.IsCompleted); }
        finally { held.Dispose(); }
        await saving.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var reopened = new SecretsStore(new PlainEncryption());
        Assert.Equal("synthetic-first", reopened.Get("existing"));
        Assert.Equal("synthetic-second", reopened.Get("new"));
    }

    [Fact]
    public void Secrets_missing_primary_with_backup_cannot_silently_create_empty_vault()
    {
        using var workspace = new TempWorkspace();
        var store = new SecretsStore(new PlainEncryption());
        store.Set("existing", "synthetic-first");
        File.Move(AppPaths.SecretsFile, AppPaths.SecretsFile + ".bak");
        var retained = File.ReadAllBytes(AppPaths.SecretsFile + ".bak");
        Assert.Throws<FileNotFoundException>(() => store.Set("replacement", "must-not-replace"));
        Assert.False(File.Exists(AppPaths.SecretsFile));
        Assert.Equal(retained, File.ReadAllBytes(AppPaths.SecretsFile + ".bak"));
    }

    [Fact]
    public async Task Secrets_concurrent_independent_instances_preserve_all_values()
    {
        using var workspace = new TempWorkspace();
        var stores = Enumerable.Range(0, 16).Select(_ => new SecretsStore(new PlainEncryption())).ToArray();
        await Task.WhenAll(stores.Select((store, i) => Task.Run(() => store.Set($"key-{i}", $"value-{i}"))));
        var reopened = new SecretsStore(new PlainEncryption());
        for (var i = 0; i < stores.Length; i++) Assert.Equal($"value-{i}", reopened.Get($"key-{i}"));
    }

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(3, 1, false)]
    [InlineData(0, 2, false)]
    [InlineData(0, 3, false)]
    [InlineData(0, 1, true)]
    [InlineData(2, 1, true)]
    public void Reaper_actual_record_path_only_terminates_verified_child_of_dead_owner(
        int ownerCode, int childCode, bool shouldKill)
    {
        using var workspace = new TempWorkspace();
        var identity = new OwnedProcessRegistry.Identity(200, 456, "owned-rclone.exe");
        var ownership = new OwnedProcessRegistry.Ownership(new(100, 123, "owner.exe"), identity);
        var path = Path.Combine(workspace.Root, "owner.json");
        OwnedProcessRegistry.Write(path, ownership);
        var killed = new List<OwnedProcessRegistry.Identity>();
        OwnedProcessRegistry.Reap(path, p => (OwnedProcessRegistry.Presence)(p.Pid == 100 ? ownerCode : childCode), killed.Add);
        Assert.Equal(shouldKill ? 1 : 0, killed.Count);
        if (shouldKill) Assert.Equal(identity, killed.Single());
    }

    [Fact]
    public void Process_identity_requires_exact_creation_time_not_only_path_or_pid()
    {
        var original = new OwnedProcessRegistry.Identity(42, 1_000_000, "rclone.exe");
        Assert.False(OwnedProcessRegistry.Matches(original, original with { StartedUtcTicks = 1_000_001 }));
        Assert.False(OwnedProcessRegistry.Matches(original, original with { BinaryPath = "unrelated.exe" }));
        Assert.False(OwnedProcessRegistry.Matches(original, original with { Pid = 43 }));
        Assert.True(OwnedProcessRegistry.Matches(original, original));
    }

    [Fact]
    public void Reaper_legacy_path_only_record_is_never_authority_to_kill()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "sidecar.pid");
        File.WriteAllText(path, "42|bundled-backrest.exe");
        OwnedProcessRegistry.Reap(path, _ => throw new Exception("Must not inspect ambiguous record"),
            _ => throw new Exception("Must not kill from ambiguous record"));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Sidecar_same_directory_second_owner_cannot_touch_first_owner()
    {
        using var workspace = new TempWorkspace();
        var data = workspace.Sub("engine");
        using (var first = BackrestSidecar.AcquireDataDirectoryLease(data))
        {
            var exception = Assert.Throws<IOException>(() => BackrestSidecar.AcquireDataDirectoryLease(data));
            Assert.Contains("left untouched", exception.Message);
            using var independent = BackrestSidecar.AcquireDataDirectoryLease(workspace.Sub("other-engine"));
        }
        using var afterExit = BackrestSidecar.AcquireDataDirectoryLease(data);
    }

    [Fact]
    public async Task Sidecar_lease_waits_out_a_brief_scanner_handle()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        var data = workspace.Sub("engine");
        // A scanner-style handle shares read/write but not deletion, which still blocks an exclusive open.
        var scan = new FileStream(Path.Combine(data, "sidecar.owner.lock"), FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
        var release = Task.Delay(300, TestContext.Current.CancellationToken).ContinueWith(_ => scan.Dispose(), TaskScheduler.Default);
        try { using var lease = BackrestSidecar.AcquireDataDirectoryLease(data); }
        finally { await release; scan.Dispose(); }
    }

    [Fact]
    public void Elevated_helper_receives_explicit_config_root_and_exact_parent_identity()
    {
        using var workspace = new TempWorkspace();
        var start = VssElevationCoordinator.BuildElevatedStartInfo(Environment.ProcessPath!,
            new VssElevationRequest(new Backup { Id = "backup" }, "C:/source", new Destination { Id = "destination" }, "default"),
            Path.Combine(workspace.Root, "result.json"));
        var parsed = VssElevationCoordinator.ParseHelperArgs(start.ArgumentList.ToArray());
        Assert.NotNull(parsed);
        Assert.Equal(Path.GetFullPath(workspace.ConfigRoot), parsed.ConfigRoot);
        using var owner = Process.GetCurrentProcess();
        Assert.Equal(owner.Id, parsed.ParentPid);
        Assert.Equal(owner.StartTime.ToUniversalTime().Ticks, parsed.ParentStartTimeTicks);
        var first = BackrestSidecar.CreateIsolatedDataDirectory("vss");
        var second = BackrestSidecar.CreateIsolatedDataDirectory("vss");
        Assert.NotEqual(first, second);
        Assert.StartsWith(workspace.ConfigRoot, first);
        Assert.NotEqual(AppPaths.BackrestDataDir, first);
    }

    [Fact]
    public async Task State_transaction_lock_is_enforced_across_processes_and_released_on_exit()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        var statePath = Path.Combine(workspace.Root, "state.json");
        var ready = Path.Combine(workspace.Root, "ready");
        var release = Path.Combine(workspace.Root, "release");
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var script = "$f=[IO.File]::Open(" + Quote(statePath + ".lock") +
            ",[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);" +
            "try { [IO.File]::WriteAllText(" + Quote(ready) + ",'ready');" +
            "$until=[DateTime]::UtcNow.AddSeconds(15); while (![IO.File]::Exists(" + Quote(release) +
            ") -and [DateTime]::UtcNow -lt $until) { Start-Sleep -Milliseconds 25 } } finally { $f.Dispose() }";
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var child = Process.Start(info)!;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(ready) && !child.HasExited && DateTime.UtcNow < deadline)
                await Task.Delay(25);
            Assert.True(File.Exists(ready), "Isolated file-lock child did not report ready.");
            Assert.Throws<IOException>(() => StateFileLock.Acquire(statePath, TimeSpan.FromMilliseconds(100)));
        }
        finally
        {
            File.WriteAllText(release, "release");
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
            await child.WaitForExitAsync(timeout.Token);
        }
        using var acquired = StateFileLock.Acquire(statePath, TimeSpan.FromMilliseconds(100));
    }

    private sealed class PlainEncryption : ISecretsEncryption
    {
        public string DisplayName => "isolated-test";
        public byte[] Protect(byte[] plaintext, byte[]? entropy) => plaintext;
        public byte[] Unprotect(byte[] ciphertext, byte[]? entropy) => ciphertext;
    }
}
