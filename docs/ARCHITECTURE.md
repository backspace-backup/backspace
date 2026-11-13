# ARCHITECTURE.md — code map for contributors

How Backspace is wired, where state lives, and the invariants a refactor
must preserve. Pair with [AGENTS.md](../AGENTS.md) for working conventions
and [GOTCHAS.md](GOTCHAS.md) for upstream-specific traps.

## High-level shape

```
Program.cs
 ├── --run <name> / --run-all  → RunUnattended(...)              [headless]
 ├── --prune / --check <name>  → RunUnattendedMaintenance(...)   [headless]
 ├── --migrate <path>         → RunMigrate(...)                 [one-shot]
 ├── --version / --help       → print and exit
 ├── --background             → RunGui(...), tray only          [sign-in start]
 └── no arguments            → RunGui(...)                     [Avalonia]

Avalonia GUI
 ├── MainWindow
 │    ├── Sidebar nav → Backups | Destinations | Restore | Logs | Settings
 │    └── Content host swaps view based on NavItem (see MainWindowViewModel)
 └── Modal dialogs: OnboardingWindow, BackupEditorWindow,
     DestinationEditorWindow, DestructiveConfirmWindow,
     ChangeStoragePasswordWindow, GeneratedPasswordWindow,
     SourceTreePickerWindow, ErasingProgressWindow

Services (singletons, accessed via ServiceLocator)
 └── src/Backspace/Services/  — backup, restore, persistence and integrations.
                                Read each service's contract and tests.
                                Platform-specific code lives under
                                Services/Platform/{Windows,Unix}/.
```

The headless code paths (`--run`, `--run-all`, `--prune`, `--check`) must work **without** the
GUI loaded — no `App.Current` references, no Avalonia dispatcher
assumptions. `ServiceLocator.InitializeCore` brings up only the services
a backup needs.

## Local engine access

Backspace runs restic (and, for cloud destinations, rclone) as its own child
processes: no daemon, no local port, no sign-in, no engine settings file. The
repository password reaches restic only in its environment while it runs
(below).

## Backups, checks and restores run restic directly (U175)

Backups, and the cleanup that follows them, run the bundled restic
(`ResticEngine.Direct.cs`), whether started from the window, by the
schedule or by the background service's worker. So do checks (entries of their own since U179, run after the backup or on
their schedule through `RunCheckOnlyAsync`), the stand-alone cleanup and the
erase tools (`ResticEngine.Maintenance.cs`), snapshot lists, folder
listings, the mounted drive's reads and every restore
(`ResticEngine.cs`, below). Each restic command goes through `ResticRunner`:

- The password reaches restic only in its environment (`RESTIC_PASSWORD`);
  inherited `RESTIC_PASSWORD_FILE`, `RESTIC_PASSWORD_COMMAND` and
  `RESTIC_REPOSITORY_FILE` are removed so no other source can win. The
  bundled engine folder comes first on `PATH`, for rclone.
- The command lines: `backup --json <folder>`, the repository's options,
  `-o sftp.args=-oBatchMode=yes`, `--exclude-caches`, `--tag plan:<id>`,
  `--tag created-by:backspace`, `--parent <id>`, then the backup's own options
  and its exclude file. `DirectBackupTests` checks them option for option.
- restic's JSON messages (`status`, `summary`, `error`, `verbose_status`, and
  `exit_error` on its error output) drive the live progress, the recap and the
  run summary, which says how it ended (`Backup result: STATUS_…`, read by
  the history and `WasStoppedOnRequest`). Exit codes: 0 done; 3 a backup saved
  without some files restic could not read (a warning); 10 no repository;
  11 locked: one `restic unlock` (stale locks only, never `--remove-all`) and
  one retry; 12 wrong password (final, in restic's words); 130 stopped.
- A line over 128 KiB is cut, and the error text a summary keeps is bounded
  (1,000 characters). The transcript gets `[restic] <time>\t<level>\t<message>`
  lines: each command line (the password is never on it), restic's messages,
  how it ended.
- Each restic joins Backspace's kill-on-close job (it ends with Backspace) and
  a job of its own (`ProcessTreeJob`). Stopping first sends Ctrl+Break to
  restic's own process group (restic removes its lock and stops, exit code
  130), waits `ResticRunner.GentleStopWait` (3 s) for it to exit, then ends
  the whole job, rclone included, and waits for the exit. A program without
  a console of its own (the window, `--run`, the service's worker) attaches
  to restic's console to send it (`ConsoleBreak`). On macOS and Linux the
  gentle step is SIGINT (the system's `kill -INT`), then the process tree.
  Force quit, of the window or of a scheduled `--run`, ends every restic at
  once (`ResticRunner.EndAll`).
- A repository this computer has not used yet is opened with
  `restic cat --no-lock config` (no lock: a lock left by a stopped run must reach the
  backup's own stale-lock recovery, U188), and created with `restic init` only when restic says
  there is none (exit code 10). The repositories this computer knows are in
  repositories.json in its engine folder (`KnownRepositories`), so later
  backups skip that step.
- The parent: the snapshot each plan's last finished backup made is remembered
  (`MaintenanceRecords`). Before the next backup, restic reads that one
  snapshot (`snapshots --json --no-lock <id>`: no lock, no list); it is the
  parent when it is there, carries `plan:<id>` and `created-by:backspace`,
  holds exactly this folder and was made on this computer. Otherwise (none
  remembered, the previous backup did not finish, restic could not read it, or
  it is another backup's) the plan's snapshots are listed
  (`snapshots --json --tag plan:<id>,created-by:backspace`) and the latest of
  this folder is taken.
- The cleanup after a backup is `forget --tag plan:<id>,created-by:backspace
  <keep options> --prune`, or `forget` then a full `prune` when the plan's
  previous backup did not finish or the repository has had no full prune for
  7 days. It takes the repository alone, as before (U156). The engine runs no
  check inside a backup (U179).
- In one process, backups to one destination still run one after the other
  (a gate per repository, which checks, cleanups and the erase tools take
  too); backups to different destinations run at once. Reads (lists,
  folders, a file's bytes) take no gate and no lock.
- Snapshot lists are `snapshots --json --no-lock` with the plan's two labels;
  no repository at the address yet is no snapshot (looking creates nothing),
  but a local destination whose folder is missing is an error. File lists and
  the mounted drive's tree are `ls --json --no-lock --recursive`, one folder
  `ls --json --no-lock` of that folder, one file `dump --no-lock`; a restore
  is `restore <id>:/<source> --target <staging>`, its selection in an include
  file (none for the whole source). The snapshot and paths follow `--`.

The file maintenance.json in the configuration folder holds, per repository, `lastFullPruneUtc`, per plan
`lastBackupStartedUtc`, `lastBackupFinishedUtc` and `lastSnapshotId`. The app
writes `Backspace.config\maintenance.json`, the service
`Backspace.config\service\maintenance.json`, and both read both. Nothing was
converted: a repository without a date gets a full prune at its next cleanup.

## Windows snapshot protection

New backups enable Windows snapshots (VSS) by default. The desktop stays at
normal privilege so Explorer can see its mounted drives. VSS comes from
administrator access, a one-time approval that installs the background
service.

**The background service (version 3, U100, 2 November 2026).** One Windows
service per Windows account, named `Backspace-<12 hex of the owner SID's
SHA-256>`, runs as LocalSystem with the command `"<copy>\Backspace.exe"
--system-service <name>` (`SystemService`). Nothing changes any file or
folder permission. Its record (version 3, owner SID, the owner's engine
folder, whether it is set up) lives in
`HKLM\SYSTEM\CurrentControlSet\Services\<name>\Parameters`, which only
administrators can change; the installation is the folder of the registered
command. Its schedule state, run status, worker logs and history are in
`Backspace.config\service`, an ordinary folder; its engine working files and
its copy of the vault key are in the owner's `%LOCALAPPDATA%\Backspace`
(U153). The service's security descriptor lets the owner query, start and stop it.

`SystemBackupService` reads the owner's `config.json` as saved, when it
changes (a folder watch) and before each run (`SystemServiceRuns`): there is no approval record and no
settings task. Whoever can change those settings can already replace
`Backspace.exe`, which the service runs as Windows; the user guide says so.
With the service set up it runs every backup; nothing is left to the tray
app or the window (U171). Settings carry no engine arguments: the old
free-text field, never passed to restic, was removed rather than handed to a
service running as Windows. Two questions are kept apart (U171):
`SystemServiceRuns.CanRun` says whether the service is able to run a backup,
`RunsOnItsOwn` whether its schedule starts it (automatic backups on). An
owner request (`start-cli`: Run now, Save and run, a named command) runs any
backup the service can run, automatic backups on or off, so every run gets
the same snapshot; the schedule plans, and `Authorize` without an owner
request accepts, only backups with automatic backups on. The service enables the backup and
restore rights of its token before starting workers, so restic reads every
selected file whatever its permissions; workers inherit the enabled rights.
It sets `BACKSPACE_ENGINE_ROOT` to the owner's `%LOCALAPPDATA%\Backspace\bin`
and `BACKSPACE_MACHINE_ROOT` to the folder above it.
Window and service share operation locks in the owner's
`%LOCALAPPDATA%\Backspace\locks` (`SystemServiceLocks`), outside any synced
folder.

Setup is one approval: `--system-service-setup` creates or repoints the
service and starts it; the desktop then sends it this computer's vault key
(`ServiceVaultUnlock`, `unlock` on the runs pipe), and the service, once the
key opens `vault.bin`, records that it is set up itself
(`SystemService.MarkActivated`). Both read and write the same `vault.bin`;
nothing is copied (see "Saved passwords" under Storage locations). Pipes are
owned by `S-1-5-18` and open only to the owner; clients verify the owner of
the pipe object. Removal needs one approval (`--system-service-remove`) and
deletes the service's key copies; the history stays readable from
`Backspace.config\service`.

Quit stops the service without a prompt (`SystemServiceLifetime`): the desktop
records its sign-in session and boot in `service\quit.json`, then stops it.
The service starts again at a restart (automatic, delayed start), when the
owner opens Backspace or signs in (the sign-in start resumes it), and after
"Shut down" with Fast Startup, which resumes services as they were, through a
start trigger (`SystemServiceTriggers`). On every start the service keeps the
Quit while the same sign-in session exists in the same boot, which covers
waking from sleep or hibernation, and otherwise deletes it. Updates stop the
service, replace `Backspace.exe` like any portable copy and start it again
(`WindowsUpdateRuntime.ApplyPendingAfterExit`).

The owner's command line (`--run`, `--run-all`, `--check`, `--prune`) is an
explicit request, so it runs after Quit too (U180): `ServiceScheduledRun`
starts the service with `SystemServiceLifetime.ForCommand`. While the Quit
holds, the service then starts in command-only mode: no schedule, the runs pipe
only, and it stops again once nothing has run for 20 seconds (two minutes if no
run came). The Quit record stays. If the owner opens Backspace meanwhile, the
Quit ends and the schedule starts. A start without that argument (Windows,
after Quit) still stays stopped. When the service can't start, the command
prints `ServiceCommandLineRun.Failure`: which command, why, and what to do.

**One copy per Windows account** (`InstallationHandover`): a GUI started from a
folder other than the one the service or the sign-in entry uses offers to
update that copy (stop its service, replace its `Backspace.exe`, open it), to
move here (a helper waits for the window to close, moves `Backspace.config`,
deletes the other program files, repoints the sign-in entry and, with one
approval, the service), or to cancel. **Remove Backspace from this PC**
(`WindowsPcRemoval`) turns access off, removes the sign-in entry, the Explorer
verb and older versions' tasks, optionally uninstalls WinFsp, and a helper
deletes `%LOCALAPPDATA%\Backspace` after the app exits.

`DesktopBackupRun` sends Run now and Save and run to the service through the
owner's `start-cli` request, automatic backups on or off; the card follows
the run through the service's activity. The desktop runs a backup itself only
when no service is set up. With one, a backup the service can't run (not
saved yet, no folders, a missing destination) or settings it can't read fail
the run with the reason (`ServiceCommandLineRun.Refusal`), never a run in
the window. Network shares run in the service: before the run the worker
connects to the share's network path with the destination's saved user name
and password (`ServiceNetworkShares`, WNetAddConnection2) and disconnects
after. A share saved as a drive letter or without them fails the run saying
what to enter; with the service set up, the destination editor requires
them, Run now opens the destination when they are missing
(`DestinationReadiness` with `serviceRuns`), and Settings → Administrator
access names such shares (`NetworkSharePath.ServiceSignInNotice`).
External drives run in the service; an unplugged drive is
left out of the run. `VssElevationPolicy.PromptAllowed` is off, so no run asks
Windows for approval: a run that cannot use VSS backs up without it and
records a warning naming the fix.

Disabling snapshots requires an explicit acknowledgement in the advanced
backup editor. Older configurations with snapshots off and no acknowledgement
are blocked before repository work. The sidebar shows Limited protection
while any backup has snapshots disabled, with an explanation and an action to
enable them. NAS sources and filesystems unsupported by VSS can legitimately
need the disabled setting. A VSS-enabled run can still warn or fail if Windows
cannot create a snapshot; configuration is not a guarantee of successful capture.

## Checks

`Backup.Checks` is a list of `BackupCheck` entries (U179): one entry is one
check, with a `CheckKind` (`Structure`, `DataSample`, `TestRestore`,
`FullDataRead`), its own `Schedule` (null = after each backup that reached the
destination; `ScheduleFrequency.Yearly` exists for checks) with its own
battery, network and missed-run conditions, and kind settings
(`MaximumBytes`, `SamplePercent`, `CompareOriginals`). Cleanup stays
`Backup.PruneSchedule`. `BackupMaintenanceDefaults` gives new backups a weekly
cleanup, structure check and 1 GiB data sample, and a monthly 1% / 1 GiB test
restore, at 3 AM; Easy mode shows them as `BackupCheck.Sentence`.

Settings saved before U179 hold one check (`checkSchedule`,
`checkReadDataSample`, `checkRestoreFiles`, `restoreCheckOptions`). They still
load in the app and in the service's strict reader (`Unmapped` disallowed):
`Backup.OnDeserialized` converts them with `BackupCheck.FromLegacy` when the
JSON has no `checks`, and they are never written again. A data sample replaces
the structure check rather than adding to it, since restic's
`--read-data-subset` check includes the structure check.

`SchedulePlan.From` makes one plan per scheduled check, keyed
`<backup>/Check/<check id>`, in run order (structure, data sample, full read,
test restore). The service passes the check's id to its worker
(`BACKSPACE_CHECK_ID`); the command line's `--check NAME` runs every check of
the backup in that order. `BackupOrchestrator.RunCheckAsync(backup, check)`
queues checks of one backup behind each other in the process, the service runs
one operation per backup at a time, and every check holds its destination
exclusively (`RepositoryAccess`), so checks due together run one after another
and backups there wait. Checks after each backup, and scheduled checks that
waited for a destination (U174, per entry through `RunRecord.CheckId`), run
right after the backup in `RunChecksAfterBackupAsync`.

The engine gets one check at a time through `EngineViewFor`: a copy with
`CheckReadDataSample` (data read or not) and `DataCheckLimitBytes` (the data
sample's limit, `long.MaxValue` for a full read). `Backup.CheckSchedule` is no
longer saved, and the engine no longer reads it: it runs no check inside a
backup (U175 step 1). The engine reports `IBackupEngine.HonoursDataCheckLimit`:
structure checks use `restic check`; the data sample runs
`stats --mode raw-data --json`, then `check --read-data-subset=<bytes>B` for 1%
of the stored data up to the entry's limit (1 GiB when the copy has none); a
full data read runs `check --read-data`, recorded as `CheckDepth.AllData`
(`DataCheckLimitTests`). Checks run restic directly (U175 step 2). An invalid
or unavailable size fails the check; it never falls back to an unlimited
download.

A check's progress (U198): its restic commands run with
`RESTIC_PROGRESS_FPS=1`, and `CheckProgressReader` turns restic's plain lines
("load indexes", "[0:05] 33.33%  1 / 3 packs") into a step and a share done,
reported through `RunCheckOnlyAsync`'s `progress` callback to
`BackupProgressService.ReportActivityStep` (step, destination, percent), and from
the service through its activity report (`ActivityStep`, `ActivityWhere`,
`ActivityPercent`). A sample test restore's progress is read from its session
once a second. The run's log keeps only restic's finished counts (100%).

Each check and cleanup keeps its own log (U233): `MaintenanceRunLog` opens a
run log (the cell log's header with an `operation` field naming the check,
and its outcome block) in the backup's log folder and sets `RunRecord.LogPath`.
Everything the run says goes through one sink to the live view and the file:
a section line named after the check (`Backup.CheckTitle`, set on the
engine's copy), progress lines from `MaintenanceRunLog.ProgressLines` (a line
per step and per further tenth), what it met, then `ResultLine`. A restore
check also takes the engine's lines while it runs and writes every file checked
as detail lines before its result. A lock restic reports (exit code 11) that
Backspace removes and retries is logged as a warning
(`ResticCommand.LockIsRetried`). In Activity & logs, a restore check's own
`TestRestore` record is folded into its check (`WithoutFoldedRestoreRecords`).

A run the owner starts (Run now, Verify) whose conditions aren't met asks
first (`ManualRunConditions`, U226): queue it on the card until they are, or
run it anyway. A queued run shows Run now anyway on its card (Run now on it
does the same); it is never queued twice. "Anyway" sets
`BackupOrchestrator.ConditionsWaived` (`WaivedConditions`: battery, metered)
on the run's flow, passed to the service as `ServiceHostRequest.WaiveConditions`
and `WaiveBattery` (accepted for `start-cli` only) and to its worker as
`BACKSPACE_WAIVE_CONDITIONS` ("battery,metered"); no destination then waits
for, or is stopped by, a metered connection. Each waived condition is written
in the run's log and `RunRecord.Notes` ("Started on battery at your
request."). A Run now started with its conditions met stops on battery and
runs again once plugged in. After a metered pause only the parts left run
again (`ServiceHostRequest.OnlyTargets`, `ScheduledRuns.ForTargets`); the
orchestrator keeps that limit (`Backup.OnlyTargets`) when it reloads the
settings at admission.

This is an approximate sampling target, not a network quota. Restic converts
the byte target to a fraction of pack count, with a minimum of one pack.
Unequal packs, metadata, compression and retries affect traffic. A check
covers the repository once per destination, while retention remains per
source plan. `CheckRunWording` writes the run's summary in plain words
("Data sample (1 GiB) passed in 7 min 50 s"); a sample never establishes full
data coverage.

## Test restore ownership and recovery

`TestRestoreService` owns manual and optional automatic sample/full checks independently
of backup cards and navigation. `BackupVerifier` uses the production restore
service for bounded batches and checks recovered file sizes. Restic validates
the saved data it reads. `TestRestoreOptions.CompareOriginals` defaults to false;
when explicitly enabled, the verifier also compares recovered bytes with
originals that have the recorded size and modification date. Those properties
are an eligibility heuristic; the comparison reads the actual bytes and does
not establish which copy changed on a mismatch. Each check appears as one session in `ActiveRestoreRegistry`;
individual temporary batches do not create separate UI sessions.

Without the optional comparison, the durable check does not enumerate or read
current originals. `Recovered` distinguishes this result from a skipped requested
comparison and from a confirmed byte match. A requested comparison difference
is reported separately in the summary: successful recovery does not establish
which copy changed. Existing persisted enum values retain their numeric values.

A `TestRestore` check entry runs `TestRestoreService.RunScheduledAsync` with
the entry's options, at the destinations it can reach, each held
exclusively meanwhile. Manual and automatic checks use the same
`RestoreCheckOptions` view and `TestRestoreService` journal/ownership path.
`TestRestoreContext.SelectSample` never exceeds the limit: a file larger than
what is left of it is passed over, and when no file fits nothing is picked and
the check fails saying so (U179: a 1 GB limit had picked a 1.85 GB file).
The automatic coordinator passes its existing maintenance read lease to the
verifier instead of trying to acquire a nested lease while a writer waits.
Cancellation and the metered-network watchdog cover the extra restore work.
A comparison failure makes the check fail; it never turns a successful copy
into a claim that recovery was tested.

Each `RunRecord` retains structured `BackupCheckResult` observations, including
the depth actually used. `BackupCheckHistory` combines repository checks and
restore comparisons for the card and history window. A shallower success cannot
clear an earlier deeper failure. A scheduled check's due time uses its cadence;
manual restore checks do not substitute for a repository check. Old records
without depth metadata are labelled as such rather than interpreted using
today's preferences. The card refreshes elapsed times once a minute.

`LogStore` retains compact dated outcomes in each backup log folder's
`timeline.json`, alongside the limited detailed history and transcripts.
Both files use the existing history transaction lock. The timeline is written
first and deduplicated by run ID; expiration of a log does not erase a check
failure or its date. History lookup uses immutable backup IDs across renames.
If the timeline file is temporarily unavailable, detailed history keeps all
pending results and transcripts. The next append retries the archive before
allowing retention to remove anything.
`RunHistoryFile` flushes new history before giving it a recoverable pending
name. An interrupted serialization is therefore never read as a complete
result. If Windows refuses publication, the pending list and any previous
copy survive; readers merge complete lists by run ID, and the next append
reconciles them before retention. Temporary replacement errors receive at
most five retries (1.55 seconds total); an uncertain partial rename is not
retried. Permanent failures remain visible.
Only previously available history can be migrated; already-expired records
cannot be recovered from this data.

`TestRestoreJournal` saves immutable snapshot IDs, the selected file list
and completed batch results under `test-restores/`. GUI startup resumes
unfinished checks against those exact snapshots. It refuses changed source
or destination routes instead of silently selecting a new backup. A file
lease prevents two application instances from owning the same backup's
test restore. Cancel first interrupts the work and any blocked progress save,
then saves a stopped result before ending the session. That result prevents
automatic resumption. If the user also stops waiting for the result to save,
the app reports that the older running record may resume on next launch.
Pausing saves a separate state; startup loads it without downloading
until Resume. The first durable terminal result wins over a late completion or
cancellation. Completed results remain available for 30 days across app
launches unless dismissed. Unreadable saved progress is preserved and
reported in Restores. On Windows, replacing the saved progress record retries
access-denied or sharing-lock errors until access returns or the user stops
waiting. The previous record stays in place until atomic replacement succeeds.
Progress saves, pause changes and dismissals run off the UI thread. Cancelling
a pending save cannot be reported as a successful save.

`RestoreControl` stops and awaits the owned restic child before acknowledging
Pause. Resume reuses the same staging folder and immutable snapshot; the
repository read lease remains held throughout. Every restore uses this
owned process (U175). Ordinary file restores resume only within the current
application session. Test restore saves its plan and completed batches across launches.

`IBackupEngine.RestoreFilesAsync` reports the `.backspace-restore-*` staging
folder through `onStaging` once it exists (the restore creates it before
restic starts) and reports `null` after cleanup. `RestoreEngine` raises `StagingChanged`
for the wizard and sets `ActiveRestoreSession.StagingPath`, which the progress
card shows with **Open folder** while the session runs (U154). Test restores
run without a registry session for their batches, so they never show it.

`RestoreOutputTransaction` keeps replaced originals until all selected sources
finish. Cancellation rolls back those publications, including earlier sources;
their registry sessions do not report completion before that finalization.
Rollback checks file contents before removing output. A concurrent user edit
is preserved alongside recovery copies and reported as an error. Each original
move has a flushed path map under `.backspace-undo-*`; a crash can leave that
folder for manual recovery. This is not automatic crash recovery. The
path map is checked to be the real file before anything is written to it
(U152). Backup folders and the `.backspace-undo-*` folder are not held open
while the engine works (removed 7 November 2026, U152): restic only creates or
deletes files named with its own 64-character IDs, so a backup folder swapped
for a link can't make it touch other files, and restoring into a folder other
accounts can change is the owner's choice. Verified
temporary batches release their publication records to keep memory bounded.
Placing verified files also retries brief Windows sharing locks. Each file
move rechecks its destination parent and refuses to overwrite a file that
appeared during the wait. Cancellation follows the same original-file recovery
path; it does not restart the whole transaction or move an original twice.
Rollback moves retry a brief sharing lock (up to about 1.5 seconds), because
a security scanner often opens an original right after it enters the recovery
folder. A longer lock keeps the recovery copy and reports its location.

On Windows, cleanup of owned restore staging and Test restore folders retries
sharing locks and transient nonempty-directory errors until access returns or
the user stops waiting. Stopping cleanup leaves the remaining temporary files
and reports their location; it cannot report successful cleanup. Staging is
deleted by `SafeTreeDelete`: each folder is opened without following a link,
checked, and held so it can be neither renamed nor replaced while its contents
go; a link inside is removed itself, never its target. The top folder must
really be where its name says (a folder above it swapped for a link shows as
another place), except on a network drive. A restore target other accounts
can change cannot steer the deletion elsewhere (U152). The restore folder is
not held open during the restore (removed 7 November 2026): restoring into a
folder other accounts can change is the owner's choice. Publishing and cleanup
never follow a link. Restored items
are published, and rolled back, by `HandleRename`: the item and the folder it
goes into are opened without following a link, both must really be inside the
restore folder as it was when publishing began, and the item is renamed
relative to the open folder; nothing is copied across drives. A folder swapped
for a link can then neither carry the owner's files out of their profile nor
put a planted file into it. Snapshot listings leave out links: restic's
listing doesn't say where a link leads, and creating one to another computer
(`\\server\share`) makes Windows sign in there, so none is offered for a test
restore, the mounted drive or a single-file restore; restoring its folder still
brings it back. A mounted snapshot
grants read access to the account that mounted it and to SYSTEM only, since
its volume is reachable from every account through `\\?\GLOBALROOT`.

`FileAccessRetry` also handles the scheduled-work pause saved when quitting or
reopening, final placement of restored files and configuration writes during
engine startup. After about five
seconds, `FileWaitPanel` explains the wait, offers Keep waiting, and provides
an operation-specific stop action with its consequences. It continues trying
automatically; five seconds is a notice delay, not a failure deadline. An access
denied error can mean permissions as well as a lock, so the message explains
both possibilities. Invalid data, full disks and directories occupying file
paths do not receive this retry treatment. Background runs log the wait and
respect operation cancellation; the panel belongs to the running desktop app.

The pause-state transaction releases its inter-process lock between retries.
Waiting to save Quit must not block a scheduled process's state monitor and
accidentally cancel it before the pause has actually been saved.
Starting Quit also cancels any pending startup resume, so a delayed reopen
save cannot clear the newer Quit pause.

## Windows release updates

`WindowsUpdateViewModel` owns the Settings update controls and a one-shot timer for the chosen check frequency. Checks use GitHub's latest stable release endpoint; downloads and restarts require a click. The release body is shown under "What changed" by `MarkdownView` (U169), which renders the Markdown subset release notes use (`MarkdownLite`: headings, bullets, bold, inline code, links); it interprets no HTML, loads no image and opens only https links. `WindowsUpdatePackage` verifies the official asset URL, exact size, GitHub SHA-256 digest, safe archive entries and application version before preparing a complete bundle under `Backspace.config/updates`. Nothing but `Backspace.exe` and `Backspace.config` is ever written beside the executable (U218): the runtime gate is a set of named mutexes (`RuntimeGate`), which Windows releases when a process ends, even after a crash or power loss, so it never leaves a file or a stale lock; each update keeps its `Recover Backspace.cmd` in its own work folder, and the write check opens the folder for add/delete rights instead of writing a probe.

`WindowsUpdateRuntime` shares a process-lifetime gate with GUI, scheduled and helper runs: each owns one slot mutex, and an update owns the writer mutex and every slot. Replacement requires that exclusive gate after the GUI closes and owned engines stop. The application operation gate also prevents new work during a requested restart. Replacement journals the old and new program files and rolls back failures; configuration is outside that file list. A recovery launcher can restore the previous bundle. A successful process start is not proof of a healthy new GUI: native launch/crash behavior remains an acceptance-test requirement. The immediately preceding bundle is retained. Older completed update records can be removed after the next successful replacement; unknown or unfinished records are preserved.

Only portable Windows x64 single-file builds in non-linked folders support installation. Other builds keep the manual release-page action. The internal `--recover-update` path runs before engine/configuration initialization. Each update keeps a separate recovery executable in its work folder, so rollback never moves the file that is running recovery. The launcher waits for that process and preserves its exit code, including after the failure prompt. Recovery opens the restored app through the Windows shell to keep its output separate from the launcher's captured output; the launcher can then finish while the restored app stays open.

With the version-3 service an update is an ordinary portable update: the
service is stopped while `Backspace.exe` is replaced, then started again.

Removal of superseded update work retries only Windows sharing violations,
for a bounded 1.55 seconds. Each retry repeats containment and reparse checks.
Permanent failures retain the remaining files, and cleanup failure cannot turn
a successful application replacement into a failed update.

## File layout

```
Backspace/                          ← repo root (folder name may vary locally)
├── README.md, AGENTS.md, DESIGN.md, LICENSE, CONTRIBUTING.md, .gitignore
├── Backspace.sln                    — Visual Studio solution
├── docs/                            — markdown library (this file + siblings)
├── assets/brand/                    — master SVG + PNGs of the brand icon
├── scripts/                         — PS helpers (regenerate website shots, ...)
├── tools/build-icon.ps1             — generate the 26 .ico files from SVG
├── src/Backspace/                   — main project (Avalonia desktop app)
│   ├── Backspace.csproj             — multi-targets net10.0-windows + net10.0
│   ├── Program.cs                   — entry point, argv dispatch
│   ├── App.axaml(.cs)               — root application resources
│   ├── Models/                      — POCOs serialized to config.json
│   ├── Services/                    — non-UI logic (browse for class-by-class docs)
│   │   └── Platform/{Windows,Unix}/ — per-OS abstractions (IScheduler, ISecretsEncryption)
│   ├── ViewModels/                  — one per view + dialog, MVVM (CommunityToolkit.Mvvm)
│   ├── Views/                       — Avalonia .axaml + code-behind
│   ├── Themes/                      — Theme.axaml + Accent_*.axaml + Controls.axaml + Icons.axaml
│   ├── Assets/                      — embedded icons and fonts (Manrope for headings),
│   │                                  restic(.exe) + rclone(.exe)
│   │                                  (gitignored, fetched at build)
│   ├── FetchEngines.targets         — MSBuild target that downloads the engines on demand
│   └── BuildNativeHost.targets      — Windows-only: framework-dependent startup
├── src/Backspace.Host/              — native startup and .NET runtime setup
└── tests/Backspace.Tests/           — see TESTING.md for the layout + harness
```

## Storage locations (on the user's machine)

Everything is **portable**: the app keeps all state in one folder next to
the executable. Settings, history and credentials never live elsewhere; the
credentials travel in `vault.bin`, encrypted with the person's password
(U153). Reproducible caches and this computer's working files live in
`%LOCALAPPDATA%\Backspace`: `bin` (the engines, checked against the
fingerprints built into Backspace.exe and fetched again when missing),
`winfsp` (the WinFsp installer and library extracted from Backspace.exe),
`locks` (operation locks shared with
the background service), `keys` (this computer's copies of the vault key) and
`engine\<16 hex>` (the engine's working files, below). All are recreated when
missing. Optional system changes are listed in the user guide: the background
service (administrator access), the sign-in start entry, and the WinFsp driver.

- `<exe-dir>/Backspace.config/config.json` — app settings, backups, destinations
- `<exe-dir>/Backspace.config/vault.bin` — the saved credentials (storage passwords, cloud sign-ins, API keys), one file for the app and the background service. See "Saved passwords (the vault)" below.
- `<exe-dir>/Backspace.config/pending-removals.json` — removed backups whose stored copies are still to be erased; absent when nothing is waiting
- `<exe-dir>/Backspace.config/maintenance.json` (and the service's `Backspace.config/service/maintenance.json`) — Backspace's own maintenance records (`MaintenanceRecords`, U175): each repository's last full prune, each plan's last backup start and finish and the snapshot it made (the next backup's parent once restic confirms it). See "Backups run restic directly".
- `%LOCALAPPDATA%/Backspace/engine/<16 hex of SHA-256 of the upper-case configuration folder>/` — this computer's engine working files (`EngineWorkPaths`): `rclone-remotes/` (one generated remote per cloud destination, with its token), `rclone.conf`, `filters/<plan id>.txt` (each plan's exclude file: the backup's Filters text, written unchanged before every use of the plan, U151), repositories.json (the repositories this computer has opened or created for a backup, so the next backup skips `restic cat config`, U175), `mount-cache/<backup id>/` (the mounted drive's copies of files read so far), `verify/` (a test restore's temporary recovered files, U217: never in the synced folder, where Dropbox locked them) and, for the background service, `scratch/`. Never in the synced folder; rebuilt from the settings and the vault when missing. The service's folder is named after `Backspace.config\service`. `BACKSPACE_MACHINE_ROOT` overrides `%LOCALAPPDATA%\Backspace` (tests; the service sets it to the owner's folder); a test configuration outside an executable's folder without it uses `<config>\engine`.
- `<exe-dir>/Backspace.config/test-restores/` — saved test-restore plans, completed batch results and sample history. Temporary recovered files live in this computer's engine folder (`verify/`, below), not here.
- `<exe-dir>/Backspace.config/logs/app/` — Serilog daily-rolling Backspace logs
- `<exe-dir>/Backspace.config/logs/engine/<backup-name>/` — per-run engine transcripts (restic's output, written by `ResticRunner`). Each engine line starts with the UTC time it was written (`LogLineStamp`, `2026-11-10T06:00:00.569Z [setup] ...`), and `[section] <backup> · backup|check|cleanup` lines mark where each part starts (`LogSection`, U176). Activity & logs parses them with `ConsoleLogParser` and `ConsoleLineClassifier` into coloured rows of a virtualizing list; lines without a stamp (older logs, restore logs) show no time.
- `%LOCALAPPDATA%/Backspace/change-lists/` — outside the portable folder, a reproducible cache: per backup cell, the source's file list (path hash, size, last write) at its last successful backup and the pace it measured, compressed. `ChangePlan` compares the source with it at the next start to know what restic will read (U144); when missing or made for another snapshot, progress falls back to restic's own totals. A configuration outside the copy's folder (tests) keeps it under its own `change-lists/`.
- The actual restic repositories live wherever the user pointed each
  destination (a local folder, an external drive, a cloud bucket via
  rclone). We don't reach into them.

The missing-executable helper, its last-executable-path record and warning
flag also live inside Backspace.config. The extracted WinFsp managed wrapper
and bundled MSI live in that folder's winfsp subdirectory. Missing or stale
embedded dependencies are recreated; equal file length alone is not accepted
as proof that a cached file is current. These paths are
per portable copy; opening one copy cannot consume another copy's warning.
If the entire portable folder moves, its sign-in start entry points at the
old location until Backspace is opened from the new one, which rewrites it.

Portable files do not eliminate Windows integration: the sign-in start
entry, the service registration, Explorer integration and the optional WinFsp
driver use Windows-managed registrations. Mounting a snapshot as a drive
requires that driver to be installed with administrator approval. Normal
restore does not require it. The service points at the portable executable
wherever it is and changes no permission (see Windows snapshot protection).
Records written while another process briefly reads them are replaced with
retries (`FileAccessRetry.Run`, U094, U135). Removing a
backup that never ran, from a destination never set up, erases nothing there
(`BackupRemoval.NothingStored`).No separate application
installation or external persistent-data folder is authorized. Reinstallable
system dependencies and reproducible caches may live outside the portable
folder, provided missing dependencies are detected and can be recreated on a
new computer. That exception does not include unique settings or credentials.

The `BACKSPACE_CONFIG_ROOT` env var overrides the location — used by the
test harness to give each test its own isolated config dir.

### Saved passwords (the vault, U153)

`SecretsStore` keeps every credential in `Backspace.config\vault.bin`
(`VaultFile`): the marker `BSVAULT1`, a 4-byte header length, a JSON header
`{v, id, kdf:{alg:"pbkdf2-sha256", iter, salt}, noPassword, wrap:{nonce, ct, tag}}`
and a body of a 12-byte nonce, the AES-256-GCM ciphertext of the JSON
credential map and its 16-byte tag. A random 32-byte vault key encrypts the
body (associated data `BSVAULT1|body|<id>|v1`); the header wraps that key with
a key derived by PBKDF2-HMAC-SHA256 (600,000 iterations, 16-byte salt, the
password in NFC as UTF-8; associated data binds the id, the KDF settings and
the no-password flag). An empty password is allowed: the file then says
`noPassword` and its contents are only obscured; it opens on any computer
without a question. Changing the password rewrites only the header, so other
computers' key copies keep working. Headers asking for fewer than 100,000
iterations, a newer format, a damaged body or a missing file with a `.bak`
beside it are refused and kept as they are, never overwritten; the only
replacement is the explicit "start over" (the file is kept as
`vault.bin.unreadable-*`).

Each computer keeps a copy of the vault key, protected by Windows for the
account (DPAPI), in `%LOCALAPPDATA%\Backspace\keys\<vault id>.user.bin`
(`VaultKeyCache`); the background service keeps its own, protected for
Windows' own account, as `<vault id>.service.bin` in the owner's folder.
States (`VaultState`): `NoVault` (nothing saved yet), `Unlocked`, `Locked` (no
usable key copy here; a key copy that no longer opens the body is stale and
deleted) and `Unreadable`. `Get` while locked throws `VaultLockedException`,
so readiness checks and runs say "locked", not "missing"; `Set`/`Remove`/
`PutNew` need `Unlocked`. Every read-modify-write holds the `StateFileLock`
and reloads inside it; writes are atomic with a `.bak`; another program
holding the file is waited out without a limit. `SetIfNewer` compares and
replaces in one transaction (rclone's refreshed tokens,
`RcloneConfigBuilder.ImportRefreshedToken`).

The store never asks. `VaultGate` asks through `IVaultPrompt`
(`VaultPasswordWindow`; tests script it) only when needed: before a
credential is saved (destination Save, the change-storage-password dialog,
cloud Connect, the Healthchecks key in the backup editor, Settings keys and
client secrets) and before an action reads one (Run now, Test restore,
restore, browse, mount, locating a moved repository). Without a vault the
first save asks to choose a password; a cancelled question saves nothing.
The test email keeps typed keys in memory. At start a locked vault shows a
banner on the Backups page (`VaultBannerViewModel`), never a dialog
(`VaultStartup`); desktop scheduled runs wait with one alert
(`DesktopSchedule.VaultBlocked`). Settings → Saved passwords shows the state
and changes the password.

**The background service** opens the same `vault.bin` with its key copy
(`ServiceVaultUnlock`). The app sends the key over the owner's authenticated
runs pipe (`unlock`) when it sets the service up, after creating or unlocking
the vault, and at start; the service keeps it only when it opens the vault,
then counts as set up (`SystemService.MarkActivated`). While it has no key,
its runs fail before any engine starts with "Open Backspace on this computer
and enter your password once."

Backspace does not read the per-computer credential files used before the
vault (`secrets.bin`, and the service's copy in `service\`): an install with
only those files has no vault, and the first save asks for a password.

## Design principles (stick to these)

1. **restic owns the repository; Backspace keeps the rest.** Every
   operation runs the bundled restic directly (U175, "Backups, checks and
   restores run restic directly" above); no other program holds Backspace's
   settings or passwords. What Backspace needs to know about a repository
   beyond what restic answers is in its own records (maintenance.json,
   repositories.json). Each restore is one owned restic process: it streams
   bounded progress and retains the caller's repository lease until exit is
   confirmed. Password changes run restic too. Tree listings use one recursive
   request for local and cloud repositories, with the destination's own rclone
   environment; invalid or incomplete output gives no tree, and the mounted
   drive then lists one folder at a time. A bounded, in-memory cache remembers
   immutable snapshot membership for the current repository address and plan.
   It avoids a second repository listing before each read. Complete file metadata is also
   retained for at most eight snapshots and 65,536 nodes in total, so reopening
   a small tree does not start another engine process. These caches do not hold
   file contents or prove that a snapshot still exists. Refreshing the snapshot list always
   reads the repository. A deleted selection must fail, never select newer data.
   Cached metadata reads do not run a provider connection probe. Real repository
   reads, restores, backups and maintenance still check the cloud connection;
   initializing a new cloud repository also checks before creating it.
   Cleanup is a forget command with the configured retention and both the
   backup's plan and instance tags, restic run directly after a backup or on
   the cleanup schedule. That forget command is the only thing that deletes
   snapshots. Its options
   come from `KeepPolicyRules.cs`: Duplicacy `n:m` rules become `--keep-within`
   for the newest window and `--keep-within-daily|weekly|monthly|yearly` up to
   the next rule's age (U172). A policy that does not parse deletes nothing.
   A backup's previous snapshot (restic's `--parent`, its "[setup]" log line,
   the change plan, the changed-files list) is the snapshot the plan's last
   finished backup made once restic confirms it, otherwise the latest in the
   plan's snapshot list (U175).
   A worker the service's schedule starts gets `BACKSPACE_RUN_AUTOMATIC=1`, so
   its history says automatic; an owner request (`start-cli`) never does. A
   run's `QueuedFor` is left out of its duration.
   A standalone check skips a destination it can't reach (drive not
   connected, share offline) and marks that part `DestinationUnavailable`.
   `CheckOverdue.cs` reads the history: when a destination's latest check part
   has that mark, `RunBackupAsync` runs the check for that destination once,
   right after a backup whose parts there all succeeded (U174). No state is
   saved besides the history.
   Healthy tasks do not run an unconditional unlock first. A lock failure
   (restic's exit code 11) can try one normal stale-lock removal
   (`restic unlock`) and retry; unknown outcomes cannot trigger that recovery.
   Active locks are never force-removed.
   **Simultaneous backups (U156, renamed from "Backups at once" in U202).** `AppConfig.Limits` (`BackupLimits`, Settings ›
   Simultaneous backups) holds "per destination" (default 1) and "in total"
   (default 2), 1 to 8; the window, scheduled runs and the service's workers
   all read them from the saved settings. `RepositoryAccess` applies them to
   each part of a run (a source to one destination, in the cell loop), across
   processes, with `CrossProcessGate`s (session semaphores, or the service's
   shared lock files): eight slots per repository (the destination gate
   `MutexNameForDestination`, then `#1`…`#7`), eight total slots
   (`Local\Backspace-Backups-N`), a `#setup` gate and an `#exclusive` gate. A
   backup part takes a free slot below the per-destination limit, then a total
   slot below the total, then the setup gate until the engine has prepared the
   repository (`RepositoryAccess.SetupDone` after `EnsurePlanAndRepo`: two
   engines creating one new repository at once each wrote their own key and
   left it unreadable). Restic lets backups share a repository but needs it
   alone for forget, prune and check, so before inline cleanup the engine calls
   `RepositoryAccess.ExclusiveAsync`: the part gives up its total slot and the
   setup gate, takes the exclusive gate (new backups there wait behind it) and
   every slot. Stand-alone prune and check take the repository the same way;
   a part run by the elevated VSS helper, whose cleanup is out of reach, takes
   it alone from the start. Waiting holds nothing, or only the part's slot
   (waiting for a total slot), or its slot and total slot (waiting for the
   setup gate); slots, total slots and the setup gate are held only by parts at
   work, which let them go before waiting for cleanup, so no wait can close a
   cycle. While a part waits, the run record and the card say "Queued: …"
   with the holders' names, noted beside each gate
   (`CrossProcessGate.Holders`), or "N backups already running", and
   `RunQueuedReasonChanged` carries it to the card or the service worker's
   activity. `BackupOrchestrator.QueuedTime` keeps the time spent queued, so
   "Running · N min" leaves it out. The engine also reports a part it holds
   back (`RepositoryAccess.Waiting`) behind its in-process one-task-per-
   repository gate. Backups run restic directly (U175), so in one process,
   such as the window, two allowed backups to different destinations run
   together (E64 part 3).
   Windows makes one VSS snapshot set at a time, so two backups starting
   together could leave the second without a snapshot: a backup using VSS
   takes `VssSnapshotGate` (one across all Backspace processes) before its
   engine call and keeps it until restic reads its first files, the backup
   waits behind another task on its repository, or five minutes pass; restic also gets
   `-o vss.timeout=30m`, so its own retry on VSS_E_SNAPSHOT_SET_IN_PROGRESS
   waits instead of backing up without a snapshot after two minutes.
2. **All destructive actions are type-to-confirm with the single token
   "OK"** (case-insensitive). Erase-destination, prune-to-latest,
   remove-backup, remove-destination, and restore-overwrite all gate on
   the user typing OK into the destructive-confirm dialog. The subtitle
   copy is what tells the user what they're about to destroy — the
   gating word is intentionally the same across actions so muscle memory
   doesn't have to remember a per-action keyword. No "are you sure"
   button. Removing a backup (`BackupRemoval`, 29 October 2026, U029) also
   erases its snapshots at every destination through `StorageCleaner`, which
   never touches other backups' snapshots. Each destination is first queued
   in `Backspace.config/pending-removals.json` (outside `config.json`, so the
   service's approval digest is unaffected); a successful erase clears its
   entry. Unreachable destinations stay queued and are retried every two
   minutes while the desktop app is open, without interrupting running work.
   Removing a destination also erases its queued snapshots, then drops them.
   Once nothing for the backup is queued, `LogStore.ForgetBackup` deletes its
   run records and transcripts by backup ID across the folders of earlier
   names; other backups' records in a shared folder are kept. Run history kept
   by the background service is not touched by the desktop.
3. **Current scheduler lifecycle.** Backspace keeps its schedules itself
   (U043, 31 October 2026); nothing is registered with Windows Task Scheduler,
   launchd or systemd. `ScheduleLoop` (in `BackupScheduler.cs`) starts due
   plans together (`MaxParallel`; one at a time without it, as its tests run
   it): how many back up at once is up to the limits below (U156), and a plan
   never starts while its previous run goes on. `SchedulePlan.From` lists each backup and its optional check
   and prune cadence. `ScheduleState` saves each plan's next due time beside the
   settings (`schedule-state.json`), so a slot missed while the computer was off
   or Backspace was not running runs once afterwards (always, U226). New and
   changed schedules start from their next time; disabled and snoozed backups
   pass over their slots. The start is recorded before the run, so a crash or
   Quit never repeats a slot; slots that pass during a long run are skipped, so
   a plan never overlaps itself. Runs ending on their own threads save their
   outcome under the loop's lock, reading the state file again each time.
   A due plan waits while it is blocked (battery, a network
   it needs, Quit) and then runs once. Conditions (U226, `ScheduleConditions`):
   "Run only while on AC power" and "Run only while on an unmetered connection",
   each a wait and a stop, the backup's for its checks and cleanup unless a
   check has `OwnConditions`; the network ones only for runs whose destinations
   all need the network. A backup's network destinations that its run left
   waiting (`CellOutcome.WaitingFor`: unmetered, connection, reachable) are kept
   in the entry's `Pending` beside its regular time and run on their own
   (`SchedulePlan.OnlyTargets`, `ScheduledRuns`, the worker's
   `BACKSPACE_ONLY_TARGETS`) once not blocked; unreachable ones only after
   Windows reports a network change (`ScheduleLoop.NetworkChanged`) or a day.
   A mixed backup's metered watchdog stops only its network destination's cell. The execution time limit and "stop on
   battery" cancel a running plan. Between runs the loop sleeps until the next
   due time (U185). On Windows `ScheduleSignals` wakes it when the power source
   changes or the computer wakes from sleep (power notifications with a
   callback, no window needed, so in the service too), when the network or its
   cost changes, and when the settings file or the Quit state changes on disk;
   with those, the longest sleep is a day (`ScheduleConditions.LongestSleep`),
   a safety look for a missed notification. Unreadable settings are looked at
   again each minute. Elsewhere it still looks every minute (planned: event
   based on macOS and Linux).
   Who runs the loop: `DesktopSchedule` in the tray app, in process, through
   the orchestrator like Run now, unless the background service owns the
   schedule; then `SystemBackupService` runs it with the saved settings.
   Quit stops the tray app, so nothing runs until it starts again. The app
   starts at sign-in through the person's own entry (`SignInStart`: the
   HKCU Run value on Windows, a LaunchAgent on macOS, an XDG autostart file on
   Linux) with `--background`: tray only, no window. One entry serves every
   copy: it is rewritten when missing, stale or this copy's; another existing
   copy keeps it unless the person turns the option on in this one. Debug
   builds never add it on their own. On startup the tray app removes the
   person's own earlier Windows tasks (`LegacyUserTasks`), which needs no
   approval. `--run`, `--prune` and `--check` remain for manual use.
   The tray's schedule badge checks each enabled backup: with the service, its
   settings, start mode, running state and settings task; without it, the
   in-app schedule is ready. Paused, off, attention and unverified states
   remain distinct. Checks run on startup, focus, menu opening and settings
   changes, and once when a snooze expires.
   Quit saves a pause for this Windows sign-in session (`SessionTaskControl`,
   with the session ID and logon time). Closing to the tray does not pause.
   The tray app's scheduled runs, like `--run`, are admitted through the same
   record and cancelled when Quit changes its generation. A start at sign-in
   exits when the current session is paused. A failed pause write keeps the GUI
   open with an explanation.
   With the background service, Quit stops it (`SystemServiceLifetime`, see
   Windows snapshot protection); the desktop's own pause record covers the
   tray app's runs. Where Backspace lives, and who may change its files, is the
   owner's decision (27-28 October 2026): any local folder is accepted,
   including a drive root, a folder shared with other files, or a shared
   parent such as C:\Users\Public\Downloads. Setup does not change the
   permissions of the program folder, Backspace.config or the program files.
   Accepted risk: any program running as an account that can modify those
   files (for example the owner's, or every account's in a shared folder) can
   replace Backspace.exe or an engine and have the privileged service run it.
   Portable restic started from a SYSTEM service carries the same risk;
   installers avoid it by using Program Files. Only network drives, which the
   service cannot reach before sign-in, and read-only folders are refused
   (`ServiceInstallationSecurity.ValidateServiceLocation`). Do not change
   permissions on unrelated parent folders.

   `BackupServicePipe` is the local transport to the service. Its Windows ACL
   admits only the owner and SYSTEM, rejects remote connections, and does not
   grant clients permission to create a competing pipe instance. Before
   sending data, the client verifies that the kernel object's owner is
   `S-1-5-18`. Clients allow identification only, not impersonation of the
   desktop account. Messages have explicit size limits and deadlines. Each
   pipe name ends with a random part the service picks every time it starts,
   kept in `Backspace.config\service\pipe-names`; clients read it before each
   connection (U152). The runs pipe accepts a fixed vocabulary
   (`ServiceHostRequest`): activity, start, cancel, run-status, measure and
   `unlock` (the vault key, U153). A run started by Run now or the command
   line belongs to the service, like a scheduled one: the starter asks its
   state (`run-status`) only after each change the service announces, and
   nothing keeps it alive, so it goes on if the window or command line closes
   or crashes. Only a stop the user asks for ends it (U224).

   The version-3 service (Windows' own account) writes its history and logs in
   `Backspace.config\service\logs\engine`, where they inherit the portable
   folder's permissions. The desktop reads them there directly
   (`ServiceHistoryFiles`, U138): every run from `history.json` and
   `timeline.json`, and each log in full, with the service running or stopped.
   A recorded log path is used only if it is a `.log` inside that folder; after
   the portable folder moves, the file of the same name in the same backup
   folder is used. Reading in this direction gives the desktop nothing it could
   not already open. No service serves a history or transcript pipe: the
   earlier services (versions 1 and 2) are gone (6 November 2026).

   `LogStore` merges the service's runs into history and the timeline, and
   tells the views to reload when the service's history files change. That
   refresh event updates lists without treating old results as newly completed
   runs or changing the user's selection. Backup cards and the health banner use
   the newest completed backup result from this history without writing it into
   desktop settings. A later repository check cannot erase a failed backup, and
   a history refresh cannot replace a live or queued run.

   Live activity uses the authenticated run endpoint's read-only `activity`
   request. The owned worker sends bounded snapshots over its inherited stdout
   pipe, at most twice per second, rather than writing progress files or blocking
   the engine's output handler. The service runs one worker per backup, several
   at once (up to `SystemBackupService.MaxWorkers`, U156); a start for a backup
   already running, or beyond that number, is answered "busy", and the desktop
   shows that wait as "Queued: waiting for the background service to start it"
   (`ServiceScheduledRun`, `DesktopBackupRun`). The activity report is the run
   started last, with `Runs` listing each running worker and each backup's
   last finished run (identity, phase, source/destination percentages, the
   reason it is queued and the time it has spent queued, and final metadata);
   a card picks its own with `ServiceActivity.ForBackup`, and only running runs
   carry their transcript tail. Private
   transcript paths are removed. One desktop loop (`BackgroundServiceActivity`)
   updates cards even when the backup began before the window opened. It is
   event based (U224): it keeps one request open that the service answers when
   something changes, `activity-wait` (every change, while the window is shown)
   or `state-wait` (a run starting, stopping or ending, while it is hidden);
   the service ends a wait whose desktop disconnected
   (`SystemBackupService.WaitUntilChangedAsync`). A worker reports at most
   twice a second and at least every 10 s; while a run goes, a desktop that
   has heard nothing for twelve of those heartbeats (`Watchdog`) asks once.
   Background alerts are looked for when a run starts, stops or ends and when
   the session is unlocked (`SessionUnlockWatcher`). An unavailable service leaves
   the last known operation visibly unconfirmed; it never becomes a success.
   Stop carries that exact host run ID, so a delayed request cannot stop a newer
   run. Completed metadata is merged with history without changing saved settings.
   The same bounded worker feed carries the latest 32,768 characters of engine
   output (each line stamped like the run logs) and its run-history ID. Activity & logs shows that live output and merges
   the running entry without stealing a selected past run. A cumulative text
   position lets Clear view suppress previously displayed output across polling
   updates while still displaying new lines. Completed runs show their saved log
   file; the final live tail is not treated as an entire saved transcript.

   The worker (`--service-worker-owned` / `--service-worker-cli`,
   `ServiceApprovedWorker`) runs one operation the service admitted: it checks
   that it runs as the installed service, opens the portable vault with the
   service's key copy (a locked vault stops it before any engine starts, with
   the reason in the run's history), connects network shares with their saved
   credentials, sets its configuration and scratch to the service's folders
   (scratch on this computer, U153) and runs the ordinary orchestrator. Its
   report (`last-service-run.json`) names the vault it used. Each worker runs
   its own restic (U175); when each repository was last pruned is in the
   service's maintenance.json, shared by its workers. The host passes its run
   ID as the worker's revision, so each worker has its own reason file
   (`worker-reason-<run>.txt`). Workers share the service's
   scratch folder and rclone remotes; restic and rclone name their temporary
   files uniquely.

   Background workers persist desktop alerts through `ServiceNotificationQueue`
   in their private adjacent data folder before exiting. Email delivery stays in
   the worker; the desktop does not send it again. `ServiceNotificationProtocol`
   exposes only take-one and acknowledge-by-receipt operations over the existing
   authenticated pipe boundary. A 60-second delivery lease prevents concurrent
   sessions from showing the same alert. An acknowledgement follows actual popup
   presentation (or sound-only delivery), never just reading the queue. Failed
   presentation and locked desktops leave the alert pending. A crash between
   presentation and acknowledgement can repeat an alert; it cannot silently
   consume one. Queue writes serialize across processes, flush before replacing,
   and retry temporary Windows replacement errors without deleting prior state.
   The 8 MiB queue limit refuses further writes explicitly; it never trims pending
   alerts. The backup's normal saved run history remains independent.

   Pending alerts reach the owner through the tray app, which starts at
   sign-in (see the scheduler lifecycle above). Absent an owner session, alerts
   wait for sign-in. A start at sign-in stays in the tray without opening the
   main window or resuming a current-session Quit pause; opening Backspace
   explicitly restores its normal startup behavior. Native sign-in,
   lock/unlock and visible delivery require VM acceptance.

   `ServiceRunRecovery` saves the admitted backup ID, name, operation, start time
   and failure-alert choices before the worker process starts, one record per
   worker in `service\service-runs\<run>.json` (U156); records whose outcome
   is saved are removed when the service starts. The first worker
   history ID is retained separately, without writing on every progress update.
   If the worker or SCM host dies before a normal outcome is saved, the next
   confirmed exit/startup creates a dated failed history row and its exact-run
   desktop alert. An explicit Stop or Quit is recorded as skipped instead.
   A normal terminal outcome already present in history wins over recovery;
   a service crash after a successful backup must not rewrite that success.
   Recovery uses the host run ID for its own fallback row and notification,
   making retries idempotent. A failed history or alert write keeps that run's
   recovery pending; it is retried at the next start.
   Stop still proceeds if recording its reason fails. Pending notifications
   are checked every two seconds while idle; delivered backlog alerts are
   spaced ten seconds apart.

4. **Single executable.** `Backspace.exe` carries the pinned versions and
   SHA-256 fingerprints of `restic` and `rclone`; on first
   start it downloads them from those projects' releases into
   `%LOCALAPPDATA%\Backspace\bin` and checks them. No separate install step.
   A file with the same fingerprint in an earlier set is copied instead of
   downloaded, and the earlier set is
   then removed.
5. **Volume-name matching is first-class** for any local destination —
   never failed-state when an external drive is simply unmounted, always
   "skipped". `DriveRepositoryGuard` keeps an external drive or network
   share that lacks the repository (another drive with the same name, an
   erased one, a replaced NAS) from being started over: only a
   person-started first backup, or an answer recorded as a marker file at
   the drive's or share's root, or beside the repository folder when that
   root is read-only (`.backspace.<backup id>.accepted` / `.declined`),
   lets a run proceed without a repository. For shares it runs after the
   reachability gate, so an offline share is reported as offline. The orchestrator fails the cell otherwise, so alerts fire ("Waiting for
   you"); `DriveConfirmation` asks in the app before Run now. The
   accepted marker is removed after the first successful backup by
   `ExactPathDelete`, through a handle opened without following a link and
   only if its real path is the one named: the service does this as SYSTEM
   on a drive other accounts may change (U152).
6. **No surprises in the config dir.** One `config.json`; a user can
   delete it to start over. Secrets in a separate file (`vault.bin`,
   encrypted with the person's password) so the JSON is shareable / diffable.
7. **Portable layout.** Everything under `Backspace.config/` next to the
   exe — see Storage locations above. Don't reach into `%APPDATA%` or
   `~/.config`.

## Invariants to protect during refactors

- The `--run` / `--run-all` code path must be usable **without** the GUI
  loaded (no `App.Current`-style references). It spins up only what it
  needs via `ServiceLocator.InitializeCore`.
- restic's output must be read incrementally, never `ReadToEnd()`: real
  backups produce GB of output over multi-day runs, and a buffered read would
  blow the heap before the first checkpoint. `ResticRunner` and
  `StreamingRestoreProcess` read it a line at a time, cutting lines over
  128 KiB, and keep only a bounded summary.
- No daemon (U175): the window, `--run` and the service's worker run restic
  themselves (`ResticRunner`, `StreamingRestoreProcess`), and nothing of the
  engine runs between operations. `DirectBackupTests.A_scheduled_backup_never_starts_Backrest`
  watches every process under two `--run` runs; E61 checks that a backup,
  browsing and a restore leave no restic or rclone running. The password
  reaches restic only through its environment, never its command line or the
  transcript.
- Config file reads and writes must be **atomic** (write to `.tmp`,
  rename) — a scheduled run losing config due to a power cut is
  unacceptable. Same applies to `rclone.conf`, maintenance.json and the
  per-backup history JSON.
- New S4U registration never happens, and no Windows task runs a backup. A
  missed run is caught up by `ScheduleState` (survive sleep and shutdown, run
  missed slots once later) without logging on as the user.
  See the unattended-access limitations
  in [GOTCHAS.md](GOTCHAS.md#unattended-windows-access). The task-pause policy
  controls whether work starts; it does not grant additional access.
- `ResticEngine.IsValidSnapshotId` is the check for a restic id
  (64 hex characters): a snapshot id read from restic's output, the
  remembered parent, an id the window passes back. Don't loosen it: an id
  that is not one never reaches a restic command line.
- A backup's Filters text is restic's exclude file (U151). The engine writes it
  unchanged (`ResticEngine.WriteExcludeFile`) and the backup passes it
  with `--iexclude-file` on Windows, `--exclude-file` elsewhere
  (`FilterTranslator.ExcludeFileFlag`); no rule is ever a command-line
  argument. Never rewrite, bind or append rules on the way: restic expands
  `$NAME` in a file but not in an argument, and the owner's rules must work
  with restic alone. Left out, the size estimates, the change listing
  and test-restore completeness read the same text with
  `FilterTranslator.CompileRules`/`Evaluate`, restic's matching on full paths
  (`C:/…`, `?:` for any drive, last match wins); E57 checks them against
  `restic backup --dry-run`. Duplicacy lines are converted once
  (`DuplicacyFilterConverter`: when settings load and at import) and saved; no
  code path reads them at run time. Recommended rules
  (`RecommendedFilters`) are written in the same syntax, `$` as `$$`, in one
  marked block below the person's own rules, read back by its group headings.
- Recommended exclusions (U165): three tickable groups (`RecommendedFilters.Group`:
  Essentials, Windows system files, caches) plus cloud folders. Essentials
  replaced the always-applied block that ended every text before U165;
  `RecommendedFilters.UpgradeEssentials` converts such texts when settings
  load (`ConfigStore.Load` → `UpdateStored`), and drops only rules that can
  match nothing in the backup's folders (`CanMatch`), so the excluded set is
  unchanged. The same call applies pending list changes
  (`CheckForUpdates` → `ApplyUpdates`, hidden ones excepted) to backups with
  `Backup.AutoApplyRecommendations`: on for a backup set up only in Easy mode,
  off once Advanced opens from Easy (`BackupEditorViewModel(openedFromEasy)`).
  The background service loads settings through the same `ConfigStore`, so a
  run it starts after an update reads the updated text. Other backups show
  "New filter suggestions" on their card (`RecommendedFilters.HasSuggestions`);
  Review opens the editor at them (`BackupCreationWindow.StartAtFilterSuggestions`),
  where `SavingsEstimator.MeasureChangesAsync` measures each one against
  today's rules in one walk. Hidden suggestions live in
  `Backup.IgnoredRecommendationChanges`: `RenderBlock(hidden, kept)` leaves a
  hidden addition out and writes a kept rule again, so a re-render never
  undoes a choice.
- Filters › Recommended has no Apply (U165 follow-up, 10 November 2026). A
  tick calls `RecommendedFilters.WithGroup` (or `WithCloudFolders`) on the
  editor's `FiltersText` at once: only that group's part of the block changes,
  in place, other groups, pending suggestions and hand-written rules in the
  block untouched, and a copied second block dropped. The ticks are read back
  from the text after any edit (`SyncTicksFromText`), so a tick always means
  the group is in the rules. Each group's `RecommendedGroupState` compares the
  tick with the saved text (`_savedFiltersText`, the stored backup):
  In place / Will be added / Will be removed / Will change when you save / Off.
  `FiltersUnsaved` (text, hidden suggestions or automatic updates differ from
  the stored backup) drives the amber line on the page and the "Filter changes
  not saved yet" note beside Save. Save is the only commit point.
- Filters › Advanced is two tabs (U177): Rules, the text at full width, and
  Left out, filled only by Run. `LeftOutAnalyzer` walks the sources with
  `TreeWalk.Walk<TMark>` (parallel, links handed to the visitor and never
  entered, no deadline, cancellable) and decides each entry with
  `FilterTranslator.Decide` (the last matching rule's index, every matching
  rule marked), the backed-up folder included. A folder left out becomes one
  `LeftOutItem` and its subtree is only counted (files, bytes, online-only,
  unreadable folders); a file is a row only when its folder is kept. Rules
  never marked are `UnusedRule`s (`RulesMatchingNothing` says which can never
  match on Windows; rules between the recommended markers are counted apart).
  With the saved text differing, the same walk evaluates the saved rules too:
  rows they don't leave out are `IsNew`, and what they leave out but the
  edited rules keep is a `BackInItem`. The view model's partial
  (`BackupEditorViewModel.LeftOut.cs`) builds the flat row list (items or
  per-rule totals, back in, match nothing, unreadable) for one virtualised
  ListBox, the search (paths below the backed-up folder, rules), Keep this
  (`KeepRule`: `!` plus the escaped full path, appended last so it wins) and
  the rule links (`RuleHighlightRequested`, which the window turns into a
  selection on the Rules tab).
- Exclude this folder (U178), in What this backup added
  (`RunChangesViewModel`): `FolderExclusion.Rule` writes the opened folder as
  `RecommendedFilters.FolderLine` writes a cloud folder (`?:/…/`, glob
  characters escaped, `$$`); `FolderExclusion.Add` puts it last in the custom
  block (a new block at the top when there is none), keeping every other byte
  and never adding a line twice. `ExcludedBy` and `KeptBy` evaluate the text
  with `FilterTranslator` to say when a rule already excludes the folder or an
  exception would keep it in. The change goes through `ConfigStore.Update`, so
  the service's next run reads it; Undo restores the previous text when nothing
  else changed since, otherwise removes only that line. Snapshots are untouched.
- A destination missing its sign-in, S3 keys or storage password
  (`DestinationReadiness`, typically imported from Duplicacy) never reaches
  restic: Run now opens it for setup, and a run's cell fails at once with the
## Mounted snapshots (WinFsp)

`MountedSnapshotRegistry` owns one mount per backup in the desktop process.
`WinFspMountCoordinator` first reads what the drive will show, then mounts a
read-only `WinFspSnapshotFs` over `BackupMountTreeProvider` (one
`EngineBackedSnapshotTreeProvider` per chosen source and snapshot). The
objective set for U158 (7 November 2026): the card answers the click at once
and counts what is read; the drive appears when its trees are in memory; from
then on every folder listing and look-up takes milliseconds, whatever the
snapshot's size.

- **Choice (U162).** Mount opens `MountRevisionPickerWindow`
  (`MountRevisionPickerViewModel`): the revisions of each source, newest first,
  the latest ticked. The result is a `MountSelection`; nothing is remembered.
  Only the chosen trees are read, all of them, one after the other, before
  the drive appears, and nothing is read afterwards: there is no lazily read
  "All revisions" folder (on the owner's 600,000-file cloud backup, Explorer
  touching it read every older tree, 30-53 s each, for four minutes). Layout:
  every level with a single choice is left out. One source and one revision:
  its files at the drive root; several revisions: a folder per revision named
  by local date and time (`2026-11-05 09-12`, seconds added when two share a
  minute); several sources: a folder per source, the same rule inside. The
  revisions' file count and size come from restic's snapshot summary when the
  snapshot has one. `ResticEngine.TreeListingStarted` reports each tree
  read from the repository; E65 counts them.
- **Tree.** `ListSnapshotTreeAsync` reads the whole tree with
  `restic ls --json --recursive` and reports its progress. restic walks one
  folder at a time, so for a mount the source's top-level folders are shared
  between several restic processes (`ListingProcesses`: one per free core and
  per 2 GB of memory, at most 12), raced against a single listing that wins
  for a small snapshot. 632,000 entries: 27-36 s in one process, about 13 s
  shared out. `SnapshotTree` keeps the result (names, sizes,
  dates, sorted children) for the mount's life. A snapshot is immutable, so
  nothing is asked twice. The per-folder `ListSnapshotDirectoryAsync` is only
  the fallback for an engine that cannot list whole trees.
- **Windows caching.** File, folder, volume and security information never
  expire (`uint.MaxValue`), and the file contents Windows cached stay valid.
  Directory listings resume after a marker by binary search.
- **Files.** The first read downloads that file alone into the mount's cache;
  files nobody opened are never fetched (the owner, 7 November 2026), and a
  whole snapshot is never restored because files were opened. The download
  is progressive (U167): `restic dump` streams the file
  (`IBackupEngine.DumpSnapshotFileAsync`) into a temporary file in the
  revision's `partial/` folder (`ProgressiveFile`); opening returns at once
  with the size from the tree, and a read waits only until its own range has
  arrived. `WinFspSnapshotFs` answers such a read asynchronously
  (`STATUS_PENDING`, then `FileSystemHost.SendReadResponse`), so no WinFsp
  thread waits on the network. Every reader of the file shares the one
  download; it becomes the cached file only once complete with exactly the
  listed size, a failure fails every read and leaves nothing cached, and it
  stops 15 s after the last reader closes the file. Empty files need no
  download; an engine without `dump`, or a snapshot read folder by folder
  (which could hold links), restores the file into `partial/` first. WinFsp's
  request timeout is raised to its 10-minute maximum through WinFsp.Net's
  private volume parameters (`WinFspSnapshotFs.RaiseIrpTimeout`). Files and
  folders carry `NOT_CONTENT_INDEXED`, so the search indexer leaves them alone.
  A program that opens a file (an Explorer thumbnail or preview of a photo,
  for instance) still fetches it.
- **Cloud.** A successful cloud connection check is trusted for two minutes
  (`CloudDestinationPreflight.TrustRecentSuccessFor`), so consecutive reads do
  not each start with a sign-in round trip.
- **Feedback.** `GetMountProgress` says what a mount is doing ("Reading the
  backup's folders: 120,000 files so far"; the percentage is beside the bar only, with several revisions "…,
  revision 2 of 3 (5 Nov 2026 09:12): …"); the card shows it once the picker
  closes, then "Mounted as Z: (5 Nov 2026 09:12)" (`GetMountedDescription`)
  with Open in Explorer, or the failure inline.
- **Percentage (U166).** The tree listing counts files, not folders, which
  matches the snapshot summary's file count the picker already has (the
  owner's snapshot: 486,506 entries = 415,256 files + 71,249 folders + root;
  summary 415,256 files). `MountReadEstimate` turns files read over that count
  into one percentage for the whole mount, each revision weighted by its file
  count; a revision without a summary borrows another chosen revision of the
  same source, otherwise the bar stays indeterminate while it is read. The
  percentage never goes backwards and stays at 99% at most until the last
  tree is read. `GetMountPercent` feeds the card's bar; no restic call is
  added. While the whole listing and the shared-out one race, progress is the
  further of the two, not their sum.
- **Exit.** Quit, normal exit and the process-exit hook call `DisposeAllAsync`.
  The drive is served by the desktop process, so if that process dies
  (crash, Task Manager, force quit) WinFsp removes the drive with it.
  Closing the window to the tray keeps the app, and therefore the drive.

### Snapshot readers (U159)

`SnapshotReaders` keeps a destination's stored data in place while it is
read. A mount holds its destination (and a global "a snapshot is mounted"
file) until unmounted; every engine restore and whole-tree listing holds it
while it runs. Locks are files beside the other operation locks
(`SystemServiceLocks` when the background service is set up, otherwise
`%LOCALAPPDATA%\Backspace\locks`), opened shared by readers and exclusively
by a change, so they work across the window, scheduled runs and the service,
and go away with a process that ends.

| Operation | While read | Where |
|---|---|---|
| Cleanup after a backup (`forget --prune`, prune) | Skipped: "Cleanup waits: …"; the backup still succeeds | `BackupPruneCheckAsync` |
| Stand-alone cleanup | Skipped with the same reason | `RunPruneOnlyAsync` |
| Removing a snapshot, prune for removal/erase | Refused: "… has to wait: …" | `PruneSnapshotIdAsync`, `PruneExhaustiveAsync` |
| Password change | Refused | `ChangeStoragePasswordAsync` |
| Any exclusive maintenance (delete, erase, moves) | Refused while any snapshot is mounted | `MaintenanceCoordinator.AcquireAsync` |
| Backup into the same destination | Allowed: it only adds a snapshot | — |

A mount that starts while a change holds the destination waits and says so.

  reason instead of retrying (U082).
- `BackupOrchestrator` raises `RunStarted` and `RunEnded` to each subscriber
  separately (`RaiseRunStarted`, `RaiseRunEnded`). A multicast invoke stops at
  the first handler that throws, which left the tray, cards and history
  showing a finished run as still running (U069).
- Cross-platform code paths under `Services/Platform/`: never call a
  Windows API directly from `src/Backspace/Services/*.cs` — go through
  the `IScheduler` / `ISecretsEncryption` interface or the
  `PlatformInfo.IsWindows` guard. The csproj `Compile Remove` excludes
  `Services/Platform/Windows/` from the cross-platform TFM, so a direct
  call would compile on Windows but fail to load on macOS / Linux.

## CLI surface

- `Backspace`                              — launch GUI
- `Backspace --background`                — start in the tray only (the sign-in start entry)
- `Backspace --run <backup-name>`          — run one backup now, without the GUI (automatic backups on or off)
- `Backspace --run-all`                    — run all enabled backups now, without the GUI
- `Backspace --prune <backup-name>`        — standalone retention and repository cleanup
- `Backspace --check <backup-name>`        — standalone repository integrity check
- `Backspace --migrate <path>`             — one-off import from an existing Duplicacy / duplicacy-util setup (single repo or parent-of-many)
- `Backspace --version`
- `Backspace --help | -h | /?`

On Windows the binary is `Backspace.exe`; on Unix it's `Backspace`.

Build and test commands live in [README.md § Building from source](../README.md#building-from-source) and
[TESTING.md](TESTING.md). They are not duplicated here.

## Repository operation and persistence coordination

`ConfigStore` and `SecretsStore` lock their state file across the complete read-modify-write transaction. A writer reloads the latest disk state inside that lock, applies its mutation and atomically replaces the file. Unread or locked secrets are never treated as an empty vault. Editors preserve newer run/verification metadata when saving a draft opened before a scheduled completion.

Secret publication also retries known temporary Windows replacement errors
for up to 1.55 seconds. A failed publication keeps the encrypted candidate
and previous `.bak`; it does not report success or replay an uncertain rename.
If the primary vault is missing while its backup exists, opening or writing
fails rather than silently creating an empty credential map. Recovery of that
case is explicit; an unacknowledged pending credential write is never imported
automatically over newer credentials.

Settings also retain their previous `.bak` and pending file when publication
fails, and never initialize a fresh configuration if the primary is missing
while that backup exists. Settings saves retry known temporary replacement
errors briefly; they never retry Windows errors that may have already renamed
the original file.

`MaintenanceCoordinator` keeps a read lease for the lifetime of backups, restores, verification and mounted-file engine reads. Destructive maintenance and password changes require exclusive access, including a portable-config file handle that coordinates separate Backspace processes. Cancelling an operation is not proof it stopped: every restic run returns only once restic has exited (after Ctrl+Break, then the end of its job), so the lease ends only then. A process whose end cannot be confirmed blocks further repository work until restart. Repository locks are never force-removed for password rotation.

Restores pin the immutable snapshot ID selected by the user; display revision numbers never identify an already selected snapshot. Output first goes into a private staging directory. Only confirmed complete selected files are moved to the target; bracket-pattern cleanup never sweeps that target. The replacement and folder-layout options apply during publication. Duplicate flattened names are rejected before replacing any file, and linked targets or ancestors are rejected before staging. Whole-folder restores also preserve empty directories. If cancellation or a disk error occurs while publishing completed files, files already moved remain and the result reports how many were copied. Restore sessions retain their own wizard state, progress and result until dismissed.

Cleanup asks restic for the snapshots belonging to each configured backup/source plan (both labels) and forgets explicit snapshot IDs. It never deletes repository directories by their names. Removing a destination preserves unconfigured snapshots, unrelated files and reusable repository settings. Destination ownership is removed from configuration before its exclusive maintenance lease is released.

Backups and standalone maintenance refresh saved configuration after acquiring
their operation lease, before touching repositories. This includes a scheduled
process that was waiting while another process removed a backup or destination.
An unreadable configuration stops the operation. A run that finishes only some
sources or destinations reports incomplete coverage; it neither advances the
last complete-backup timestamp nor sends a successful monitoring ping.

Startup cleanup trusts persisted process identity records (owner PID plus start time; child PID plus start time and executable) and leaves unknown/live owners alone. Windows job ownership covers restic and the rclone it starts. There is no process-name or bundled-path sweep.

On Windows, executable identity comes from `QueryFullProcessImageNameW` on
the process handle. Reading `Process.MainModule` immediately after launch
can return no module before the loader runs, leaving the recovery record
unwritten. The lookup never substitutes the requested executable path for
the actual process identity; a failed OS lookup remains unknown.

On Windows, the platform builder uses Avalonia's RedirectionSurface composition
path and renders on the processor (`Win32RenderingMode.Software`, U109). The
GPU path (ANGLE over Direct3D 11) held about 140 MB more for the same window:
measured on 4 November 2026 with the release build and a real backup running,
the window used 330-355 MB with the GPU path (and 3% GPU for the running card)
against 175-187 MB rendered on the processor, at under 1% CPU. Avalonia 12.1.3 can
pause its compositor render loop when there is nothing to draw; Avalonia 11 kept
ticking while idle. Native window-host measurements recorded lower idle CPU after
the upgrade. They use an isolated configuration and do not guarantee zero CPU on
every system. The test harness uses the matching Avalonia 12 headless packages
and xUnit v3. DataGrid has its own package release, 12.1.2.

Closing to the tray destroys the native main window and releases its visual
tree, icon resources and view subscriptions. `TrayWindowHost` retains the main
view model so an active restore, editor draft and selected page survive.
Opening from the tray, a forwarded launch or a notification creates a new
window around that same model. Only one window exists at a time: a request
that arrives while the window is being made (a tray double-click right after a
start at sign-in opened two windows, U201) waits for it and shows that window.
Explicit Quit still uses the operation drain
and disposes the model only when the application exits. A paused or running
operation must never be cancelled merely because the user closed the window.

The main window refreshes external configuration and run history on activation
and when entering Backups or Activity. A fingerprint check and transaction run
off the UI thread; no idle polling or watcher loop is introduced. Unsaved editor
drafts remain separate, and saving them preserves newer runtime results.
