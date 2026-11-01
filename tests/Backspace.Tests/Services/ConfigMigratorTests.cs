using System;
using System.IO;
using System.Linq;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Covers both layouts the importer must handle:
///   1. plain Duplicacy — picked folder IS the repo (contains .duplicacy/preferences)
///   2. duplicacy-util — picked folder CONTAINS many repo folders, plus per-repo yamls
/// Plus the "no repos found" path so the friendly error text doesn't regress.
/// </summary>
public class ConfigMigratorTests
{
    [Fact]
    public void PlainUser_singleRepoLayout_importsBackupAndDestination()
    {
        using var sandbox = new ConfigSandbox();
        using var src = new TempDir();

        // Layout: <repoRoot>/.duplicacy/preferences
        var repoRoot = src.Path;
        var duplDir = Path.Combine(repoRoot, ".duplicacy");
        Directory.CreateDirectory(duplDir);
        File.WriteAllText(Path.Combine(duplDir, "preferences"),
            """
            [
              {
                "name": "default",
                "id": "my-laptop",
                "repository": "C:\\Users\\jane\\Documents",
                "storage": "dropbox://Apps/Backups/me",
                "encrypted": true
              }
            ]
            """);
        // Filters file should be picked up too.
        File.WriteAllText(Path.Combine(duplDir, "filters"), "+*.txt\n-*.log\n");

        var store = CreateLoadedStore();
        var migrator = new ConfigMigrator(store);
        var report = migrator.MigrateFrom(repoRoot);

        Assert.False(report.Skipped, report.Reason);
        Assert.Empty(report.Errors);
        Assert.Single(report.ImportedBackups);

        var cfg = store.Current;
        var backup = Assert.Single(cfg.Backups);
        Assert.Equal("my-laptop", backup.Name);                       // id from preferences wins
        Assert.Equal(new[] { @"C:\Users\jane\Documents" }, backup.SourcePaths.ToArray());
        Assert.Contains("+*.txt", backup.FiltersText);
        Assert.Contains("-*.log", backup.FiltersText);

        var dest = Assert.Single(cfg.Destinations);
        Assert.Equal(DestinationKind.DropboxAppScoped, dest.Kind);
        Assert.Equal("Apps/Backups/me", dest.PathOrSubpath);
        Assert.True(dest.Encrypted);

        // Backup.Target points at the imported destination, with the legacy storage name.
        var target = Assert.Single(backup.Targets);
        Assert.Equal(dest.Id, target.DestinationId);
        Assert.Equal("default", target.StorageName);

        var reopened = CreateLoadedStore();
        Assert.Equal(backup.Id, Assert.Single(reopened.Current.Backups).Id);
        Assert.Equal(dest.Id, Assert.Single(reopened.Current.Destinations).Id);
    }

    [Fact]
    public void PlainUser_localFolderStorage_isImportedAsLocalKind()
    {
        using var sandbox = new ConfigSandbox();
        using var src = new TempDir();
        using var storage = new TempDir();

        Directory.CreateDirectory(Path.Combine(src.Path, ".duplicacy"));
        File.WriteAllText(Path.Combine(src.Path, ".duplicacy", "preferences"),
            $$"""
            [
              {
                "name": "default",
                "id": "docs",
                "repository": "{{src.Path.Replace("\\", "\\\\")}}",
                "storage": "{{storage.Path.Replace("\\", "\\\\")}}",
                "encrypted": false
              }
            ]
            """);

        var store = CreateLoadedStore();
        new ConfigMigrator(store).MigrateFrom(src.Path);

        var dest = Assert.Single(store.Current.Destinations);
        Assert.Contains(dest.Kind, new[] { DestinationKind.LocalFolder, DestinationKind.ExternalDrive });
        // Beside Duplicacy's storage, never inside it (U054).
        Assert.Equal(storage.Path + " (Backspace)", dest.PathOrSubpath);
        Assert.False(dest.Encrypted);
    }

    [Fact]
    public void DuplicacyUtilUser_parentOfManyLayout_importsAllReposAndAppliesYamlSettings()
    {
        using var sandbox = new ConfigSandbox();
        using var parent = new TempDir();

        // Two repos as siblings under parent, plus a duplicacy-util yaml for one of them.
        MakeRepo(parent.Path, "local_c", id: "local_c",   storage: @"D:\Backups\c",                   encrypted: false);
        MakeRepo(parent.Path, "dropbox", id: "dropbox", storage: "dropbox://Apps/dupli/main",       encrypted: true);

        // duplicacy-util style yaml: threads + keep + prune for the dropbox repo.
        File.WriteAllText(Path.Combine(parent.Path, "dropbox.yaml"),
            """
            repository: .
            storage:
                -   name: default
                    threads: 4
                    vss: true
            prune:
                -   storage: default
                    keep: "0:180 30:60 7:14"
                    threads: 6
            check:
                -   storage: default
            """);

        var store = CreateLoadedStore();
        var report = new ConfigMigrator(store).MigrateFrom(parent.Path);

        Assert.False(report.Skipped, report.Reason);
        Assert.Equal(2, report.ImportedBackups.Count);

        var cfg = store.Current;
        Assert.Equal(2, cfg.Backups.Count);
        var dropbox = cfg.Backups.Single(b => b.Name == "dropbox");
        Assert.Equal(4, dropbox.Threads);
        Assert.True(dropbox.UseVss);
        Assert.Equal("0:180 30:60 7:14", dropbox.KeepPolicy);
        // Imported backups land on the model default: PruneSchedule = null
        // means the engine runs prune inline after each backup. The old
        // distinction between "prune configured" and "prune disabled" in
        // Duplicacy collapses to the single "run inline" state — the new
        // UI doesn't expose "never prune" anyway.
        Assert.Null(dropbox.PruneSchedule);
        Assert.Null(dropbox.CheckSchedule);

        // Same for the local-only repo that had no yaml at all.
        var local = cfg.Backups.Single(b => b.Name == "local_c");
        Assert.Null(local.PruneSchedule);
        Assert.Null(local.CheckSchedule);
    }

    [Fact]
    public void RunningTwice_isIdempotent_noDuplicates()
    {
        using var sandbox = new ConfigSandbox();
        using var src = new TempDir();
        Directory.CreateDirectory(Path.Combine(src.Path, ".duplicacy"));
        File.WriteAllText(Path.Combine(src.Path, ".duplicacy", "preferences"),
            """[ { "name":"default","id":"x","repository":".","storage":"dropbox://a/b","encrypted":false } ]""");

        var store = CreateLoadedStore();
        new ConfigMigrator(store).MigrateFrom(src.Path);
        new ConfigMigrator(store).MigrateFrom(src.Path);

        Assert.Single(store.Current.Backups);
        Assert.Single(store.Current.Destinations);
    }

    [Fact]
    public void EmptyFolder_reportsSkippedWithFriendlyReason()
    {
        using var sandbox = new ConfigSandbox();
        using var empty = new TempDir();

        var report = new ConfigMigrator(CreateLoadedStore()).MigrateFrom(empty.Path);

        Assert.True(report.Skipped);
        Assert.Contains(".duplicacy", report.Reason!);
    }

    [Fact]
    public void NonexistentPath_reportsSkipped()
    {
        using var sandbox = new ConfigSandbox();
        var missing = Path.Combine(Path.GetTempPath(), "definitely-not-a-real-path-" + Guid.NewGuid().ToString("N"));

        var report = new ConfigMigrator(CreateLoadedStore()).MigrateFrom(missing);

        Assert.True(report.Skipped);
        Assert.Equal("Directory does not exist", report.Reason);
    }

    [Fact]
    public void S3Url_fullyQualified_isParsedIntoRegionEndpointBucketSubpath()
    {
        using var sandbox = new ConfigSandbox();
        using var src = new TempDir();
        var parent = src.Path;
        // Duplicacy-style: s3://region@endpoint/bucket/subdir
        MakeRepo(parent, "s3-repo", "s3-id", "s3://us-east-1@s3.amazonaws.com/my-bucket/path/to/data", encrypted: true);

        var cs = CreateLoadedStore();
        var report = new ConfigMigrator(cs).MigrateFrom(parent);

        var dest = cs.Current.Destinations.Single(d => d.Kind == DestinationKind.S3Compatible);
        Assert.Equal("us-east-1", dest.S3Region);
        Assert.Equal("s3.amazonaws.com", dest.S3Endpoint);
        Assert.Equal("my-bucket", dest.S3Bucket);
        Assert.Equal("path/to/data (Backspace)", dest.PathOrSubpath);
        Assert.True(dest.Encrypted);
    }

    [Fact]
    public void S3Url_regionOmitted_endpointAndBucketSplit()
    {
        using var sandbox = new ConfigSandbox();
        using var src = new TempDir();
        var parent = src.Path;
        // No region@ prefix — Duplicacy supported this for self-hosted endpoints.
        MakeRepo(parent, "s3-noregion", "s3-id", "s3://s3.eu-west-1.amazonaws.com/bucket-only/sub", encrypted: false);

        var cs = CreateLoadedStore();
        new ConfigMigrator(cs).MigrateFrom(parent);

        var dest = cs.Current.Destinations.Single(d => d.Kind == DestinationKind.S3Compatible);
        Assert.True(string.IsNullOrEmpty(dest.S3Region));
        Assert.Equal("s3.eu-west-1.amazonaws.com", dest.S3Endpoint);
        Assert.Equal("bucket-only", dest.S3Bucket);
        Assert.Equal("sub (Backspace)", dest.PathOrSubpath);
    }

    [Fact]
    public void S3Url_bucketOnly_landsInBucket()
    {
        using var sandbox = new ConfigSandbox();
        using var src = new TempDir();
        var parent = src.Path;
        MakeRepo(parent, "s3-bare", "s3-id", "s3://just-the-bucket", encrypted: false);

        var cs = CreateLoadedStore();
        new ConfigMigrator(cs).MigrateFrom(parent);

        var dest = cs.Current.Destinations.Single(d => d.Kind == DestinationKind.S3Compatible);
        Assert.Equal("just-the-bucket", dest.S3Bucket);
        Assert.Equal("backspace", dest.PathOrSubpath);
    }

    // Mirrors ServiceLocator.InitializeCore: writes are allowed only after
    // a successful load, so a missing/unreadable settings file cannot be
    // replaced accidentally by a fresh in-memory default.
    private static ConfigStore CreateLoadedStore()
    {
        var store = new ConfigStore();
        store.Load();
        Assert.True(store.IsLoaded);
        return store;
    }
    /// <summary>U054: duplicacy-util setups made with -pref-dir keep a one-line
    /// .duplicacy file naming the folder that holds preferences and filters
    /// (the maintainer's C:, D: and Dropbox backups). They were not found, and
    /// a restore profile sharing a snapshot id replaced the real backup.</summary>
    [Fact]
    public void PrefDirLayout_isFound_andRestoreProfilesDoNotReplaceTheBackup()
    {
        using var sandbox = new ConfigSandbox();
        using var parent = new TempDir();
        void PrefDirRepo(string folder, string id, string repository, string? filters)
        {
            var dir = Directory.CreateDirectory(Path.Combine(parent.Path, folder)).FullName;
            File.WriteAllText(Path.Combine(dir, ".duplicacy"), dir);
            var json = System.Text.Json.JsonSerializer.Serialize(new[] { new { name = "dropbox", id, repository, storage = "dropbox://erwin_" + id, encrypted = true } });
            File.WriteAllText(Path.Combine(dir, "preferences"), json);
            if (filters is not null) File.WriteAllText(Path.Combine(dir, "filters"), filters);
        }
        PrefDirRepo("local_c", "local_c", @"C:\", "e:(?i)^Windows/\ne:(?i)/node_modules/\n");
        PrefDirRepo("local_c_restored", "local_c", @"C:\local_c", null);

        var store = CreateLoadedStore();
        var report = new ConfigMigrator(store).MigrateFrom(parent.Path);

        Assert.False(report.Skipped, report.Reason);
        Assert.Equal(new[] { "local_c" }, report.ImportedBackups);
        Assert.Contains(report.Notes, n => n.StartsWith("local_c_restored") && n.Contains("restore profile"));
        var backup = Assert.Single(store.Current.Backups);
        Assert.Equal(new[] { @"C:\" }, backup.SourcePaths);
        // ^Windows/ is exactly the recommended /Windows/; /node_modules/
        // (never at the top of the drive) stays the person's own rule.
        Assert.Contains("/Windows/", backup.FiltersText);
        Assert.Contains("e:(?i)/node_modules/", backup.FiltersText);
        Assert.DoesNotContain("e:(?i)^Windows/", backup.FiltersText);
    }

    [Theory]
    [InlineData(@"M:\Backup\Duplicacy\local_dropbox", @"M:\Backup\Backspace\local_dropbox")]
    [InlineData(@"D:\Backups\c", @"D:\Backups\c (Backspace)")]
    [InlineData("Backups/duplicacy/main", "Backups/Backspace/main")]
    public void BackspaceNeverWritesIntoDuplicacysStorage(string duplicacy, string backspace)
        => Assert.Equal(backspace, ConfigMigrator.BesideDuplicacyStorage(duplicacy));

    [Fact]
    public void ImportedFilters_keepTheirMeaning_whenRecommendedRulesAreRecognized()
    {
        var duplicacy = string.Join("\n",
            @"e:(?i)(^|/)temp/", @"i:(?i)^Data/temp/keep/", @"e:(?i)^Data/", @"e:(?i)\.(tmp|temp)$", @"e:(?i)^pagefile\.sys$");
        var imported = ConfigMigrator.ImportFilters(duplicacy).Replace("\r\n", "\n");
        Assert.Contains(RecommendedFilters.AutoBlockHeader, imported);
        foreach (var p in new[] { "temp/", "*.tmp", "*.temp", "/pagefile.sys" }) Assert.Contains("\n" + p + "\n", imported);
        var rules = FilterTranslator.CompileRules(imported);
        bool Excluded(string path) => FilterTranslator.Evaluate(rules, path).Excluded;
        Assert.True(Excluded("a/temp/x"));          // recognized, still excluded
        Assert.True(Excluded("Data/temp/keep/x"));  // Duplicacy: the earlier temp/ rule wins
        Assert.True(Excluded("Data/other"));
        Assert.True(Excluded("b/c.TMP"));
        Assert.False(Excluded("Documents/pagefile.sys"));
    }
    // ---- helpers ----

    private static void MakeRepo(string parent, string folderName, string id, string storage, bool encrypted)
    {
        var repoDir = Path.Combine(parent, folderName);
        var duplDir = Path.Combine(repoDir, ".duplicacy");
        Directory.CreateDirectory(duplDir);
        var json = $$"""
            [
              {
                "name": "default",
                "id": "{{id}}",
                "repository": ".",
                "storage": "{{storage.Replace("\\", "\\\\")}}",
                "encrypted": {{(encrypted ? "true" : "false")}}
              }
            ]
            """;
        File.WriteAllText(Path.Combine(duplDir, "preferences"), json);
    }

    /// <summary>
    /// Redirects AppPaths.ConfigRoot to a throwaway temp dir for the duration
    /// of the test so ConfigStore.Save_NoLock doesn't write to the real
    /// %USERPROFILE% config.
    /// </summary>
    private sealed class ConfigSandbox : IDisposable
    {
        private readonly string? _previous;
        public string Root { get; }
        public ConfigSandbox()
        {
            _previous = Environment.GetEnvironmentVariable("BACKSPACE_CONFIG_ROOT");
            Root = Path.Combine(Path.GetTempPath(), "fb-cfg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Environment.SetEnvironmentVariable("BACKSPACE_CONFIG_ROOT", Root);
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("BACKSPACE_CONFIG_ROOT", _previous);
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fb-migrator-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    /// <summary>A Duplicacy storage on a drive letter that is not connected
    /// while importing (the maintainer's "My Passport" on M:) becomes an
    /// external drive, so its backup is skipped while it is unplugged and
    /// runs from the tray app rather than the background service.</summary>
    [Fact]
    public void Storage_on_an_unconnected_drive_letter_is_imported_as_an_external_drive()
    {
        using var sandbox = new ConfigSandbox();
        using var parent = new TempDir();
        var letter = "QRSTUVWXYZ".Select(c => c + ":").First(l => !new DriveInfo(l).IsReady);
        MakeRepo(parent.Path, "local_dropbox", id: "local_dropbox", storage: letter + @"\Backup\Duplicacy\local_dropbox", encrypted: true);
        var store = CreateLoadedStore();
        new ConfigMigrator(store).MigrateFrom(parent.Path);
        var dest = Assert.Single(store.Current.Destinations);
        Assert.Equal(DestinationKind.ExternalDrive, dest.Kind);
        Assert.Equal(letter, dest.ExpectedDriveLetter);
        Assert.Equal(letter + @"\Backup\Backspace\local_dropbox", dest.PathOrSubpath);
    }
}
