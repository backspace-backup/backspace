# Real-engine E2E coverage tree

This file is the contract between the scenario suite and the surface
area users actually touch. It enumerates the paths a real user can
walk through Backspace, marks which ones a scenario already exercises,
and names the gaps still open. Add a scenario → tick its node. Find a
bug a real user hit → first check that this tree predicts it; if it
doesn't, the tree is incomplete and needs the new branch before you
patch the bug.

All scenarios listed below run against the **real Backrest sidecar**
(via `BackupE2EBase` / `RealEngineFixture`). The unit-style tests
under `tests/Backspace.Tests/E2E/` use the `InMemoryBackupEngine`
fake — useful for orchestrator logic but blind to the actual
Backrest 1.13 contract (where the user's "plan not found" bug
came from). When in doubt, the real-engine path is the source of
truth.

---

## Tree

Leaf legend: `✓ Exx` = covered by scenario Exx; `□` = gap; `~` =
partial (caveat noted); `↘` = covered elsewhere (snapshot-only S-suite
or unit). Killed branches are listed under "Deliberately out of scope"
at the bottom with their reason — write a comment, not silence.

### 1. Source side

```
1.1 single source folder                       ✓ E01 E04 E05 E06 E11 E17 E18
1.2 multiple sources, one backup
    1.2.1 two sources, both real folders       ✓ E12 E13 E14 E15
    1.2.2 three+ sources                       ↘ MultiSourceBackupRestoreTests (InMemory)
    1.2.3 same source path in two backups      □ E29 (planned: shared-source dedup test)
1.3 source filters
    1.3.1 default recommended exclusions       ✓ E06 (Advanced editor seeds them)
    1.3.2 custom regex add                     ✓ E30
    1.3.3 filter that excludes everything      ✓ E31 (engine stays clean on empty-snapshot)
1.4 source disappears mid-run                  □ deferred — requires injection harness
1.5 source with NTFS features
    1.5.1 hardlinks                            ✓ E11 (smoke)
    1.5.2 sparse files                         ~ E11 (asserted as known-gap dimension)
    1.5.3 ADS                                  ~ E11 (asserted as known-gap dimension)
    1.5.4 junctions                            ✓ E11 (smoke)
    1.5.5 paths > 260 chars                    ✓ E11
    1.5.6 ACLs / DACL preservation             ~ E11 (asserted as known-gap dimension)
    1.5.7 non-ASCII / Unicode names            □ deferred — needs dedicated fixture
1.6 source on detachable drive (External)      □ deferred — needs mock plug/unplug
1.7 source = network share (UNC)               □ deferred — needs SMB server
```

### 2. Destination side

```
2.1 LocalFolder
    2.1.1 encrypted, password set              ✓ E01 E02 E04 E05 E06 E11 E14 E15 E16 E17 E18 E20
    2.1.2 unencrypted (no password)            ✓ E12 E13 E19 E21
2.2 ExternalDrive
    2.2.1 same shape as LocalFolder            ↘ via E13 (same code path under the hood)
    2.2.2 expected drive letter / volume mismatch    □ deferred — needs mock drive
2.3 NetworkShare (UNC)                         □ deferred — SMB infra
2.4 Dropbox (rclone)
    2.4.1 App-scoped                           ✓ E07
    2.4.2 Full-access                          ~ E07 (one variant — scope set at OAuth time)
2.5 OneDrive (rclone)
    2.5.1 Personal                             ✓ E08
    2.5.2 Business                             □ deferred — needs Business test account
2.6 GoogleDrive (rclone)                       ✓ E09
2.7 S3-compatible                              □ deferred — needs MinIO container
2.8 Multi-destination per backup
    2.8.1 1 src → 2 LocalFolder dests          ✓ E13
    2.8.2 1 src → Local + Cloud                □ E32 (planned, opt-in on cloud token)
    2.8.3 N srcs × M dests grid                ↘ E10 covers 2×2 multi-cloud
2.9 Erase storage (Remove destination)
    2.9.1 Local: wipes restic dir, keeps foreign files   ✓ E20
    2.9.2 Local: refuse when sibling dest shares URL     ↘ DestinationsViewModel unit
    2.9.3 Cloud: only prunes known snapshot ids          ↘ StorageCleaner unit
2.10 Change storage password
    2.10.1 round-trip: rotate, backup again, restore     ✓ E19
    2.10.2 wrong-password retry                          □ deferred — needs negative-path
2.11 Edit destination (path, name, OAuth re-auth)
    2.11.1 path rename mid-life                          ✓ E33
    2.11.2 cloud re-auth                                 □ deferred — OAuth flow needs mock
```

### 3. Workflow / run lifecycle

```
3.1 Run now (manual)
    3.1.1 single cell run                      ✓ E01 (and many)
    3.1.2 multi-cell run                       ✓ E12 E13
    3.1.3 concurrent run of two backups        ✓ E03 E10
    3.1.4 queue / busy state on same backup    ✓ E21
3.2 Scheduled run (OS scheduler fires)         □ deferred — OS-level
3.3 CLI --run / --prune / --check              □ deferred — process-spawn harness
3.4 Backup options
    3.4.1 VSS on (admin)                       □ deferred — admin required
    3.4.2 VSS off (default in CI)              ✓ E01..E12 all
    3.4.3 bandwidth limit                      ↘ Repo flag round-trip unit covered
    3.4.4 abort-on-metered                     ↘ network detection unit
    3.4.5 skip-on-battery                      ↘ schedule unit
    3.4.6 threads = N                          ✓ E06 (T=3)
3.5 Stop / cancel
    3.5.1 pre-cancelled token                  ↘ BackupLifecycleTests (InMemory)
    3.5.2 stop via UI button mid-run           ✓ E02 (stop-and-resume next run)
    3.5.3 sidecar killed mid-run               □ deferred — process injection
3.6 Crash recovery
    3.6.1 lock left by killed restic           ↘ AutoUnlock=true on repo stanza
    3.6.2 partial run, re-run completes        ↘ E02
3.7 Notifications fire per matrix              □ E34 (planned)
3.8 Healthcheck ping on success / failure      □ deferred — needs mock endpoint
```

### 4. Maintenance lifecycle

```
4.1 Prune after backup
    4.1.1 enabled inline, single run, keep-policy honored   ✓ E14
    4.1.2 enabled inline, multi-revision                    ↘ BackupLifecycleTests (InMemory)
4.2 Independent prune schedule (PruneSchedule non-null)     □ deferred — scheduled-task path
4.3 Check after backup
    4.3.1 enabled inline, structure-only                    ✓ E15
    4.3.2 enabled inline, read-data subset                  □ deferred — check-policy variant
4.4 Independent check schedule                              □ deferred — scheduled-task path
4.5 Test restore (verify drill, sample byte-compare)
    4.5.1 happy path, all samples match                     ✓ E16
    4.5.2 source mutated between runs → mismatch surfaced   □ deferred
4.6 Snapshot browser
    4.6.1 mount as drive letter (Windows)                   ✓ E04
    4.6.2 open snapshot URL in browser                      □ deferred — launches external
```

### 5. Restore

```
5.1 Restore all files of latest revision                    ✓ E01 E12 E13 (and many)
5.2 Restore cherry-pick subset                              ✓ E17
5.3 Restore from a specific older revision                  ↘ via revision-id parameter; E22 implicit
5.4 Restore target
    5.4.1 to fresh empty dir, preserve structure on         ✓ E01 (and many)
    5.4.2 to fresh empty dir, preserve structure off        □ deferred — option-matrix
    5.4.3 to original source location (overwrite)           ✓ E18
5.5 Restore failure modes
    5.5.1 per-file retry cascade                            ↘ unit (RetryDelaysSeconds)
    5.5.2 all retries exhausted → file marked failed        □ deferred
5.6 Restore from multi-cell backup
    5.6.1 pick which destination to restore from            ↘ implicit (E13 restores each)
    5.6.2 pick which source to restore                      ✓ E12 (each source independent)
```

### 6. Wizards / editors / dialogs

```
6.1 Onboarding from empty config → Save & Run
    6.1.1 easy-mode happy path                              ✓ E05 ✓ E23 (truly empty)
    6.1.2 cancel mid-wizard                                 ↘ S01 snapshot only
6.2 Backup editor
    6.2.1 advanced new                                      ✓ E06
    6.2.2 advanced edit existing                            ↘ S07 snapshot only
    6.2.3 edit while backup is running (blocked)            ↘ DestinationsViewModel unit
6.3 Destination editor
    6.3.1 new (per kind)                                    ↘ S08 snapshot only (Local, Dropbox)
    6.3.2 edit while backup is running (blocked)            ↘ DestinationsViewModel unit
6.4 Destructive confirm
    6.4.1 universal OK gate (delete backup / dest / erase)  ↘ S10 snapshot; E20 click-through
6.5 Backup card action shelf
    6.5.1 every visible button reaches its command          ✓ E24
6.6 Settings
    6.6.1 notification matrix persistence                   ↘ SettingsViewModel unit
    6.6.2 healthcheck / mail provider                       □ deferred — needs mock
    6.6.3 theme + accent live swap                          ↘ S05 / S12 snapshot
```

### 7. Engine contract

```
7.1 Brand-new repo registration
    7.1.1 /AddRepo flow assigns canonical 64-char Guid      ✓ E12 (regression-pinned)
    7.1.2 legacy 36-char UUID migrates via re-add           ✓ E22
    7.1.3 unencrypted + --insecure-no-password              ✓ E12 (implicit)
7.2 SetConfig drift (per-backup Flags rewrite)              ✓ E14 / E15 (prune/check change repo state)
7.3 Modno mismatch retry                                    ↘ ResticBackrestEngine unit
7.4 Repo / plan disappearance detection                     ↘ TranslateRpcError unit
```

---

## Deliberately out of scope (and why)

These would be valuable but are kept out of the real-engine suite for
a specific reason. Tracked here so a future contributor knows they
aren't "missed" — they were considered and parked.

- **VSS-on, admin path.** Requires elevated test runner; CI runs as a
  standard user. Covered by manual smoke before each release.
- **S3 / MinIO container.** Adds a Docker dependency to the test
  matrix that we haven't paid for yet. The S3 destination code path
  is exercised end-to-end via rclone in `tests/Backspace.Tests/Cloud/`
  when credentials are present, so this is "tested" — just not in
  the automated scenario suite.
- **NetworkShare / SMB.** Same as S3 — needs a server.
- **Mail send (Resend / Mailgun).** Real send means real provider
  costs + flaky network. The `MailService` unit tests cover the
  HTTP-API + provider-error mapping in detail; surfacing it through
  a scenario would only add value if we mocked the provider, which
  we've punted.
- **Healthchecks.io ping.** Same: covered by `HealthcheckService`
  unit tests against a mocked transport.
- **OS scheduled-task firing.** The scheduler stanza is unit-tested
  end-to-end against a clean `Backspace.exe --run <id>` invocation
  (covered by `Program.cs` headless smoke); the OS firing the entry
  is a Windows / launchd / systemd thing we trust on the basis of
  the WindowsTaskScheduler RPC return code.
- **Sidecar process crash mid-run.** The recovery shape is
  AutoUnlock=true on the repo + restart-the-sidecar on next launch.
  Forcing a crash deterministically in a unit/scenario harness
  requires intrusive injection that isn't worth its weight.
- **Concurrent edit of a file during snapshot.** restic's own atomic
  semantics handle this; surfacing the race deterministically requires
  multi-process orchestration we'd rather rely on restic's test
  suite for.
- **Forgotten-password recovery.** There isn't one — restic is honest
  about this and so are we. The negative path is covered by the
  "password incorrect" error mapping in `ResticBackrestEngine`'s
  unit tests, not by an E2E.

---

## Adding a new scenario — checklist

1. Pick or add the leaf in the tree above. If the leaf already has
   `✓` next to it, you're duplicating coverage — talk to whoever
   owns that area first.
2. The scenario must drive the **same command path the user clicks**
   (RunNowCommand, TestRestoreCommand, DeleteCommand, …) — going
   straight to the orchestrator bypasses the UX gate that's often
   where the bug actually lives.
3. Use `RealEngineFixture` via `BackupE2EBase`. The in-memory engine
   is for unit tests, not for scenarios — most production bugs
   live in the Backrest 1.13 contract surface, which the fake can't
   model.
4. Assert one specific user-visible outcome — "Status ∈ {Success, Warning}"
   plus a byte-equality / count / state check. Don't pile on
   assertions that another scenario already owns.
5. Update this tree before committing. A new scenario without a tick
   in the tree is a coverage rumour.
