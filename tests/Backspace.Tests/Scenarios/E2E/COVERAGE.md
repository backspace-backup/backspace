# Real-engine E2E coverage tree

This is an inventory of test scope, not a work queue or a record of
acceptance. The [local Review desk](http://127.0.0.1:4317/) holds issues,
decisions, current results and remaining user checks. A test listed here
may require credentials or an installed driver and skip without them;
read its runner result and scenario report before claiming it passed.

The `Exx` scenarios below run against the **real Backrest sidecar**
(via `BackupE2EBase` / `RealEngineFixture`). Other tests referenced
under `tests/Backspace.Tests/E2E/` include both real-engine and
`InMemoryBackupEngine` tests. Check each fixture. An in-memory test
can establish orchestrator logic but cannot establish the Backrest
protocol, real storage behavior or native Windows integration.

---

## Tree

Leaf legend: `✓ Exx` = covered by scenario Exx; `□` = gap; `~` =
partial (caveat noted); `↘` = covered elsewhere (S-suite or unit).
`□` identifies a boundary of this inventory, not a decision to defer
work. Planning and acceptance belong in the Review desk.

### 1. Source side

```
1.1 single source folder                       ✓ E01 E04 E05 E06 E11 E17 E18
1.2 multiple sources, one backup
    1.2.1 two sources, both real folders       ✓ E12 E13 E14 E15
    1.2.2 three+ sources                       ↘ MultiSourceBackupRestoreTests (InMemory)
    1.2.3 same source path in two backups      □ no dedicated scenario
1.3 source filters
    1.3.1 default recommended exclusions       ✓ E06 (Advanced editor seeds them)
    1.3.2 custom regex add                     ✓ E30
    1.3.3 filter that excludes everything      ✓ E31 (engine stays clean on empty-snapshot)
1.4 source disappears mid-run                  □ no dedicated real-engine scenario
1.5 source with NTFS features
    1.5.1 hardlinks                            ✓ E11 (smoke)
    1.5.2 sparse files                         ~ E11 (asserted as known-gap dimension)
    1.5.3 ADS                                  ~ E11 (asserted as known-gap dimension)
    1.5.4 junctions                            ✓ E11 (smoke)
    1.5.5 paths > 260 chars                    ✓ E11
    1.5.6 ACLs / DACL preservation             ~ E11 (asserted as known-gap dimension)
    1.5.7 non-ASCII / Unicode names            □ no dedicated fixture
1.6 source on detachable drive (External)      □ physical plug/unplug not exercised
1.7 source = network share (UNC)               □ needs an SMB server
```

### 2. Destination side

```
2.1 LocalFolder
    2.1.1 encrypted, password set              ✓ E01 E02 E04 E05 E06 E11 E14 E15 E16 E17 E18 E20
    2.1.2 unencrypted (no password)            ✓ E12 E13 E19 E21
2.2 ExternalDrive
    2.2.1 same shape as LocalFolder            ↘ via E13 (same code path under the hood)
    2.2.2 expected drive letter / volume mismatch    □ physical drive transition not exercised
2.3 NetworkShare (UNC)                         □ needs an SMB server
2.4 Dropbox (rclone)
    2.4.1 App-scoped                           ✓ E07
    2.4.2 Full-access                          ~ E07 (one variant — scope set at OAuth time)
2.5 OneDrive (rclone)
    2.5.1 Personal                             ✓ E08
    2.5.2 Business                             □ needs a Business test account
2.6 GoogleDrive (rclone)                       ✓ E09
2.7 S3-compatible                              □ no dedicated real-account scenario
2.8 Multi-destination per backup
    2.8.1 1 src → 2 LocalFolder dests          ✓ E13
    2.8.2 1 src → Local + Cloud                □ no dedicated scenario
    2.8.3 N srcs × M dests grid                ↘ E10 covers 2×2 multi-cloud
2.9 Erase storage (Remove destination)
    2.9.1 remove owned snapshots, keep foreign files     ✓ E20 E44
    2.9.2 preserve unmanaged snapshots and repo config   ✓ E44
    2.9.3 all providers use explicit owned snapshot IDs  ↘ StorageCleanerTests (in-memory)
2.10 Change storage password
    2.10.1 round-trip: rotate, backup again, restore     ✓ E19
    2.10.2 wrong-password retry                          □ no dedicated real-engine scenario
2.11 Edit destination (path, name, OAuth re-auth)
    2.11.1 path rename mid-life                          ✓ E33
    2.11.2 cloud re-auth                                 ↘ deterministic OAuth tests; live account required separately
```

### 3. Workflow / run lifecycle

```
3.1 Run now (manual)
    3.1.1 single cell run                      ✓ E01 (and many)
    3.1.2 multi-cell run                       ✓ E12 E13
    3.1.3 concurrent run of two backups        ✓ E03 E10
    3.1.4 queue / busy state on same backup    ✓ E21
3.2 Scheduled run (OS scheduler fires)         □ real reboot/sleep acceptance required
3.3 CLI --run / --prune / --check              ↘ CliParserCoverageTests; generated-action process probes
3.4 Backup options
    3.4.1 VSS on (admin)                       □ native elevation required
    3.4.2 VSS off (default in CI)              ✓ E01..E12 all
    3.4.3 bandwidth limit                      ↘ Repo flag round-trip unit covered
    3.4.4 abort-on-metered                     ↘ network detection unit
    3.4.5 skip-on-battery                      ↘ schedule unit
    3.4.6 threads = N                          ✓ E06 (T=3)
3.5 Stop / cancel
    3.5.1 pre-cancelled token                  ↘ BackupLifecycleTests (InMemory)
    3.5.2 stop via UI button mid-run           ✓ E02 (stop-and-resume next run)
    3.5.3 sidecar killed mid-run               □ no dedicated real-engine scenario
3.6 Crash recovery
    3.6.1 lock left by killed restic           ↘ AutoUnlock=true on repo stanza
    3.6.2 partial run, re-run completes        ↘ E02
3.7 Notifications fire per matrix              ↘ S48 S50 / CloudNotificationRepairTests; native display separate
3.8 Healthcheck ping on success / failure      ↘ CloudNotificationRepairTests / BackupCoverageOutcomeTests (fake HTTP)
3.9 Partial coverage cannot report success     ↘ BackupCoverageOutcomeTests / MaintenanceOutcomeTests
3.10 Refresh saved config after queue wait      ↘ OrchestratorAdmissionRefreshTests
```

### 4. Maintenance lifecycle

```
4.1 Prune after backup
    4.1.1 enabled inline, single run, keep-policy honored   ✓ E14
    4.1.2 enabled inline, multi-revision                    ↘ BackupLifecycleTests (InMemory)
4.2 Independent prune schedule (PruneSchedule non-null)     □ real scheduled-task firing not exercised
4.3 Check after backup
    4.3.1 enabled inline, structure-only                    ✓ E15
    4.3.2 enabled inline, read-data subset                  □ no dedicated real-engine scenario
4.4 Independent check schedule                              □ real scheduled-task firing not exercised
4.5 Test restore (all files, bytes compared when unchanged)
    4.5.1 actual button restores both sources + empty file  ✓ E16
    4.5.2 same-size source change with original timestamp   ✓ E16
4.6 Snapshot browser
    4.6.1 mount as drive letter (Windows)                   ✓ E04 (requires WinFSP)
    4.6.2 immutable cache, lazy/bulk reads and empty dirs    ✓ E45 (provider; no Explorer/WinFSP)
    4.6.3 refuse partial or linked cached output            ↘ EngineBackedSnapshotTreeProviderTests
4.7 Maintenance cancellation between sources and retry     ✓ E47
```

### 5. Restore

```
5.1 Restore all files of latest revision                    ✓ E01 E12 E13 (and many)
5.2 Restore cherry-pick subset                              ✓ E17
5.3 Restore from a specific older revision                  ✓ E22 E44 (immutable ID after retention)
5.4 Restore target
    5.4.1 to fresh empty dir, preserve structure on         ✓ E01 (and many)
    5.4.2 preserve structure off, through wizard controls   ✓ E46
    5.4.3 to original source location (overwrite)           ✓ E18
    5.4.4 replacement off protects existing files          ✓ E44 E46
    5.4.5 flattening collision refused before publication  ✓ E44
5.5 Restore failure modes
    5.5.1 target or ancestor is an NTFS junction            ↘ RestoreSafetyRegressionTests
    5.5.2 staged failure / cancellation leaves target safe  ↘ RestoreSafetyRegressionTests
5.6 Restore from multi-cell backup
    5.6.1 pick which destination to restore from            ↘ implicit (E13 restores each)
    5.6.2 pick which source to restore                      ✓ E12 (each source independent)
```

### 6. Wizards / editors / dialogs

```
6.1 Onboarding from empty config → Save & Run
    6.1.1 easy-mode happy path                              ✓ E05
    6.1.2 cancel mid-wizard                                 ↘ S01 snapshot only
6.2 Backup editor
    6.2.1 advanced new                                      ✓ E06
    6.2.2 edit existing sources affects the next backup     ✓ E23
    6.2.3 edit while backup is running                      □ no dedicated real-engine UI scenario
6.3 Destination editor
    6.3.1 new (per kind)                                    ↘ S08 snapshot only (Local, Dropbox)
    6.3.2 edit while backup is running                      □ no dedicated real-engine UI scenario
6.4 Destructive confirm
    6.4.1 universal OK gate (delete backup / dest / erase)  ↘ S10 snapshot; E20 click-through
6.5 Backup card action shelf
    6.5.1 every visible button reaches its command          ✓ E24
6.6 Settings
    6.6.1 notification matrix persistence                   ↘ S45 (headless controls)
    6.6.2 provider/recipient edits and cleared credentials  ↘ S63 (saved JSON; no email sent)
    6.6.3 theme + accent live swap                          ↘ S05 / S12 snapshot
```

### 7. Engine contract

```
7.1 Brand-new repo registration
    7.1.1 /AddRepo flow assigns canonical 64-char Guid      ✓ E12 (regression-pinned)
    7.1.2 reject invalid GUID format                       ↘ BackrestRpcModelsTests (not a live migration)
    7.1.3 unencrypted + --insecure-no-password              ✓ E12 (implicit)
7.2 SetConfig drift (per-backup Flags rewrite)              ✓ E14 / E15 (prune/check change repo state)
7.3 Modno mismatch retry                                    □ no dedicated real-engine scenario
7.4 Repo / plan disappearance detection                     □ no dedicated real-engine scenario
```

---

## Evidence boundaries

These boundaries describe what automated tests do not prove. They are
not decisions to park work; current checks and decisions live in the desk.

- **VSS-on, admin path.** Requires elevated test runner; CI runs as a
  standard user. Covered by manual smoke before each release.
- **S3 / MinIO container.** No dedicated live S3 scenario is listed
  here. Dropbox or in-memory tests do not establish S3 compatibility.
- **NetworkShare / SMB.** Same as S3 — needs a server.
- **Mail send (Resend / Mailgun).** Fake HTTP tests cover provider
  responses and retry/error handling. S63 covers saving the settings.
  Neither proves delivery to an inbox with a real account.
- **Healthchecks.io ping.** Same: covered by `HealthcheckService`
  unit tests against a mocked transport.
- **OS scheduled-task firing.** Definition and generated-action
  process tests check arguments, execution and exit handling. They
  do not prove task registration, S4U permissions or sleep/reboot firing.
- **Sidecar process crash mid-run.** The recovery shape is
  AutoUnlock=true on the repo + restart-the-sidecar on next launch.
  That configuration alone does not prove recovery after a real crash.
- **Concurrent edit of a file during snapshot.** Source-change tests
  for the verification drill do not establish atomic capture of a file
  being written while backup runs. Do not infer that guarantee from
  an unchanged-source round trip.
- **Forgotten-password recovery.** There is no password recovery.
  E33 checks rejection of a wrong password when locating a moved
  encrypted repository; it cannot recover a lost password.

---

## Adding a new scenario — checklist

1. Pick or add the leaf in the tree above. Existing coverage may still
   need a negative case, different user interaction or real-engine test.
2. The scenario must drive the **same command path the user clicks**
   (RunNowCommand, TestRestoreCommand, DeleteCommand, …) — going
   straight to the orchestrator bypasses the UX gate that's often
   where the bug actually lives.
3. Use `RealEngineFixture` via `BackupE2EBase`. The in-memory engine
   is suitable for deterministic S-suite scenarios, but it cannot prove
   the Backrest protocol or real backup contents.
4. Assert a precise user-visible outcome plus byte-equality, count or
   state checks. Add the relevant negative case; a warning must not be
   accepted as success without checking what it means for this fixture.
5. Update this tree before committing. A new scenario without a tick
   in the tree is a coverage rumour.
