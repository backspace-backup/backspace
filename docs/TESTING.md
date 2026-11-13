# TESTING.md — test suite & scenario harness

How to run the tests, what each tier covers, and the standing
instructions for any agent driving the visual scenario suite.

Backup health checks must preserve a known cancelled or failed result while
configuration and history reload. The view-model regression asserts the state
immediately after refresh, before a background read could hide a false all-clear.
E49 also cancels a real Test restore through its button, returns to Backups and
captures the warning after refresh. A fresh card shows a neutral loading state
until saved history has been read. A later successful backup alone must not clear
an incomplete recovery check; a successful recovery check may clear it.

`CompletedEngineFailureTests` removes only a disposable repository's config
after a real successful backup. The next actual backup must fail after one
attempt, preserve every remaining repository file, and never initialize a
replacement. Controlled results also distinguish an offline NAS and an
unrelated filename from this explicit missing-repository error.

S66 drives the multi-source Restore wizard through its actual buttons with
controlled snapshot-list responses. Next stays disabled during loading and
after a failed lookup; a failed read is not labelled as an empty
backup. Back and retry can recover, and a late failure from an abandoned
request cannot overwrite the new result. Row-level tests also reject empty
results, missing, duplicate and unlisted selections. These controlled failures do not prove
cloud availability; E12 separately backs up and recovers two real local sources.

Website screenshots use separate log folders and reset preferences and logging
settings. This prevents earlier tests' transcripts or settings from changing the
demo. Portable-update relocation tests allow at most two seconds for Windows to
release freshly written fixture files before the external folder move; recovery
validation itself is not retried or relaxed.

Whole-drive recovery has two complementary checks: `SnapshotReadEfficiencyTests`
keeps root and nested files while rejecting similarly named or other-drive
paths, and E43 backs up and recovers a real mounted volume with the bundled
engines. E43's remote provider is synthetic; its successful recovery does not
qualify the parked cloud-source feature as implemented or cloud-tested.

Exclusion scope is checked against the real engine in E06 and E30. E06 saves
the Advanced editor's recommended filters and restores its source from beneath
a `temp` ancestor. E30 checks a source beneath an excluded ancestor, literal
brackets in the source name, and a root-only exclusion with a nested namesake
that must survive. E54 confirms that an edited exclusion reaches the next
backup and does not change older snapshots. Restic gets the Filters
text unchanged as its exclude file, and the preview reads it the same way, on
full paths (U151).

E57 compares Left out (U177) and the savings estimate with restic's own
`backup --dry-run -vv` run directly with the same exclude file, an actual
backup and byte-verified recovery, for three backups: character classes,
literal brackets, ordered `!` exceptions and a bare directory name; restic's
syntax (`?:/` for the top of the drive, `$$`, `$USERNAME`, `**`, a trailing
`/`, a plain leading `/`); and a stored Duplicacy filter converted when the
settings load. The assertions require both the included files and the
excluded files to agree across every path. `DuplicacyFilterConverterTests`
converts the owner's three approved backups byte for byte
(`Services/Fixtures/U151`, kept exact by `.gitattributes`), then in their U165
form (the always-applied block moved into the Essentials group);
`RecommendationUpdatesTests` checks that form excludes the same paths. S90
shows the converted local_c in the editor after a settings load.

`RunHistoryFileTests` exercises actual Windows replacement locks, bounded
retry exhaustion, recovery after an interrupted rename, an undeletable old
pending list, and incomplete serialization. `InfrastructureReliabilityTests`
also holds the encrypted vault against replacement, checks concurrent writers,
and verifies that a missing primary with a retained backup cannot silently
create an empty vault. These tests use disposable local files; they do not
modify Windows Credential Manager.

The same persistence group checks that a failed settings replacement leaves
the live model unchanged and keeps both the original file and pending edit.
Missing settings or engine configuration with a retained `.bak` must not
initialize defaults. The visible file-wait path must reject, rather than
replay, an uncertain partial rename.

`ServiceCommandLineRunTests` checks name matching, ambiguous-name rejection,
named runs of backups with automatic backups off (U171), strict request parsing, lost-reply handling and explicit paused results.
VM acceptance must invoke the packaged public commands as an ordinary owner and
verify the actual worker identity and saved result; protocol tests alone do not
establish that the CLI entry point or engine ran.

Windows update cleanup tests hold real temporary and persistent sharing locks:
the temporary lock permits a bounded retry, while a persistent lock must retain
the file and report failure. Unknown and unfinished update folders remain intact.
Settings saves and update recovery plans retry only recognized Windows file-access
conflicts for up to 1.55 seconds. If access remains blocked, the previous record
stays intact and a complete staged record remains available for recovery.

`ServiceTranscriptTests` checks bounded UTF-8 reads, wrong-run/backup rejection,
missing logs, outside paths and strict request parsing. `S71_BackgroundRunLogs`
uses the actual bound run picker with controlled replies to check delayed
selection responses, live service output, Clear view across repeated polls and
failed reads. Actual transport acceptance belongs in the VM: receive output
while the real worker runs, compare a requested saved transcript with its private
original, and reject the same run ID under a different backup ID. Headless
screenshots do not establish native window interaction or notification delivery.
`ServiceNotificationTests` covers durable worker delivery, concurrent queue
writes, restart recovery, delivery leases, stale receipt rejection, failed
presentation, locked-desktop deferral and preservation of corrupt state. It
also inspects the least-privilege interactive-only companion task without
registering it. `S72_BackgroundNotifications` delivers a queued alert through
the real popup and pointer-clicks Open run details; an older failed run must
remain the target even when a newer run succeeded. Desktop availability is
controlled in that headless scenario. VM acceptance must separately exercise
the actual worker, authenticated owner pipe, service restart, task launch and
native presentation after Windows sign-in and unlock.
`ServiceRunRecoveryTests` checks early process failures, retained success,
explicit Stop, wrong/stale identities and a blocked notification file. A retry
must save one fallback history row and one alert, without losing the pending
recovery record. Actual VM acceptance separately kills the admitted worker and
the SCM host while a disposable operation gate holds the backup before engine
admission, then checks ordinary-owner history and exact-run alert delivery.
These are deterministic authorization tests, not service installation or
before-sign-in acceptance tests.

The synthetic index-completion failure tests use separate configuration roots
and release their retained safety gates after the fake engine has finished.
Otherwise those deliberately blocked coordinators survive garbage collection
and can prevent later real password-rotation scenarios from acquiring exclusive
access. This fixture cleanup must not weaken the production retained-gate rule.
E33 allows at most two seconds for its external directory-move setup to settle;
a persistent holder still fails before the application's locate action is tested.

`ServiceHostRequestTests` rejects unknown execution fields, duplicate JSON
properties, invalid revisions and run IDs, unsupported operations and oversized
requests. Runtime acceptance must also use the actual SCM host in the isolated
VM: a valid request starts the real worker, concurrent and stale requests fail,
and the authenticated credential endpoint can open the existing service vault.
Those checks do not substitute for installer, upgrade or desktop acceptance.

`ScheduleNetworkConditionsTests` (U226) drives the loop with a local and a cloud destination: offline or metered for days, the local one runs on schedule and the cloud one runs alone once the network allows; an unreachable one is retried only after a network change or a day; checks follow their backup's conditions unless they have their own. `OrchestratorNetworkDestinationsTests` runs a real orchestration: on a metered connection or offline the local destination is backed up and the cloud one is skipped and named as waiting; a backup to local folders only never watches the connection.

`BackupSchedulerTests` drives the shared schedule loop (tray app and service)
with a fake clock and runner: one run at the due time and none before, idle
sleep up to the next run (at most a day with Windows' notifications, U185:
a wake-up looks again at once; a change to the settings file or the Quit
state wakes it; Windows accepts the power notifications), one catch-up after missed slots
(always, U226), state kept
across restarts so a run is not repeated, waiting for battery, network and
Quit then running once, disabled and snoozed backups passing over their slot,
a changed time taking effect without running early, no overlap with slots
passed during a long run skipped, separate check and prune cadences, time limit
and battery stops, an unreadable state file, and waking after a settings
change. It also checks the start-at-sign-in entry: added when missing, kept for
another existing copy, taken over only when turned on in this copy, replaced
when its copy is gone, and never added by a development build on its own.
`S74_StartupAndTray` screenshots Settings → Startup and tray in each state.
These do not establish a real sign-in, sleep, hibernation or reboot; the VM
acceptance does.

`ServiceScheduledRunTests` covers Run now and command-line runs through the
service, and which backups wait for a network. Controlled service replies cover
completion, failure, a lost start response, lease expiry, inconsistent results
and a different run identity. Monotonic-clock tests reject late or wrong-run renewals.
`ServiceRunJournalTests` checks actual saved receipts across successive runs
and rejects missing, oversized or mismatched results.
Actual worker cancellation still requires the isolated VM; these
deterministic checks do not establish production activation.

Run the service and credential tests in **Release as well as Debug**. The
cross-thread rejected-import regression caught a Release-only lock leak that
same-thread tests missed. Keep filtered exception handlers outside methods
that acquire protocol monitors: SDK 10.0.401 reused the monitor-cleanup flag
in a rejected filter. The isolated reproduction and compiled-code evidence
are attached to N046 in the local Review desk. CI runs the Release subset.


`ServiceWorkerTests` verifies that approved settings replace obsolete plans
without replacing newer backup/recovery outcomes, and that unsupported
network/removable
paths fail admission. An ordinary test process cannot claim the service's VSS
capability. Native acceptance must additionally run the actual Backspace
worker as the isolated VM's virtual service identity, exercise a locked source,
recover its contents, and repeat before sign-in after a VM reboot. Unit tests
alone do not establish that acceptance.

`RestorePublicationLockTests` holds actual recovered files for over ten seconds,
checks that publication is still waiting, and exercises both continuation and
the shared Stop restore command. Original files survive cancellation. S65
clicks Keep waiting and Stop restore in the real Avalonia window and captures
both expanded and collapsed notices while the recovered file remains locked.
This is headless UI coverage with real file locks, not a native desktop claim.

Test restore startup also waits for its per-backup ownership file on a worker
thread. `TestRestoreJournalTests` holds that file for more than six seconds,
checks that duplicate starts reuse the waiting session and no journal is
written before ownership, then verifies release or cancellation. E53 exercises
consecutive real scheduled checks with optional comparison on and off.

## Audit qualification and remediation (2026-10-02)

The matrix below is an inventory of tests, not proof that every complete user flow has been exercised. Scenario pages report **STEPS COMPLETED** and explicitly exclude the runner's later teardown; the test-runner result (TRX) is authoritative for pass/fail. Scenario-owned cleanup is included in its recorded outcome. Always inspect both results and visual artifacts.

Real backup/restore scenarios compare SHA-256 for every regular restored file, including large fixtures. Mutable sources, repositories and restore targets use unique directories under the test process's temporary root. For this review checkout, keep that root inside `audit-dashboard/private/temp/`, as required by AGENTS.md. Use a new local-only subfolder there for build outputs and disposable mutable fixtures so Dropbox does not hold compiler files open or alter fixture attributes. Scenario screenshots and reports normally go under the repository's artifacts directory. Set `BACKSPACE_SCENARIO_ARTIFACTS_ROOT` to an absolute path in a fresh repository-local temp folder to keep a review run separate from existing scenario evidence; the scenario fixture does not delete old files.

The original audit reproduced data-safety, ownership and reporting defects. The local Review desk records repair evidence and remaining limits. Fake-provider HTTP tests cover deterministic OAuth and notification contracts; they do not prove real account registration, email delivery, real VSS elevation, online-only file hydration or a reboot-triggered schedule.

Run credential-loss and before-sign-in experiments only in an isolated Windows
VM with disposable accounts and secrets. Never register an S4U acceptance-test
task against the developer's real account. Start each destructive comparison
from a clean checkpoint, collect evidence before logging the test subject back
in, and retain a separate verification after a password logon. Match the target
Windows build and record differences in edition, account type and security
configuration. Use the production candidate's encrypted vault and an encrypted
repository for before-sign-in acceptance; password-free local backups do not
cover vault access. See the required checks in [RELEASING.md](RELEASING.md).

`S70_AutomaticBackupRemoval` clicks the
actual Settings buttons with controlled replies: duplicate clicks, declined
approval, unfinished removal and retry. These tests do not prove Windows
registration removal or native UAC behavior. VM qualification must recover a
real interrupted removal, compare original permissions, verify both credentials
and retained transcripts, re-enable automatic backups, run a real backup and
remove them again. Compare repository contents and credential canaries before
and after, and preserve unsuccessful attempts alongside the final result.

Measure complete scheduled dispatch separately from the backup's own duration:
process start and shutdown can otherwise hide delays.

`ServiceInstallationSecurityTests` rejects unsafe installation ownership,
write/delete-child permissions, null access lists, readable private storage,
remote paths and path aliases. Native VM acceptance must also check actual
Windows file permissions and directory junctions. Descriptor-only tests do not
prove that an installer applies the intended permissions or that the service
validates every file it loads.

`BackupServicePipeTests` checks the new service transport boundary separately:
an actual fake local pipe receives no payload, the wrong identity cannot
create a service endpoint, malformed/oversized/truncated frames fail, and a
silent connected client cannot keep a read open forever. Descriptor checks
reject broad access and client-created server instances. These tests use no
Credential Manager entries or scheduled tasks. They do not establish service
installation, cross-account acceptance, vault migration or reboot behavior;
those require the isolated VM and the integrated production candidate.

`SecretsStoreTests` covers the portable vault (U153): a round trip on two
computers (separate key folders), a wrong password that changes nothing and
keeps no key, an empty password that opens without asking, a tampered KDF
(another iteration count, too few iterations, a forged no-password flag), a
damaged body that is kept and never overwritten, a newer format refused, a
`.bak` without its main file refused, a password change that keeps another
computer's key working, key copies bound to their vault id, a stale key copy
removed, reads and saves while locked (nothing written, "locked" not
"missing"), two instances keeping both writers' keys, `SetIfNewer` inside one
transaction, starting over, and a byte scan for any value or key in clear.
`VaultServiceAndPathsTests` covers the service's `unlock` (a wrong key or vault
id refused and nothing kept, the key surviving a password change, an existing
vault refused without a key, malformed requests and the removed `freeze` and
`thaw` commands) and `EngineWorkPaths`. `VaultGateViewModelTests` checks that
cancelling the password question saves nothing (destination save, cloud
Connect, the change-password dialog) and that a test email keeps typed keys in
memory. E63 and E64 run the real engine: first credential, then a byte scan of
the whole configuration folder with a positive control; a second computer with
the banner, a refused and an accepted password, the earlier history and
Settings → Saved passwords; and a vault without a password on a second
computer. Neither registers a service: the service side of `unlock` is
exercised in-process, not by a running Windows service.

`ServiceHistoryFilesTests` reads the background service's history and logs from
the portable folder: a moved folder, another backup's runs in the same folder,
an expired log and a run past the latest 30. S68, S84 and
`ServiceHistorySelectionTests` write real service history files and check
that cards, the health banner and Activity & logs follow them without moving
the user's selection.

`ServiceWorkerTests` checks that repository-only checks and cleanup do not pin
original source folders. Backup and explicit original-file comparisons still
require source protection. The isolated VM offline-source probe moves only its
synthetic source out of the way, runs the real packaged check and cleanup as
the ordinary owner, and requires a backup attempt to fail. It restores and
hash-checks the source afterward; it never uses laptop source folders.

`ServiceActivityTests` exercises real progress parsing, removal of private
transcript paths, invalid percentages, unrelated results, duplicate sources,
overlapping polls and loss of the service connection. S68 checks the completed
service result on the card and health banner; S69 renders progress and an
interrupted connection. These headless scenarios use controlled replies. The
separate VM activity probe checks actual worker output, stored final results,
and a delayed Stop request against a newer worker.

`RestoreComparisonOptionsTests` checks persisted opt-in comparison, a locked
original that is not read when comparison is off, and missing/wrong-sized
recovered files that must still fail. E50 exercises the independent data-read
checkbox and a damaged real repository. E53 clicks the recovery and comparison
controls, checks a same-size/same-date changed original with comparison off
and on, and preserves the distinction in the actual run summary. These UI
checks use headless Avalonia; they do not prove native Windows input behavior.

S33 now exercises persisted ownership records with injected process inspection/termination actions. It never sweeps the developer's running processes. Real-engine tests use disposable data and their own restic processes; live cloud tests require explicit credentials and their opt-in gate.

`ProcessOwnershipStartupTests` creates its own suspended Windows process,
before its executable module has loaded. It verifies that the actual image
path can still be saved and checked, rejects changed start times and paths,
and preserves a process whose owner is alive. The test terminates only its
own suspended child through the original process handle.

Website screenshots use the same awaited restore-history cleanup as scenarios.
An explicit test leaves a completed result behind before capturing Settings,
then requires the normal screenshot baseline and an empty restore registry.
This prevents another test's retained result from shifting the sidebar in
documentation images. Screenshot thresholds are not relaxed; intentional UI
changes require visual review before updating their reference images.
The test build copies the repository's baseline images into custom artifact
outputs as well. Otherwise `--artifacts-path` can create fresh output-only
baselines and accidentally compare a screenshot against itself.

The test host supplies a quiet network-cost probe so a hosted runner's metered or background-restricted connection does not cancel unrelated scenarios. The runtime-guard tests override that per instance and explicitly trigger cancellation. Neither proves a physical Windows network transition. Clock-format tests cover both 12-hour and 24-hour cultures. Engine files are selected for copying after the download target has completed, including on a clean checkout's first build.

S67 uses the actual Settings controls and headless pointer clicks with controlled
setup responses. Opening the page is read-only, repeated clicks cannot start two
setup operations, and cancelled approval leaves pending changes visible with a
retry action. `BackgroundBackupViewModelTests` rejects a stale status response.
`ServiceConfigurationComparisonTests` distinguishes appearance and run results
from deleted backups, destinations, schedules and other execution settings.
The VM desktop coordinator probe must separately exercise actual registration,
credential handoff, declined activation followed by newer credentials, and a
real scheduled backup. Neither the controlled UI test nor an elevated helper
callback proves a native Windows approval-dialog walkthrough.

### Quitting and schedules on Windows

`SessionTaskControlTests` checks pause/reopen, expired or reused sign-in
sessions, independent accounts, a quick Quit/reopen, force-quit escalation,
unreadable state and failed writes. Actual file-handle checks cover temporary pause-record replacement locks
while quitting, reopening and cancelling Quit; cancelling the wait preserves
the previous state and cannot report a successful save. The suite also queries
the real Windows session API and launches the actual Backspace CLI for all four work modes. While
paused, those child processes must exit before reading deliberately invalid
configuration. After reopening, the same invocation must reach normal config
loading. Two additional native checks interrupt an actual encrypted local
backup through ordinary Quit and Force quit, verify that its restic process
has stopped (and, for Quit, that restic removed its own lock after Ctrl+Break
sent from a program without a console), and reject another run while the
pause remains in effect.
`S60_ReviewFeedback` clicks the close-to-tray setting and the quit
dialogs. These checks do not log the user out or reboot the computer; actual
before-sign-in execution and live cloud access need separate acceptance proof.

### Windows updates

`WindowsUpdateTests` covers release selection, URL/checksum rejection, archive path safety, complete replacement and rollback after each move, modified staging files, preserved configuration, and a real child process holding the update lease. `S62_WindowsUpdates` clicks actual Settings controls with simulated GitHub HTTP and captures light/dark screenshots. Neither establishes a public release download, a native GUI restart, SmartScreen behavior or a real low-disk-space condition. Keep the update issue marked as needing testing until the packaged flow is exercised.

`WindowsUpdateRelocationTests` moves disposable portable-installation folders
after completed updates. Completed journals must not prevent startup merely
because their old absolute paths no longer exist; interrupted installation
journals still require full validation and must not mutate unrelated files.

`E2E_WindowsUpdateBundle` requires `BACKSPACE_UPDATE_CANDIDATE` to point to a published framework-dependent Windows folder. It verifies that real bundle through a file-backed HTTP response, replaces a disposable installation, launches its native executable with `--version`, then rolls back and compares every original file. This verifies packaging, file replacement and native CLI launch, not a running GUI upgrade or live GitHub download. The release workflow runs it against its candidate. Prerelease candidates instead must be rejected by the stable installer without changing installed files.

The same class runs the generated recovery launcher with a real release
executable while the installation is in use. Recovery must wait for that
child, report its failure exit code and leave the installed executable and
prepared journal unchanged. The path includes Unicode, spaces, quotes and
shell punctuation. A separate regression check verifies that the recovery
executable remains in place throughout replacement and rollback.

### Cleanup, restore and truthful results

`DirectReadAndRestoreTests` checks that known immutable selections avoid
repeated repository listings, while another address, different plans, missing
IDs and refreshed deletions require fresh validation (real restic).
`SnapshotReadEfficiencyTests` rejects incomplete tree output and excludes
similarly named sibling paths. These tests supplement the real-engine E44 deleted-selection check and E42/E49 restore
scenarios; they are not evidence of live cloud speed. Performance comparisons
must time the real engine and verify restored bytes, with cold startup and
optional maintenance reported separately.

`E54_PlanUpdatesAndCleanupSchedule` uses a real encrypted repository to verify
that editing exclusions affects the next backup, old snapshots remain intact,
independent cleanup does not run during backup, and explicit cleanup uses the
latest retention setting. It counts the actual cleanup commands (restic's
forget, after a backup and on the schedule) to catch duplicate inline work,
checks the backup's command line (exclude file, upload limit), and verifies
that another backup's snapshots survive. `PlanSettingsTests` checks the forget
command itself: this plan's snapshots only, nothing for a policy that keeps
everything.

`E55_OnDemandLockRecovery` uses a real restic child waiting on standard input.
The cleanup after a backup (restic run directly) meets its live lock: one
ordinary `restic unlock`, one retry, the backup saved with a cleanup warning,
the live lock and both snapshots kept. The stand-alone cleanup (restic run
directly too) gets the same one unlock and retry, and fails. After the child exits and the test releases its Windows process
handle, the next cleanup's one unlock removes the stale lock and it succeeds;
force removal is forbidden. Healthy backups issue no recovery command.
U188 recreates the owner's case: a `restic key passwd` child holds the
exclusive lock (as a check does) and is killed, and repositories.json is
removed so the next backup reads the repository's settings first. That read
takes no lock; the first locking command is refused once, the ordinary unlock
removes the dead process's lock and the backup is saved. While the exclusive
lock is live the backup fails and the lock survives.

`ResticRunnerTests` (U175 step 0) checks the runner with recorded restic output
and real processes: the password only in the environment (inherited password
file and command variables removed, never on the command line or in the
transcript), each JSON message type, cut and unfinished lines, bounded error
text, every documented exit code, and stopping: Ctrl+Break that makes a real
restic remove its lock, the end of the whole tree when a program ignores it,
and both with rclone in the tree. `DirectBackupTests` checks the direct
command lines option for option, and against the owner's transcript; maps every backup exit code to the status history
reads; and runs real restic: backups with their
cleanup and no check inside them (checks are entries of their own, U179), the
remembered parent (used without a list when restic confirms it;
missing, another plan's, another folder's, unreadable, none remembered or an
unfinished previous run fall back to the plan's latest), exit code 3, a wrong
password, a live lock, a stopped backup and the full prune after it.
`A_scheduled_backup_runs_restic_and_keeps_its_records` runs `Backspace.exe --run`
twice and watches every process under it (restic appears), then checks the
history, the remembered parent and the repository. `BackupFixedCostBenchmark` (opt-in:
`BACKSPACE_FIXED_COST_BENCH=<folder>`, `BACKSPACE_FIXED_COST_BENCH_RUNS`,
`BACKSPACE_FIXED_COST_BENCH_LABEL`) times unchanged backups end to end and
splits each into the time before restic's backup starts, the backup and the
time after it. `DataCheckLimitTests` checks the engine's reading of a check
entry's data limit (U179): 1% of the stored data up to the limit (1 GiB
without one), the command for a sample and for a full read, the recorded
depth, and, with the real engine, a full data read that runs
`check --read-data` (recorded as all data) and a 4 KiB sample that reads
4 KiB.

`E56_ConcurrentLiveAndSavedLogs` starts two real encrypted local backups with a
deliberate bandwidth limit so they overlap long enough to inspect. Their actual
restic command lines (each carrying its backup's label) must appear in the
combined live display, while their saved logs must have none in common. Keyboard selection of a completed run
must hide LIVE and exclude the other run's output. This closes the injected-log
coverage gap; it still uses headless Avalonia rather than native Windows input.

`RestorePublicationLockTests` holds real Windows file handles while recovered
files are placed at their destination. It checks delayed success, cancellation
with original-file recovery, and a new user file appearing during the wait.
These tests cover the final file-placement step after the engine has exited.

`E53_CheckHistoryAndAutomaticRestore` also pauses a real automatic comparison
while its parent backup remains active. It checks that the card names the
comparison phase, shows Stop and holds Verify until it ends (Restores doesn't
list test restores), and verifies that completion clears
the live state. The final duration includes the comparison; the compact card
keeps detailed results in its tooltip and history.
Cancelling a later comparison must leave both the card and the overall banner
requiring attention. View-model tests also cover overdue checks, fresh passing
retries, and retaining a failed backup's higher severity.

`DirectReadAndRestoreTests` (U175 step 3) runs the bundled restic
against a real repository: snapshot
lists (newest first, restic's file counts, the plan's labels, no lock),
source-relative file lists, one folder at a time ("no such folder" for a
missing path or a file), one file's bytes, which snapshot ids are read without
listing again (and which are not: a position, an unknown id, another address,
plan or source, a removed snapshot), no repository yet as no snapshot but a
missing folder as an error, a selected restore that promotes only the asked
files beside a bracket overmatch, a whole-source restore with its empty folder,
nothing restored as an error and part of it as a warning, and a damaged
repository and a cancelled restore that both keep the original. E01, E40–E42
and E44–E47 exercise the production adapter with the actual bundled engines;
record their binary versions with `scripts/record-engine-versions.ps1` when
upgrading.

`E48_EngineUpgradeContinuity` creates two encrypted backup versions with
restic 0.18.1 (an earlier bundled restic, with Backspace's labels), then continues with the current engine on the same disposable
repository and nothing remembered: it lists and browses both snapshots,
restores the oldest, adds a third backup whose parent is the old engine's
latest snapshot, and compares exact contents across four real restores. It
never opens user repositories or replaces installed binaries. This tests
engine/data continuity, not the updater, cloud accounts or a power failure.

On Windows, put the previous restic in a separate test-input folder, named
exactly `restic-0.18.1.exe`, then run:

```powershell
$env:BACKSPACE_PREVIOUS_ENGINE_DIRECTORY = 'C:\test-tools\backspace-previous-engines'
dotnet test tests/Backspace.Tests --filter "FullyQualifiedName~E48_EngineUpgradeContinuity"
```

E48 explicitly skips when the environment variable, the previous restic, or
any current bundled engine binary is missing. A skipped
rehearsal is not upgrade proof. Require both completed scenario steps and
a passing final test-runner result, and retain the recorded engine versions.

`E44_DestructiveOperationSafety` uses an encrypted local repository with two
independent backup histories. It checks explicit snapshot deletion, retention
scope, immutable restore selection after renumbering, no-replacement behavior,
flattened-name collisions, repeat erase and recovery of unmanaged snapshots
after destination cleanup. `RestoreSafetyRegressionTests` also rejects actual
NTFS junction targets and ancestors before any engine submission.

`E45_MountedSnapshotSafety` checks real lazy and bulk mounted-file reads,
cache identity after retention, nested folders and empty-only folders. The
provider tests inject interrupted or preallocated output and reject linked
cached paths. These do not establish Explorer/WinFSP or cloud-account behavior.

`WinFspSnapshotFsTests` checks directory-query restarts and marker resumption,
including an entry fetched before a Windows output buffer fills. It also
issues simultaneous reads at different offsets on one open handle: each
request must receive its own bytes. `BackupMountTreeProviderTests` checks
uppercase Windows paths, revision labels and rejection of nonexistent
revisions. These callback tests supplement an actual native mount check;
they do not substitute for reading the files through a mounted Windows drive.

`E46_RestoreOptionsThroughUi` activates the real wizard's controls with
headless keyboard input and restores real backup contents. Existing files
must survive when replacement is off, and disabling folder preservation
must place recovered files at the target root. `E47_MaintenanceOutcomeSafety`
checks real engine retention cancelled between sources, a shared repository
checked once, and the subsequent complete retry. The runtime regression tests separately inject failed,
skipped and unexpected outcomes; those simulations are not live-cloud proof.

`E50_ScheduledDataCheck` uses the real editor checkbox with headless keyboard
input, persists the opt-in setting, and runs the production check coordinator
against the bundled restic. It verifies the default structure-only path,
a sampled data check, a changed local original that must not cause failure,
and a deliberately truncated disposable pack that must fail in saved history.
The damaged pack's original bytes are restored in `finally`. This tests
corruption handling, not a guarantee that random sampling finds every damaged
byte. No OS task is registered and no live cloud traffic is measured.

`E51_TrayWindowRecreation` closes the real main window during a throttled
restore from an encrypted local repository. It verifies the Closed event,
the detached visual tree, a newly created window with the same restore model,
and the recovered file's exact bytes. Three more close/open cycles preserve
the completed result. This headless lifecycle test does not measure native
tray CPU or substitute for a Windows tray interaction check.
`TrayWindowSingleTests` asks for the window again while it is being made, as
the second click of a tray double-click did (U201): one window is made and
both requests show it.

`S63_SettingsPersistence` uses actual Settings controls and fresh reads of
the saved JSON for provider/domain/recipient changes, clearing credentials,
and switching away from Mailgun sandbox restrictions. It does not send email.
`S11_LogsViewPopulated` checks combined live output and separate saved history
with injected activity and log signals, rather than concurrent real engines.

`ConsoleLogTests` classifies real log lines (setup, progress, recap and summaries, warnings, errors, engine debug, file lines) with the near misses that must not match, and parses a run log's header, section markers, stamps and outcome block. `S96_LogConsole` drives Activity & logs through its controls with run logs in the orchestrator's format: colours from the dark-variant theme tokens, the remembered Timestamps column (read back from the saved settings), Copy of the whole log, of one section and of dragged lines (also past the edge, which scrolls), Open log file, a failed run's error list jumping to its line, several live runs and a restore with their headers, every accent's console tint against the warning and error colours, both app themes, a 100,000-line saved log (only the visible rows drawn) and the live view's 100,000-line cap.

### Production scale

`E49_TestRestoreProgress` clicks the actual sample/full chooser and Cancel
button, using headless Avalonia with the real restic and an encrypted
local repository. It verifies byte progress, two non-overlapping 25% samples,
exact comparison of each recovered file, truthful cancellation, and the
readable backup-card label at 1024 × 768. `E16_TestRestoreVerifyDrill` covers
full recovery from two sources and a same-size, same-date content mismatch.
These are local-engine tests, not live-cloud or physical-screen tests.

`TestRestoreJournalTests` covers sample ordering and limits, durable terminal
results, failed journal writes, changed destination settings, corrupt progress
and persistent dismissal. Actual Windows file handles also block replacement
of a saved record: a released lock must allow the save to complete, while a
cancelled wait must leave the previous record byte-for-byte intact.
A separate local native-process probe forces exit
after a completed batch, creates a newer backup, and restarts through the
application recovery path. It must retain the original snapshot and download
only unfinished files. Its dated report belongs in the local Review desk;
the headless scenario alone does not establish process-crash recovery.

`FileAccessRetryTests` holds actual Windows file handles for more than ten
seconds in all four paths: Test restore progress, a backup's exclude file,
temporary cleanup and Quit. It verifies that the old data stays intact while
waiting, that releasing the lock completes the operation, and that a running
scheduled-task monitor is not falsely cancelled by the pending Quit save.
`S65_FileAccessWait` clicks the actual window's Close, Keep waiting, Show details
and Cancel quit controls against a real file lock, then checks a successful
close after unlock. The screenshots use headless Avalonia; no Windows task is
registered, and the scenario does not reboot or touch Credential Manager.

`E52_RestorePauseAndCancel` clicks Pause, Resume and Cancel in both restore
views while real restic transfers run. It creates a newer snapshot during a
pause and verifies that Resume still recovers the original selection. It checks
that Cancel preserves existing files and removes staging, that the Restores
navigation button works when already selected, and that a fresh recovery
service leaves a saved paused check paused. `RestoreControlTests` adds actual
filesystem rollback and a concurrent edit that must survive cancellation;
`ActiveRestoreRegistryTests` keeps earlier sources owned until the whole
operation finishes. A separate native-process probe verifies saved Pause across
an actual process exit and relaunch. No Windows reboot is needed for that test.

`E53_CheckHistoryAndAutomaticRestore` uses the real restic and an encrypted
local repository. It opens the single Activity view through the actual card
control, checks its backup/check filters, selects the explicit deeper-check
choices in the editor, preserves an existing both-enabled configuration, and exercises both the
standalone check and after-backup paths. It deliberately changes an original's
bytes while preserving its size/date, then checks that a later structure-only
success cannot clear that failure. A passing full retry and externally cancelled
automatic check cover the opposite outcomes. The UI is headless Avalonia: this
does not prove Windows task logon or live cloud execution. `BackupCheckHistoryTests`
covers scope, due times, incomplete destinations, retained history across renames,
and changed-original eligibility in both timestamp directions.

`E40_BracketFolderRestore` uses a real encrypted local repository. It restores
2,397 files from a folder containing brackets through one folder pattern,
compares every file's bytes, rejects a similarly named wildcard match, and
preserves an existing destination file. It repeats the folder request without
an exact-file list and checks that a deceptive sibling's existing contents stay
unchanged. Six single-file restores cover paired brackets, multiple pairs and
an unmatched opening bracket, each with a deceptive sibling in the backup.
They prove that an absent sibling stays absent and an existing sibling stays
unchanged even with replacement enabled. Each restore's restic command is
checked: restic ran it itself, its selection in an include file with the
opening brackets escaped (U175 step 3). A two-file restore verifies that a
third file does not appear. This tests
the production selection and restore services; it does not click the wizard's
folder checkbox or establish that every possible filename has been tested.

`E41_NativeRestoreProgress` restores 128 MiB of incompressible data, the
whole source, with restic run directly and given no selection. The backup's own download
limit makes intermediate progress observable; the screenshot records a live
percentage.
It compares the recovered contents and verifies that cancelling a second restore
preserves an existing destination file and cleans up its staging directory.
This tests real engine cancellation through the session action; it does not
click the Cancel button.

`E42_StreamingRestoreProgress` restores two scattered files (32 MiB total)
with the production per-backup speed limit through one owned bundled restic
process. It captures real intermediate progress, compares both files, rejects
an unwanted bracket-name match, and checks that the completed bar reaches 100%.
A headless pointer click on Cancel cancels a second run and preserves an
existing file. A third run uses deliberately damaged content packs in its own
disposable repository: no staged output may replace either existing target file.
`StreamingRestoreProcessTests` separately checks bounded memory with 100,000
synthetic progress messages and rejects oversized output lines. These checks
do not establish live cloud behavior, a multi-day run or an OS refusing to kill
a process.

`E43_CloudSourceChangeDetection` is an architecture experiment, not a shipped
cloud feature. It uses real WinFSP and restic with simulated remote
files. Four separately mounted rounds establish initial reads, complete
unchanged snapshots without content reads, the negative same-size/same-date
case (old contents reused), and a distinct virtual timestamp that causes only
the changed file to be read. Every snapshot restores both complete files.
No account or original placeholder is accessed. Provider integration, temporary
space limits, durable revision metadata and original-date recovery remain work
under B035, which is parked until the user gives implementation approval.
The scenario skips explicitly when WinFSP is unavailable.

`S31_RestoresNavItem` samples the actual sidebar animation while a session
is running and checks that it stops after all sessions finish. Its sessions
are injected. `E42_StreamingRestoreProgress` also samples that animation
during a real local restore, clicks the cancelled run's card, checks that
only that result opens, and returns to the two-run list through the real
button. This is headless Avalonia with actual backup engines; native
Windows tray, display scaling and live cloud acceptance remain separate.

`S56_EditorModeAndApply` begins with two copied custom and recommended
exclusion blocks, clicks Apply twice, and verifies that both custom rules
survive in one block while obsolete generated rules are removed. It also
checks Cancel, Save and reopening the stored configuration. The related
rule tests preserve malformed custom blocks without losing user rules.

Set `BACKSPACE_E2E_SCALE=huge` to lift the shared E2E source builders and manual E11/E30/E31/E34/E35 fixtures to production-sized input. The standard huge fixture has at least 20,000 files and approximately 2 GiB; exact edge fixtures retain their special files in addition to the scale payload. Multi-source or multi-backup tests can create multiple such payloads, and backup/restore copies need additional free space. The default remains small. This switch increases local I/O and, for explicitly enabled cloud scenarios, upload/download traffic.

`FixtureFactory.Generate` resolves the override centrally; `ScenarioContext.MakeSource` honors it too. E34 keeps its include-file regression at 1,500 files normally and exercises 20,000 files/2 GiB under the override. Exact generation asserts file count and byte budget, rather than relying on a random tree reaching its target.

## Commands

For review work in this checkout, set the process's `TEMP`, `TMP` and
`TMPDIR` to a scratch folder under `audit-dashboard/private/temp/`, and
set `--artifacts-path` to a separate build folder there. Do not put new
review fixtures or cloud vaults in Windows Temp or AppData. In a synced
checkout, make a new empty build/scratch parent local-only before creating
its contents. Do not change sync settings on existing populated folders:
that can remove their cloud copies. The local Review desk README records
the prepared folder for this machine.

Source-based checks locate the checkout independently of the binary's
location. Scenario evidence then lives under the selected test output's
`artifacts/scenarios` directory; copy the relevant screenshots and reports
to the Review desk before linking them. Keep saved cloud configurations
separate from disposable scratch and preserve them between sessions.

`E2E_WindowsUpdateBundle` requires `BACKSPACE_UPDATE_CANDIDATE` to name a
published, framework-dependent Windows bundle. Set `BACKSPACE_UPDATE_PREVIOUS`
to a second, older bundle to verify replacement of a different executable
and byte-for-byte rollback. Without it, the test installs over a copy of
the same bundle and proves package handling, not a version transition.
Download delivery is simulated; installed executable launches are real.
Neither variant proves the Settings update flow or live GitHub delivery.

```bash
# Full unit + visual suite (no live cloud)
dotnet test tests/Backspace.Tests \
  --filter "FullyQualifiedName!~E2E"

# Local restic round-trip tests (runs the real restic against temp local
# repos)
dotnet test tests/Backspace.Tests --filter "FullyQualifiedName~E2E&FullyQualifiedName!~E2E_WindowsUpdateBundle&FullyQualifiedName!~E07_DropboxRoundTrip&FullyQualifiedName!~E08_OneDriveRoundTrip&FullyQualifiedName!~E09_GoogleDriveRoundTrip&FullyQualifiedName!~E10_TwoConcurrentMultiCloud"

# Live cloud tests (real Dropbox, OneDrive Personal and Google Drive)
dotnet test tests/Backspace.Tests --filter "FullyQualifiedName~E07_DropboxRoundTrip|FullyQualifiedName~E08_OneDriveRoundTrip|FullyQualifiedName~E09_GoogleDriveRoundTrip|FullyQualifiedName~E10_TwoConcurrentMultiCloud"
```

### Linux and macOS

The test project builds for `net10.0` on Linux and macOS (or on Windows with
`-p:CrossPlatformTests=true`). Test files that use Windows-only parts (the
background service, WinFsp, Credential Manager, Task Scheduler, shadow copies)
are listed between the "windows-only tests" markers of
`Backspace.Tests.csproj` and left out there. The "Cross-platform tests"
workflow (`cross-platform-tests.yml`) runs the suite on GitHub's
`ubuntu-latest` and `macos-latest` runners, on demand and every Monday, and
keeps the results and scenario screenshots as artifacts. Screenshots are
compared with their references on Windows only: the references are Windows
renders.

### Live cloud tests

E07–E10 back up to a real account and restore byte-for-byte. They need no
setup on the maintainer's computer: `CloudCredentialsLoader` reuses the saved
test sign-ins in `audit-dashboard/private/temp/BackspaceReviewTests/` (one
Backspace configuration per provider, each signed in to Backspace's own app
folder; Git-excluded). Point `BACKSPACE_CLOUD_TEST_VAULTS` at another folder of
saved configurations if needed. Each run writes to a new
`backspace-review/e2e/<date>/<id>` folder inside the app folder, and a token the
engine refreshed is written back to the saved vault, so the sign-ins stay
current. An explicit `DROPBOX_TEST_TOKEN`, `ONEDRIVE_TEST_TOKEN` or
`GOOGLEDRIVE_TEST_TOKEN` in `tests/Backspace.Tests/secrets.env` takes
precedence. When neither exists, the tests skip with the reason. Set
`BACKSPACE_TEST_INTERACTIVE=1` only when a person can approve a new sign-in on
the provider's page. The vaults are encrypted for the Windows account that
created them; copy the configurations separately when moving the checkout, and
sign in again on another account.

`E58_DropboxScaleBenchmark` times a 20,000-file, 2 GiB backup (or another
`BACKSPACE_BENCHMARK_FILES` count at the same ~105 KiB per file) to Dropbox,
Google Drive or OneDrive (`BACKSPACE_BENCHMARK_PROVIDER`), and an
incremental backup after rewriting a quarter of the files. It runs only when
`BACKSPACE_BENCHMARK_ACTION` is `generate`, `mutate`, `backup` or `tool`; `tool`
runs the bundled restic or rclone against the same repository for profiling.
See the test's summary for its other variables. Keep the benchmark tree out of
any folder a sync client uploads (this checkout may live in Dropbox): the
client's own upload competes for the same bandwidth and distorts the timings.

The visual / scenario harness writes PNGs under
`tests/Backspace.Tests/artifacts/` and a `SUMMARY.md` after each scenario
run — see [tests/Backspace.Tests/Scenarios/README.md](../tests/Backspace.Tests/Scenarios/README.md)
for the deep-dive on the harness + how to add a scenario.

To regenerate the 13 screenshots used on the website:

```powershell
pwsh scripts/regenerate-website-screenshots.ps1
```

## Visual + interaction E2E test philosophy

The concurrent-log scenario deliberately starts its final selection on Photos
and uses the closed backup picker's Arrow Up navigation to reach Documents.
Do not use Home as a substitute: that key can leave a closed ComboBox unchanged,
making a passing assertion depend on which concurrent backup finished last.

The standing rule: don't QA by hand every time something changes. Build
the test pipeline so an agent (or CI) can:

1. **Boot the real app** headlessly — same `App.axaml.cs`, same DI
   graph, same resources — pointing at a throwaway
   `BACKSPACE_CONFIG_ROOT`.
2. **Generate dummy source data** under
   `%TEMP%/backspace-scenarios/<name>-<unique-id>/sources/...`
   via `SyntheticFileTree.Generate(...)`. Keep totals small (≤ a few MB
   per scenario) so the suite stays fast. The folder is deleted when the
   scenario ends (junctions are removed, never followed); set
   `BACKSPACE_KEEP_SCENARIO_DIRS=1` to keep it for debugging.
3. **Drive each scenario** through scripted UI interactions — show
   MainWindow, click buttons, fill text, navigate the wizard, run the
   backup, restore, verify. Each step renders a PNG under
   `artifacts/scenarios/<name>/screenshots/<NN>-<step>.png`.
4. **Report missing credentials explicitly.** `SecretsLoader` reads the
   ignored `tests/Backspace.Tests/secrets.env` file. The scenario report
   records a skip and its reason in both the scenario report and the xUnit v3 runner result. Inspect both. Cloud E2E scenarios
   acquire missing tokens only with `BACKSPACE_TEST_INTERACTIVE=1`.
   `BACKSPACE_E2E_NONINTERACTIVE=1` overrides that opt-in. An ordinary
   regression run skips missing credentials without opening a browser.
5. **Write a markdown summary** at `artifacts/scenarios/SUMMARY.md`
   after each suite run, listing every scenario's status, screenshot
   index, and any anomalies the auditor caught.
6. **Iterate.** The agent runs the suite, opens the summary + select
   PNGs, files findings, fixes them in the source, re-runs.

Account registration, actual Windows elevation, packaged-app integration,
physical device transitions and visual motion still need environment-specific
validation. Deterministic tests are useful evidence within their stated scope;
a passing model test does not establish the corresponding desktop interaction.

**Adding a scenario.** Subclass `ScenarioBase`, override `RunAsync`,
script the user flow with `ctx.Driver` helpers, and call
`ctx.Snapshot(window, "name", "what the picture should show")` at interesting
state transitions. Mark the public runner method with `[AvaloniaFact]`.
Follow the existing E07–E10 credential and interactive gates for live cloud
tests. Finish or acknowledge cancellation of live operations before disposing
the UI driver; teardown must not kill an active backend to make a test pass.
Teardown closes every window the driver showed and disposes its view model,
including the view model of a view hosted in a plain window: left alive, its
cards posted work into a later test's dispatcher (U066).

## Coverage matrix — every user-driven action vs. its scenario

The goal of this action coverage matrix is that no
user-reachable action gets shipped without an E2E or scenario covering
it. Two production bugs (Mount fails on dist, restore crash on long
include list) shipped under green scenario suites this month; the gaps
column below is where the same class of false-green could still hide.

**Status key:**
- ✅  Evidence covers the bounded action described in that row; check the latest result.
- 🟡  Partial — the stated limit still prevents a complete user-flow claim.
- ⛔  Environment-dependent proof remains (credentials, elevation or physical devices).
- ❌  No executable evidence identified for that action.

Most `Sxx` scenarios use `InMemoryBackupEngine`. Some render or click real
Avalonia controls; others only call view-model or service methods. `Exx` uses
the real local engine, except live cloud cases which are explicitly gated.
Neither naming scheme proves that a packaged executable was launched or that
the exact user interaction occurred. The scope notes below take precedence over
older scenario comments that describe broader coverage.

### Onboarding & first-run

| Action | Scenario(s) | Status |
|---|---|---|
| Open onboarding | `S01_OnboardingFirstTime` renders the seeded window; no packaged fresh-install launch | 🟡 |
| Walk the three wizard steps | `S01`, `S21_OnboardingScheduleStep` | ✅ |
| Pick source folder via tree picker | `S24_SourceTreePicker` | ✅ |
| Pick destination (local) | `S01`, `E05_EasyModeWizard` | ✅ |
| Pick destination (Dropbox / OneDrive / GDrive) | `S08` renders kinds; `S51_OneDriveEditorLoopback` drives sign-in with fake provider HTTP and real local callback listener. E07–E09 require real credentials and do not drive editor sign-in. | 🟡 |
| Pick destination (S3 / Wasabi / R2) | — | ⛔ deferred: needs real bucket creds, same shape as E07 |
| Set schedule | `S21` | ✅ |
| Apply recommended exclusions in onboarding | `S42_EasyAdvancedSwitchAndRecommended` asserts the default checkbox model flag; it does not click Apply or assert generated filters | 🟡 |
| Switch Easy ↔ Advanced mid-wizard | `S56_EditorModeAndApply` clicks the real coordinator's mode buttons, edits draft text, ticks a recommended group, verifies custom ordering, preserves both storages/overrides, tests Cancel isolation and actual Save plus fresh reload | ✅ |

### Backup creation/edit (Easy + Advanced)

| Action | Scenario(s) | Status |
|---|---|---|
| Create new backup (Easy) | `E05_EasyModeWizard` invokes the wizard view-model save command then real backup/restore; not every native click | 🟡 |
| Create new backup (Advanced) | `E06_AdvancedModeEditor` commits editor fields and saves the model then real backup/restore; not the Save-button event | 🟡 |
| Edit existing backup (Easy) | `S07_BackupEditorEdit` (Easy variant) | 🟡 |
| Edit existing backup (Advanced) | `S07_BackupEditorEdit` (Advanced variant) | 🟡 |
| Edit sources, ensure persistence | `E23_EditBackupSourcesPersists` | ✅ |
| Edit filter text inline | `E30_CustomFilter` | ✅ |
| Apply recommended exclusions (full effect on file count) | `E31_FilterExcludesEverything` | ✅ |
| Estimated-savings indicator label states | `S34_SavingsLabelStates` scans real files and asserts view-model labels; `S50` renders the zero-match label | ✅ |
| Savings message wraps on the Recommended card (no crop) | `S50_RecommendedRowTextWraps` | ✅ |
| Recommended groups, automatic updates and filter suggestions (U165) | `S94_RecommendationUpdates` (Easy save with automatic updates on and a later load adding a new rule; clicking Advanced turns them off and the box turns them on; three groups with the caches' rule list opened; card line only on the backup with a ticked changed group; Review with each suggestion's measured effect; a hidden one not brought back by ticking its group). U165 follow-up (no Apply): a new backup's groups read "Will be added when you save"; saved and reopened they read "In place" under a green Saved line; a pointer click unticking one reads "Will be removed when you save" with the amber line and the footer note, ticking it back clears both; saved and reopened it reads "Off", light and dark; used suggestions read "Will change when you save". `S79` and `S88` check the same states after suggestions and a deleted rule. `RecommendationUpdatesTests` (the owner's U151 texts and an always-block-only text convert to the same excluded set, checked with restic's matching on probes and falsified by a broken conversion; automatic updates skip hidden rules; card-line conditions; Easy saves; one-walk measurement). The background service's own settings load is not run | ✅ |
| Exclude a folder from What this backup added (U178) | `S95_ExcludeFolderFromChanges` (pointer clicks on Exclude this folder, Exclude from next backups and Undo in the real window opened from the activity: the rule shown, the saved settings file before, after and after Undo, the button kept whole at 640 pixels, no button for the backed-up folder itself, a folder already excluded, an exception that keeps the folder in). `FolderExclusionTests` (the rule for drive and nested folders, brackets, spaces and `$`, its place in the custom block with every other byte kept, no duplicate, Undo). A real backup after excluding is not run here; E54 covers an edited rule reaching the next backup | ✅ |
| Grace controls available before and during skips | `S28_GraceBlockVisibility` | ✅ |
| Reset grace period (Easy + Advanced) | `S28_GraceBlockVisibility` (Easy Save/reopen and Advanced reset) | ✅ |
| Remove backup | `S41_DestinationAndBackupCrud`; `BackupRemovalTests` (stored copies erased, sibling kept, unplugged drive finished later, removed destination dropped); `S10` (confirmation wording) | ✅ |
| Administrator access in the creation flow | `S71_AdministratorAccessOffer` (ticked choice before saving, prompt only after save, no prompt when unticked or editing, sidebar warning badge and its flyout fix, no badge once on or without VSS, Settings section collapsed when on and open when off); real UAC and service needs the VM check | 🟡 |
| Applying later changes without a prompt | `ApproveChangesInFlowTests`: with access on, a saved change is applied once and never prompts; nothing happens when access is ready, off, unfinished or being removed; a running backup delays it without an alarm; a failure shows its reason; a setup from an earlier version asks once. `S67_AutomaticBackupSetup` shows the folded section while applying and the reason with Try again on failure. The real task was checked in the VM (U043) | ✅ |
| Picking a destination in Add a backup | `OnboardingDestinationPickTests`: clicks the real list and Continue; a picked destination is accepted, nothing picked still shows the error; no "first backup" wording once backups exist or when editing (U042) | ✅ |
| Notice pills jump to their backup | `S73_NoticePillJump` (a card below the fold is realised, scrolled into view and outlined, then the outline clears; its skipped band is tinted) | ✅ |

### Backup execution

| Action | Scenario(s) | Status |
|---|---|---|
| Run backup manually | `E01_LocalRoundTrip` (Smoke/Many/Large), `S22_BackupCardRunningState` | ✅ |
| Run backup at scale (≥ 1500 files) | `E34_RestoreLongIncludeList`, `BACKSPACE_E2E_SCALE=huge` lift | ✅ |
| Stop mid-run | `E02_StopAndResume` | ✅ |
| Resume after stop | `E02` | ✅ |
| Two concurrent backups | `E03_TwoConcurrentBackups` | ✅ |
| Two concurrent multi-cloud backups | `E10_TwoConcurrentMultiCloud`; requires explicit real-account opt-in | ⛔ |
| Reject a duplicate run of the same backup | `E21_QueueWhileRunning` | ✅ |
| Queue separate processes using aliases for one repository | [Four-process local check](../audit-dashboard/evidence/repository-alias-processes-20261016/report.html): real encrypted backups with `Default` / `default`, successful restore hashes, an unrelated repository that proceeds, and a cancelled waiter that never starts backup work. The first process deliberately holds the production gate before engine work; all processes use the same Windows session. `RepositoryQueueAliasTests` adds empty-name, punctuation and duplicate-alias cases with a simulated engine. | ✅ bounded real-engine process check |
| Skip-on-battery → run skips | `RuntimeGuardCoverageTests` inspects the actual unregistered Windows task setting both on/off; `S36` covers editor persistence. OS battery transition unexercised | ⛔ OS acceptance |
| Stop-on-battery → run stops | Actual unregistered Windows task setting both on/off; no battery transition during a running task | ⛔ OS acceptance |
| Require-network → run skips when offline | Actual unregistered Windows task network setting both on/off; no machine connectivity change or registered-task execution | ⛔ OS acceptance |
| Metered-network watchdog stops mid-run | `RuntimeGuardCoverageTests`: actual backup/prune/check orchestration, injected metered signal after first cell starts, cancellation/persistence and opt-out negatives. `WindowsMeteredNetworkProbeTests` reads the real Windows network cost API (every connected profile must parse to a valid cost type) and checks that the change subscription is released when the watchdog stops. A real metered connection is not exercised automatically | ✅ injected signal + real API read |
| Catch-up missed runs after sleep | `BackupSchedulerTests` with a fake clock, catch-up across restarts; no real sleep/resume or reboot | ⛔ OS acceptance |
| Headless `--run <name>` | [Actual published workflows](../audit-dashboard/evidence/packaged-workflows-final/report.html): existing option-shaped name `-h`, real snapshot and both files verified by SHA-256; missing backup exits 2. `DirectBackupTests.A_scheduled_backup_runs_restic_and_keeps_its_records`: two runs with cleanup, restic under Backspace | ✅ isolated local repository |
| Headless `--run-all` | [Actual published workflows](../audit-dashboard/evidence/packaged-workflows-final/report.html): both enabled/unpaused backups run; paused and disabled repositories remain untouched | ✅ isolated local repositories |
| Schedule fires at the configured time | `BackupSchedulerTests` and `RuntimeGuardCoverageTests`: daily, hourly, weekly and monthly next times, per-kind runs and conditions with a fake clock; real timing is the VM's check | ⛔ OS acceptance |

### Restore

| Action | Scenario(s) | Status |
|---|---|---|
| Restore everything | `E01` (round-trip), `S04_RestoreWizardShots` | ✅ |
| Restore from older revision | `E22_RestoreFromOlderRevision`, `S14_RestoreSingleSourceRevisionPicker` | ✅ |
| Restore cherry-picked files | `E17_RestoreCherryPickSubset`, `S15_RestoreFilesStep` | ✅ |
| Restore over original source (overwrite) | `E18_RestoreToOriginalSource` | ✅ |
| Multi-source restore (pick one source) | `S20_RestorePickSourceMulti` | ✅ |
| Restore wizard target step | `S23_RestoreTargetStep` renders both states; `InteractionTests.RestorePickTarget_RadioToggle_DrivesTargetOriginalSource` clicks both radios | ✅ |
| Restore wizard summary | `S16_RestoreSummary` checks success, warning, error and cancellation explanations. `E46_RestoreOptionsThroughUi` verifies that deliberately preserved existing files are explained on screen and in the saved log, with a “not restored” count rather than a false failure count. | ✅ bounded local checks |
| Restore folder → minimal cover (1 pattern not N files) | `S47_RestoreFolderMinimalCover` projection plus `E35_FolderCollapseEndToEnd` collapsed selection through the real restore engine and byte comparison | ✅ |
| Restore long include list (CreateProcess ceiling) | `E34_RestoreLongIncludeList` | ✅ |
| Restore over slow network (retries) | — | ⛔ deferred: needs network-shaping infra |
| Restore with bandwidth limit applied | `E42_StreamingRestoreProgress` applies the production per-backup limit to a real local restore; it does not measure throughput on a cloud connection. | 🟡 local only |
| Cancel a running restore | `E41_NativeRestoreProgress` cancels a real whole-source restore through the session action; E42 and E52 click Cancel during real restic work; `DirectReadAndRestoreTests` cancels one and runs a cleanup right after it. Existing files survive. | ✅ bounded local checks |
| Live-tail restore progress | E41 captures real whole-source progress; E42 covers scattered selections and a per-backup limit, including a completed 100% bar. | ✅ bounded local checks |

### Mount/Browse snapshots

| Action | Scenario(s) | Status |
|---|---|---|
| Mount snapshot as readable Windows volumes (WinFSP) | `E04_SnapshotMountWindows` mounts two real volumes, enumerates and compares every file's bytes, unmounts one without breaking the other. Requires WinFSP. | ✅ |
| Mount from the packaged app's card and open Explorer | E04 exercises developer-build coordinators; S49 exercises the card with a fake mount registry. Neither launches the distributed executable. | 🟡 |

### Maintenance

| Action | Scenario(s) | Status |
|---|---|---|
| Prune after backup (inline) | `E14_PruneAfterBackupRealEngine` | ✅ |
| Standalone prune (cadence) | `RuntimeGuardCoverageTests` verifies actual unregistered weekly trigger/action and real orchestration cancellation/opt-out; `S39` covers fake-engine invocation. OS due-time firing unexercised | ⛔ OS acceptance |
| Check after each backup (an entry of its own, U179) | `E15_CheckAfterBackupRealEngine`: the backup runs restic and no check, then the structure check runs as its own run (`restic check`, run directly) | ✅ |
| Standalone check (cadence) | Actual unregistered monthly trigger/action and real orchestration cancellation/opt-out; `S39` covers fake-engine invocation. OS due-time firing unexercised | ⛔ OS acceptance |
| Test-restore drill | `E16_TestRestoreVerifyDrill`: actual button pointer clicks in headless Avalonia; the real restic, two sources, an empty file, all file counts, persisted runs, and a second-source same-size mismatch that must fail. Screenshots show the result beside the button. | ✅ |
| Drill skips mid-flight-edited files | E16 deliberately retains the source modification time to test byte mismatch detection; it does not simulate a concurrent edit. `BackupVerifierMidFlightDirtyTests` covers record state and retry replacement only. | 🟡 |
| Maintenance policy cards rendered | `S26_MaintenancePolicyCards` | ✅ |
| Checks as a list (U179): Add a check, edit, Remove; Easy mode sentence; conversion of the old single check; checks due together run one after another; the test restore limit never exceeded | `S99_ChecksList` (pointer clicks on + Structure check and Remove, invalid share refused, light and dark, Easy step 3). `BackupChecksTests` (the owner's saved shapes in the app and the service's strict reader, defaults, sentence, yearly, one plan per check, plain summaries, card lines), `MaintenanceOutcomeTests.Checks_started_together_run_one_after_another`, `CheckOverdueTests` (each entry waits on its own), `TestRestoreJournalTests` (limit) | ✅ |

### Destinations

| Action | Scenario(s) | Status |
|---|---|---|
| Add destination (each kind) | `S08_DestinationEditorVariants` renders preconfigured variants; does not authenticate/save every kind | 🟡 |
| Edit destination | `S41_DestinationAndBackupCrud` | ✅ |
| Delete destination | `S41` | ✅ |
| Locate a moved repository and keep earlier snapshots | `E33_DestinationPathRenameMidLife` moves a real encrypted repository, clicks Cancel / Use this repository, rejects a wrong password, then backs up and restores through the new path. The native picker is replaced by its chosen path; Save uses the view-model method. | 🟡 native picker remains |
| Change storage password | `E19_ChangeStoragePassword`, `S09_PasswordDialogs` | ✅ |
| Erase a destination's data | `E20_EraseStorageWipesDataPreservesForeign`, `S10_DestructiveConfirmDialog` | ✅ |
| Destination pill click → edit dialog | `InteractionDestinationClickTests` | ✅ |
| External-drive volume-name matching | `CloudNotificationRepairTests` injects matching drive roots and checks rebasing; physical drive-letter reassignment remains | 🟡 |
| Multi-destination card | `S17_MultiDestinationCard` | ✅ |
| Destinations view busy state | `S25_DestinationsViewBusy` | ✅ |

### Filters

| Action | Scenario(s) | Status |
|---|---|---|
| Hand-edit filter text | `E30_CustomFilter` | ✅ |
| Filter excludes everything (edge) | `E31_FilterExcludesEverything` | ✅ |
| Tick a recommended group (no Apply) | `RecommendedFiltersTests` (`WithGroup`: only that group changes, hand-written block rules kept, copied block dropped, untick-then-tick restores the text) plus `S56_EditorModeAndApply`: pointer clicks on the caches box keep custom rules above the block across Easy/Advanced mode switches | ✅ |
| Custom-block markers placed above generated | `S30_CustomExclusionMarkerPlacement` asserts merged strings, legacy-rule preservation and idempotence; no rendered editor in S30 | ✅ |
| What these rules leave out (Filters › Advanced › Left out, U177) | `S97_WhatTheRulesLeaveOut` (pointer clicks: nothing before Run; Run; topmost rows sorted by size with their rules; total; search; By rule; a rule link selecting the line on the Rules tab; Keep this adding an exception; a second run compared with the saved rules, New and Back in; Stop while held before reading; light and dark). `LeftOutAnalyzerTests` (topmost collapsing, last-match attribution with `!` exceptions, rules matching nothing with lines, recommended ones apart, the backed-up folder left out whole, comparison with saved rules, cancel, missing folder, links not followed, Keep this rule). `CloudFileBehaviorTests` (online-only files counted apart) | ✅ |
| Left out, estimate and real restic agree | `E57_CustomFilterParity` (restic `--dry-run` with the same exclude file, real backup and recovery) | ✅ |
| Duplicacy rules converted once at load | `DuplicacyFilterConverterTests` (byte-for-byte against the approved texts, in their U165 form), `S90_DuplicacyRulesConvertedOnce`, E57 part 3 | ✅ |

### Settings

| Action | Scenario(s) | Status |
|---|---|---|
| Switch accent / theme | `S05_LightDarkThemeShots`, `S12_SettingsAndAccents` | ✅ |
| Configure Healthchecks.io | `CloudNotificationRepairTests` fake HTTP exercises custom PingUrl, terminal pings and retries; live service remains | 🟡 |
| Configure Resend / Mailgun email | `CloudNotificationRepairTests` fake HTTP exercises API requests, retry and visible nonfatal failure; live delivery remains | 🟡 |
| Toggle notification gating per event | `S45_SettingsTogglePersistence` | ✅ |
| Saved passwords: first-credential password, new-computer banner and unlock, no-password vault, Settings section (U153) | `E63_SavedPasswordsVault`, `E64_NoPasswordNewComputer` (real engine, real password window; the background service's side is in-process only), `VaultGateViewModelTests` | ✅ desktop; 🟡 service on a real Windows service |
| Legacy restore-delay configuration | `S45` directly writes the old `RetryDelaysSeconds` list and checks that its order remains in the current configuration. This is a compatibility check, not an active Settings control or engine retry feature. | ✅ configuration compatibility |
| Section-header hover wash (no Fluent band leak) | `S55_SettingsHoverAnimation` sends real headless pointer movement and advances the production transition with a controlled per-control clock. It checks intermediate alpha/RGB values, rejects a white hue, and checks left extension/corners and return to transparent in both themes. This does not measure native frame timing or display-scale rendering. | ✅ controlled-clock animation |

### Monitoring & notifications

| Action | Scenario(s) | Status |
|---|---|---|
| Toast on Success / Skipped / Failure | `S50_ExactFailureNotification` checks compact failure/skipped popups, scrollable long messages, exact-run navigation and dismiss-without-navigation. A separate native Windows helper records actual size and position at the desktop's display scaling. Run history is prepared; a full live-failure desktop flow remains separate. `S48_NotificationsWiring` only checks no-throw dispatch. | 🟡 |
| Healthchecks.io ping on each run terminal | `CloudNotificationRepairTests` fake HTTP proves independence from daily human-alert cap and email delay; live endpoint remains | 🟡 |
| Email on Failure | `CloudNotificationRepairTests` fake HTTP proves bounded retries and visible errors; actual inbox delivery remains | 🟡 |
| Sound alert on Failure | `S48` returns without a desktop sound assertion; audible output remains | 🟡 |
| Failure alert opens the exact failed run | `S50_ExactFailureNotification` pointer-clicks the real popup button and checks the earlier failed row even with a newer success; a missing run ID must not select an unrelated run | ✅ headless interaction |
| Close-to-tray preference takes effect immediately | `S60_ReviewFeedback` starts with the tray hidden, clicks the actual checkbox, immediately closes, and reopens the same window | ✅ headless interaction |
| Force quit remains available while ordinary Quit waits | `S60_ReviewFeedback` keeps an operation lease open, starts ordinary Quit, opens Force quit, then cancels it. Native menu interaction and actual forced process exit remain untested. | 🟡 |
| Restore list opens one result without a duplicate card | `S60_ReviewFeedback` clicks View results and All restores on the real views using prepared session history | ✅ headless interaction |
| External scheduled results appear when returning to the app | `S53_ExternalScheduledRefresh` uses separate config/history writers and real navigation/activation; no periodic watcher | ✅ |
| Health banner state matrix | `S13_HealthBannerStateMatrix` covers backup run states. `E16_TestRestoreVerifyDrill` checks that a real byte mismatch makes both the card and banner fail, and a passing retest clears the warning. View-model checks keep the warning after a later successful backup and avoid duplicate counts; E49 confirms cancellation preserves the previous completed result. | ✅ headless UI + real local engine |

### Edge cases (the boundary set)

| Action | Scenario(s) | Status |
|---|---|---|
| Unicode names (emoji, RTL, CJK, NFD) | `E11_EdgeCaseFixture` + peculiarity set in every E01 size | ✅ |
| Brackets in filenames (`[draft]`) | E01 matrix, E40 and `DirectReadAndRestoreTests`: private staging, literal requested-output promotion beside a real overmatch, unrelated target-file preservation; no target-wide deletion sweep | ✅ |
| Zero-byte / leading/trailing space / dotfiles | peculiarity set | ✅ |
| Compound extensions, long names | peculiarity set, `S18_LongNamesOverflow` | ✅ |
| Future mtimes | peculiarity set | ✅ |
| NTFS-specific shapes (ADS, hidden, system, junction) | E11 compares regular-file contents and reports the unsupported junction. Regular-file hashing does not prove ADS/ACL/attribute fidelity. | 🟡 |
| Locked file during backup (FileShare.None) | Peculiarity generator supports an opt-in sentinel; its presence alone does not prove an elevated locked-file backup | 🟡 |
| VSS shadow-copy path | Native Windows probe accepted UAC, backed up an exclusively locked file, restored identical bytes while it remained locked, and cancelled another elevated backup without reporting success. `S64_VssProtection` clicks the warning, acknowledgement and sidebar recovery controls, and checks the warning text and action layout at 920 × 540 in both themes. This covers the disposable local fixture, not every VSS provider or source application. | ✅ bounded native + UI checks |

### Headless / CLI

| Action | Scenario(s) | Status |
|---|---|---|
| `--run <backup-name>` | [Actual published workflows](../audit-dashboard/evidence/packaged-workflows-final/report.html): real option-shaped backup name `-h`, snapshot count and every fixture byte verified by SHA-256; missing backup exits 2 | ✅ |
| `--run-all` | [Actual published workflows](../audit-dashboard/evidence/packaged-workflows-final/report.html): real snapshots for enabled/unpaused backups, skipped paused and disabled repositories, independent byte checks | ✅ |
| `--migrate <duplicacy-path>` | [Actual published workflows](../audit-dashboard/evidence/packaged-workflows-final/report.html): isolated legacy import preserves names, source, destination, encryption setting and filters; second invocation preserves identity without duplicates | ✅ |
| `--prune <name>`, `--check <name>` | [Actual published workflows](../audit-dashboard/evidence/packaged-workflows-final/report.html): both maintenance modes exit 0 against the disposable repository; all snapshot file hashes still match afterward | ✅ |
| Generated Windows task action | [Actual action execution](../audit-dashboard/evidence/packaged-task-action-final/report.html): spaced executable path, real backup snapshot, hostile literal names, child wait/exit code, missing-main fallback and both-missing failure; no task registered | ✅ action execution |
| `--version`, `--help` | [Actual published executable report](../audit-dashboard/evidence/packaged-vault-final/loader/report.html): output/version, exit status and PID-matched native-loader trace; parser aliases also covered | ✅ |
| Unknown/mixed/trailing/missing CLI arguments | `CliParserCoverageTests`: 32 positive/negative cases. [Actual published checks](../audit-dashboard/evidence/packaged-vault-final/loader/report.html): unknown flag/positional input and missing required arguments return exit 2 without creating configuration; option-shaped backup names remain valid data | ✅ |

Guard, scheduler-definition and parser proof: [44 passing cases plus 13 scheduled-action regressions](../audit-dashboard/evidence/infrastructure-tests/runtime-coverage-report.html). Battery/network availability and maintenance due times are checked by Backspace's own schedule (`ScheduleConditions`, `SchedulePlanner`); manual orchestration does not have a separate battery or due/not-due branch. The report distinguishes injected signals, unregistered OS definitions, and actual packaged processes from real machine-state acceptance.

### Status legend

- ✅ — evidence covers the bounded action and scope stated in the row;
  it does not mean the user has accepted the issue or the latest run passed.
- 🟡 — partial coverage (one mode/path, or unit-only on a path the
  user reaches through UI). Worth a follow-up scenario when the
  surrounding code is next touched.
- ⛔ — intentionally deferred with a stated reason (interactive
  infrastructure, secret-gated, or requires real removable / VSS /
  network shaping infrastructure). Not a coverage gap that can be
  closed in-process; routes through the secret-loader skip path
  when relevant.
- ❌ — no executable evidence identified. The matrix intentionally keeps
  gaps visible instead of treating a similarly named Sxx scenario as proof.

## Standing instruction for any agent that runs the scenario suite

When you run the scenarios (directly, via `dotnet test`, or as part of a
broader run), don't stop at "tests passed/failed":

1. Read `tests/Backspace.Tests/artifacts/scenarios/SUMMARY.md`.
2. For every `❌ FAIL`: investigate, fix the underlying code in
   `src/Backspace/`, re-run. If you can't fix in one or two passes,
   surface a clear diagnosis instead of leaving it unaddressed.
3. For every `**Anomalies:**` block: open the listed PNG. If it's a
   real visual regression (blank frame, clipped control, wrong colour,
   broken hover), fix it in source. If it's a false positive, tighten
   `tests/Backspace.Tests/Scenarios/VisualAuditor.cs`.
4. For `⊘ SKIP` rows whose reason is a missing secret: do nothing.
   Those are user-supplied; mention them once in the turn summary.
5. After any fix, rerun and require both completed scenario steps and
   a passing test-runner result. `STEPS COMPLETED` alone is insufficient.

A single fix-and-rerun pulse, not an unbounded loop. Stop and report if
real issues remain after two passes.

Screenshot generators now compare each newly saved image with its baseline in
the same test call. The directory-wide diff test is only an additional check;
its execution order can no longer silently substitute previous-run images for
fresh render verification. Inspect an intentional visual change before updating
its baseline. The local audit dashboard retains before/after images separately.

Screenshot publishing retries brief Windows file locks. A persistent lock still
fails the test and preserves the previous image; dedicated checks exercise both
outcomes using actual file locks. Scenario cleanup restores the process-wide quit
flags, and shutdown scenarios must release any test-owned shutdown admission
before another scenario starts. Dispatcher-dependent tests use `AvaloniaFact`
so they cannot accidentally initialize a fallback dispatcher before the headless
application starts.

Avalonia's per-test isolation clears the UI dispatcher, and whichever thread next
reads `Dispatcher.UIThread` becomes the UI thread. A background continuation
left by an earlier test could win that race, so setup failed with "a different
thread owns it" (seen late in full runs). `HeadlessApp` now claims the dispatcher
for the session thread before Avalonia builds its compositor.
`HeadlessDispatcherClaimTests` reproduces the race with a background thread that
keeps reading the dispatcher. Keep per-test isolation; the per-assembly level
shares application state and broke screenshot and interaction tests. For the
same reason, styles must not use Avalonia's shared static decorations such as
`Value="Underline"`: that object belongs to the first test's dispatcher. Declare
an inline `TextDecorationCollection` instead.

Before opening a scenario's first window, the harness awaits dismissal of earlier
restore results, including the saved-journal write. A dismissal still in progress
must finish first; a failed write must fail preparation while keeping that result
visible. Starting a dismissal without awaiting it can leave an unrelated restore
card in a later scenario. The harness checks both delayed success and failed
persistence without weakening the scenario's expected card count.

The general window screenshot generators advance real render frames for a short
settling interval before capture, so initial caret rotations and theme brushes
have finished. Transitions stay enabled. `S55_SettingsHoverAnimation` separately
captures and checks intermediate hover values with a controlled clock rather than using this settling
step. `S54_EditorialMinimumWidth` checks both themes at 1180 × 720, including the
retained Restores navigation row, all action-label bounds and viewport edges,
and a deliberately narrow card that must wrap its actions onto multiple rows.
It also saves complete light/dark card references at 1180 × 840; this capture
size does not change the app's startup size or minimum. S58 checks that Activity
and Test restore stay inside the card without clipped labels or overlapping
actions. S54 also opens the More menu and checks that its actions disable while
a backup runs. S100 photographs the More menu with a hovered item in both
themes, checks a disabled item fades, and checks More itself is greyed out with
its reason while the backup runs (a real click opens nothing). S101 samples the sidebar's
hover fade frame by frame in both themes: every frame must be the hover colour
at some opacity (a fade from Transparent passed through whitish tones on the
dark sidebar, U194). S102 runs a check on the owner's three backups: Backup
health shows it with the turning icon, the short name, the bar and the step,
its hover has everything, Last backup keeps the backup's result, the banner
says only "2 backups need attention" with each pill's reason on hover, and a backup that has
stored nothing says why it has no checks (U197–U200); the bar and step sit in
the strip below the sources (U204), and a test restore started from the card
shows the same way (U205). S103 opens the card's Verify window (U213): the
backup's own checks ticked with their latest result, other checks to run once
(a test restore with its own size), and a once-only check run through the
orchestrator, not skipped as removed. S104 shows a running check at the top of
the card's Activity window with "Open live log", which opens Activity & logs
on that backup's running entry (U216). CheckProgressTests also holds a real
restic lock while a check starts: the check waits, says so, and passes once
the lock is gone (U215). CheckProgressTests reads
restic's check output and runs a real full data read slowly enough to see the
packs counted part-way (U198). E53 clicks Activity against a real encrypted local backup,
checks the backup/check filters and opens the saved log;
its failed, passing and cancelled restore comparisons also exercise the card's
status colors and wording. These are headless Avalonia interactions, not a new
native Windows display-scaling check.

### Laptop working areas

MainWindow starts at 1180 × 760 logical pixels when space permits, with a 920 × 540 minimum. Main and resizable editor windows fit to the current monitor's working area after Windows display scaling, taskbar space, and native frame are accounted for. If a monitor is smaller than a requested minimum, the window's minimum is lowered to keep its controls on screen. This is a layout policy, not a claim about laptop market share.

S58_LaptopWorkingAreas renders main pages at 920 × 540, 1076 × 558 (1366 × 768 at 125% with a taskbar), and 1264 × 672 (1920 × 1080 at 150% with a taskbar). It captures the initial backup overview and the scrolled actions, checks matching page gutters and non-overlapping controls, and verifies four resizable dialogs with a 519-pixel client height. WindowPlacementTests separately checks scaling, native frame allowance, off-screen positions, negative-origin monitors and a screen smaller than the normal minimum. Its window-event tests preserve configured startup dimensions, fit moves between simulated monitors at the same scaling, leave ordinary dragging alone, and refit after restoring a maximized window. Screenshots show logical working areas; physical multi-monitor transitions remain an OS acceptance check.

S59_DropdownAffordance checks the painted dropdown arrow in both themes, opens the actual popup with a pointer click, selects choices, checks persisted settings, and exercises keyboard selection, Escape and disabled controls. S11_LogsViewPopulated checks live and saved output separately, including the first-ever run of a selected backup and the return to the empty state when an unfinished activity is removed. Screenshot comparisons serialize their file access so a directory scan cannot write the same diff image as a fresh capture.
