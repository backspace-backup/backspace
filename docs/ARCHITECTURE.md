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

Backrest runs in the background on an ephemeral IPv4 loopback port. Its HTTP
server carries the API used by Backspace, so stopping that server also stops
backup and restore operations. Backspace suppresses Backrest's tray icon and
does not automatically open its browser interface.

Before starting the daemon, Backspace writes a random per-instance password
and its bcrypt hash into the local credential/configuration files. The daemon
therefore requires authentication from its first request; there is no open
setup interval. Backspace signs in and sends a bearer token with API calls.
Missing credentials are replaced on disk before launch, preserving unrelated
configuration. Failed sign-in stops startup. Corrupt or newer configuration
is preserved and rejected, never replaced with an empty repository list.

This protects against unauthenticated local HTTP callers, including websites
that attempt to reach localhost. It does not isolate Backspace from malware
already running as the same Windows user or as administrator. The password
file receives a current-user ACL where the filesystem supports it.

## Windows snapshot protection

New backups enable Windows snapshots (VSS) by default. The desktop stays at
normal privilege so Explorer can see its mounted drives. VSS comes from
administrator access, a one-time grant that installs the background service
(29 October 2026, U026). Creating a backup offers it as a ticked choice; the
wizard applies it right after saving and before the first run, and the
advanced editor's caller does the same. `DesktopBackupRun` sends Run now and
Save and run to that service through the owner's `start-cli` request when
the service is active and the saved settings are approved; the card follows
the run through the service's activity. Otherwise the desktop runs the backup
itself. The service runs the configuration recorded in its protected
approval; a saved change makes scheduled runs wait until it is applied
(`ServiceConfigurationComparison.RequireCurrent`). The owner no longer
approves each change (31 October 2026, U043): activation registers an
"Apply saved settings" task in the service's task folder that runs as
SYSTEM with one fixed argument (`ServiceSettingsTask`). Its access list
lets the owner start it but not change it. The desktop starts it after any
saved change (`BackupsViewModel.ApproveChangesInFlowAsync`, debounced, and
retried a minute later while a run holds the operations lock). It applies
the owner's current `config.json` through `ServiceConfigurationUpdate.Apply`,
so the same validation, schedules, coordination files and repository access
apply as for an approval. Trusting the owner's saved settings follows the
accepted risk below: whoever can change them can already replace the
executable in a folder they control. A setup made before this task existed
asks Windows once, which registers it; a failure to apply shows its reason
with Try again and no prompt. The task first finishes any update left
unfinished, including one interrupted after its policy was saved; otherwise
every later change would be refused. The update's record is read with shared
deletion, and the service may be started by the owner's app at the same time
as by the update (`ServiceRegistration.EnsureRunning`); both happened in the
release VM check of 31 October 2026.
An approval is verified against its configuration exactly as saved
(`ServiceRunAuthorization.SavedDigest`): serializing it again would add
settings introduced by a later version and invalidate every approval after an
update. Turning access on is one approval: the elevated `--service-setup`
process prepares the service, leaves `service-setup-prepared` in the settings
folder, waits while the desktop hands the saved sign-ins over, then activates
(`ServiceActivation.ActivateAfterHandoff`). Turning it off is one approval
too: the elevated `--service-retire` process freezes the service, leaves
`service-retire-prepared`, waits until the desktop has taken its sign-ins back
and written `service-retire-returned`, then removes the service
(`ServiceRetirement.CompleteAfterReturn`). It waits for the desktop rather than
the service's receipt, so the service is not stopped mid-reply; `Complete`
still verifies the receipt and the returned file. Backups whose destination is a
network share are left to the tray app's schedule (`SchedulePlan.ServiceCanRun`),
since the service's account cannot sign in to a share; approval checks skip
them so they do not block the others. External drives run in the service
(`ServiceExternalDrives`): an unplugged drive is left out of the run, which
skips it; when a connected drive's folder is not writable for the service, the
worker starts the settings task (its folder and task allow the service to read
and run them), which creates the folder and grants access as SYSTEM.
`VssElevationPolicy.PromptAllowed` is off, so no run asks Windows for
approval: a run that cannot use VSS backs up without it, records a warning
naming the fix (allow access, or approve changes), and keeps the saved VSS
setting. The per-run elevated helper (`VssElevationCoordinator`) remains
behind that switch until this is validated, then it can be removed.

Disabling snapshots requires an explicit acknowledgement in the advanced
backup editor. Older configurations with snapshots off and no acknowledgement
are blocked before repository work. The sidebar shows Limited protection
while any backup has snapshots disabled, with an explanation and an action to
enable them. NAS sources and filesystems unsupported by VSS can legitimately
need the disabled setting. A VSS-enabled run can still warn or fail if Windows
cannot create a snapshot; configuration is not a guarantee of successful capture.

## Scheduled repository checks

The editor presents four mutually exclusive choices: inspect backup records,
verify saved data, try recovering files, or run both deeper checks. These map
to the existing `CheckReadDataSample` and `CheckRestoreFiles` flags; opening
an older configuration with both flags enabled preserves both. Recovery alone
still inspects repository records first, then reads file contents while
recovering them. Selecting both additionally samples stored data in a separate
pass, which can download overlapping data twice. The UI explains that cost.

`Backup.CheckReadDataSample` is false by default. Structure-only checks use
Backrest's native repository Check operation. The opt-in data sample uses
Backrest's tracked RunCommand path to run bundled restic `stats --mode raw-data
--json`, then `check --read-data-subset=<bytes>B`. The byte target is the smaller
of 1% of the stored blob size and 1 GiB. An invalid or unavailable size fails
the check; it never falls back to an unlimited download. RunCommand retains
the usual operation ownership, cancellation acknowledgement and diagnostics.

This is an approximate sampling target, not a network quota. Restic converts
the byte target to a fraction of pack count, with a minimum of one pack.
Unequal packs, metadata, compression and retries affect traffic. Shared
repository settings are not changed to apply one backup's preference. The
standalone coordinator checks each repository once, while retention remains
per source plan. Inline checks run with the last configured source for each
destination. The result distinguishes structure-only, sampled-data and full
recovery work; a sample never establishes full data coverage.

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

`Backup.CheckRestoreFiles` enables the automatic step after a successful check;
it defaults to false. Manual and automatic checks use the same
`RestoreCheckOptions` view and `TestRestoreService` journal/ownership path.
The automatic coordinator passes its existing maintenance read lease to the
verifier instead of trying to acquire a nested lease while a writer waits.
Cancellation and the metered-network watchdog cover the extra restore work.
An automatic comparison failure makes a backup warn, or a standalone check
fail; it never turns a successful copy into a claim that recovery was tested.

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
repository read lease remains held throughout. Resumable UI requests use this
owned process because Backrest's native Restore endpoint rejects an existing
target. Ordinary file restores resume only within the current application
session. Test restore saves its plan and completed batches across launches.

`RestoreOutputTransaction` keeps replaced originals until all selected sources
finish. Cancellation rolls back those publications, including earlier sources;
their registry sessions do not report completion before that finalization.
Rollback checks file contents before removing output. A concurrent user edit
is preserved alongside recovery copies and reported as an error. Each original
move has a flushed path map under `.backspace-undo-*`; a crash can leave that
folder for manual recovery. This is not automatic crash recovery. Verified
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
and reports their location; it cannot report successful cleanup.

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

Backrest can also exit when its own startup configuration write is denied.
`BackrestSidecar` detects that exit promptly and retries only when the current
launch's log explicitly identifies the configuration replacement access error.
It stops and releases each owned failed attempt before starting another.
Unrelated exits and stale log entries do not qualify. If cancellation separated
the local password write from the configuration replacement, the next startup
checks the password against the saved hash and repairs the mismatched pair
before exposing a client.

The Backups health banner includes a failed Test restore, even if the latest
backup succeeded. Another successful backup does not clear that warning; a
passing Test restore does. Cancelling a test preserves the previous completed
verification result. A backup with both kinds of failure appears once in the
attention list and is not counted among healthy backups.

Sample history records successful checks by source, destination, path, size
and modification time. Selection favours never-checked and oldest-checked
files, with random ties. A sample is labelled as such throughout its result;
it cannot establish that every file is recoverable. The configured byte
budget describes selected file sizes, not compressed network traffic, and
can be exceeded by the last whole file selected for each source/destination.

## Windows release updates

`WindowsUpdateViewModel` owns the Settings update controls and a one-shot timer for the chosen check frequency. Checks use GitHub's latest stable release endpoint; downloads and restarts require a click. `WindowsUpdatePackage` verifies the official asset URL, exact size, GitHub SHA-256 digest, safe archive entries and application version before preparing a complete bundle under `.backspace-updates` beside the executable.

`WindowsUpdateRuntime` shares a process-lifetime file lease with GUI, scheduled and helper runs. Replacement requires its exclusive lease after the GUI closes and owned engines stop. The application operation gate also prevents new work during a requested restart. Replacement journals the old and new program files and rolls back failures; configuration is outside that file list. A recovery launcher can restore the previous bundle. A successful process start is not proof of a healthy new GUI: native launch/crash behavior remains an acceptance-test requirement. The immediately preceding bundle is retained. Older completed update records can be removed after the next successful replacement; unknown or unfinished records are preserved.

Only portable Windows x64 single-file builds in non-linked folders support installation. Ordinary copies require a writable folder; an activated service copy uses Windows administrator approval as described below. Other builds keep the manual release-page action. The internal `--recover-update` path runs before engine/configuration initialization. Each update keeps a separate recovery executable in its work folder, so rollback never moves the file that is running recovery. The launcher waits for that process and preserves its exit code, including after the failure prompt. Recovery opens the restored app through the Windows shell to keep its output separate from the launcher's captured output; the launcher can then finish while the restored app stays open.

`ServiceProgramUpdate` provides the separate protected replacement transaction
for an activated Windows service. It accepts a stable release tag and a staging
ID, then fetches that exact release from the official GitHub API after elevation.
It pins the owner's staged archive against replacement, verifies the official
size and checksum again, and extracts only the four portable executables into
the protected control folder. No caller-supplied URL, hash or executable path
authorizes replacement. Its pending record blocks new service work. It disables
service restart, replaces the bundle, and updates the permission-recovery
journal to identify the new files while retaining the original ACLs. Recovery
rolls an incomplete replacement back or finishes enabling an already verified
replacement. Ordinary update recovery refuses these service-specific plans.
The Settings update button stages its download under the owner's portable data
folder, closes Backspace, then invokes `--service-update <tag> <staging-id>` with
Windows administrator approval. The elevated helper writes a protected result
for that attempt; the ordinary caller checks its identity and exit code before
reopening the GUI without elevation. The public download is then disposable;
the previous program files and recovery runner stay in the protected work folder.
After a successful service update, older completed or rolled-back work folders
are removed. The latest rollback bundle stays available. Unknown, unfinished,
redirected or mismatched records are retained rather than swept away.
`--service-recover-update <plan>` restores the previous version, including after
a completed update. `--finish-pending` instead completes the recorded recovery
decision after interruption. A launcher from an older attempt cannot replace a
newer update. Ordinary GUI startup offers recovery before opening a pending
bundle; if the executable itself is missing, the retained recovery launcher can
still run. Native UAC, Settings clicks and live GitHub delivery require separate
acceptance checks; automated backend coverage does not establish those results.

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
│   │                                  backrest(.exe) + restic(.exe) + rclone(.exe)
│   │                                  (gitignored, fetched at build)
│   ├── FetchBackrest.targets        — MSBuild target that downloads the engines on demand
│   └── BuildNativeHost.targets      — Windows-only: framework-dependent startup
├── src/Backspace.Host/              — native startup and .NET runtime setup
└── tests/Backspace.Tests/           — see TESTING.md for the layout + harness
```

## Storage locations (on the user's machine)

Everything is **portable**: the app keeps all state in one folder next to
the executable. There is **no** install footprint under `%APPDATA%` or
`%LOCALAPPDATA%`.

- `<exe-dir>/Backspace.config/config.json` — app settings, backups, destinations
- `<exe-dir>/Backspace.config/secrets.bin` — encrypted secrets (API keys, OAuth tokens, storage passwords)
- `<exe-dir>/Backspace.config/pending-removals.json` — removed backups whose stored copies are still to be erased; absent when nothing is waiting
- `<exe-dir>/Backspace.config/backrest/` — the GUI-owned Backrest data directory. Scheduled runs and elevated VSS helpers use isolated directories under `engine-runs/` so one process cannot shut down another process's daemon.
- `<exe-dir>/Backspace.config/rclone-remotes/` — one generated remote configuration per cloud destination; refreshing one account cannot erase another.
- `<exe-dir>/Backspace.config/test-restores/` — saved test-restore plans, completed batch results and sample history. Temporary recovered files live separately under `verify/`.
- `<exe-dir>/Backspace.config/logs/app/` — Serilog daily-rolling Backspace logs
- `<exe-dir>/Backspace.config/logs/engine/<backup-name>/` — per-run engine transcripts (restic stdout via Backrest)
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
entry, service registration and its settings task, Explorer integration and the optional WinFsp driver
use Windows-managed registrations. Mounting a snapshot as a drive requires
that driver to be installed with administrator approval. Normal restore
does not require it. A service can point at a local executable outside
Program Files. The service protects only its own records and private vault
(the windows-service folder); the program folder and its files keep whatever
permissions the owner gave them. No separate application
installation or external persistent-data folder is authorized. Reinstallable
system dependencies and reproducible caches may live outside the portable
folder, provided missing dependencies are detected and can be recreated on a
new computer. That exception does not include unique settings or credentials.

The `BACKSPACE_CONFIG_ROOT` env var overrides the location — used by the
test harness to give each test its own isolated config dir.

## Design principles (stick to these)

1. **Backrest owns its own state.** We do not reach into Backrest's
   config DB or operation history directly. Every read goes through
   `GetConfig` / `GetOperationEvents` / `ListSnapshots` /
   `ListSnapshotFiles`, every write through `SetConfig` / `Backup` /
   `Forget` / `DoRepoTask` / `Restore` / `RunCommand`. Single-path restores
   use native Backrest Restore when it can express the request. Scattered
   selections, per-backup bandwidth limits and significant whitespace use
   one owned bundled restic process, with Backrest's repository configuration.
   It streams bounded progress and retains the caller's repository lease
   until exit is confirmed. These restores appear in Backspace, but not in
   Backrest's operation history. Password changes and complete snapshot-tree
   listings also invoke bundled restic directly. Tree listings use one recursive
   request for local and cloud repositories, with the destination's own rclone
   environment. Invalid or incomplete output falls back to Backrest's directory
   requests. A bounded, in-memory cache remembers immutable snapshot membership
   for the current repository, plan and daemon configuration revision. It avoids
   a second repository listing before each read. Complete file metadata is also
   retained for at most eight snapshots and 65,536 nodes in total, so reopening
   a small tree does not start another engine process. These caches do not hold
   file contents or prove that a snapshot still exists. Refreshing the snapshot list always
   reads the repository. A deleted selection must fail, never select newer data.
   Cached metadata reads do not run a provider connection probe. Real repository
   reads, restores, backups and maintenance still check the cloud connection;
   initializing a new cloud repository also checks before creating it.
   Backspace also refreshes its owned plan fields when settings change, while
   preserving unknown daemon fields. Backup plans use keep-all retention;
   explicit inline or independently scheduled cleanup uses a tracked command
   with the configured retention and both the backup's plan and instance tags.
   It never temporarily changes a shared plan's retention. This prevents Backrest's automatic cleanup
   from running ahead of Backspace's schedule or duplicating its inline request.
   Healthy tasks do not run an unconditional unlock first. A confirmed lock
   failure can try one normal stale-lock removal and retry; unknown outcomes
   cannot trigger that recovery. Active locks are never force-removed. Brief Windows config
   replacement conflicts reread and retry at most four times; unrelated failures
   and persistent permission errors remain failures.
   Backrest 1.14.1's automatic snapshot index has no pending operation record.
   After a saved backup, Backspace waits for both the repository's finished-index
   counter and the exact snapshot's index record. This keeps subsequent reads
   from racing the invisible task. A failed index or unconfirmed cancellation
   blocks further repository work until restart; an unchanged backup that creates
   no snapshot does not wait for an index that will never be scheduled.
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
   launchd or systemd. `ScheduleLoop` (in `BackupScheduler.cs`) runs due plans
   one at a time. `SchedulePlan.From` lists each backup and its optional check
   and prune cadence. `ScheduleState` saves each plan's next due time beside the
   settings (`schedule-state.json`), so a slot missed while the computer was off
   or Backspace was not running runs once afterwards ("catch up missed runs";
   with it off, a slot more than 10 minutes late is passed over). New and
   changed schedules start from their next time; disabled and snoozed backups
   pass over their slots. The start is recorded before the run, so a crash or
   Quit never repeats a slot; slots that pass during a long run are skipped, so
   runs never overlap. A due plan waits while it is blocked (battery, a network
   it needs, Quit) and then runs once. The execution time limit and "stop on
   battery" cancel a running plan. Between runs the loop only sleeps until the
   next due time, at most a minute so that sleep, hibernation and clock changes
   are noticed; a saved change wakes it.
   Who runs the loop: `DesktopSchedule` in the tray app, in process, through
   the orchestrator like Run now, unless the background service owns the
   schedule; then `BackupWindowsService` runs it with the approved settings.
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
   For an activated service, the desktop sends these actions through
   `ServiceSessionProtocol`. The pause record and running-work handle stay in
   the private service folder beside the portable executable. Requests cannot
   supply file paths. Windows supplies the pipe client's session ID; a pause
   must name that session and the configured owner. The service ends the
   pause when Windows reports that session's sign-out (`OnSessionChange`), when
   the saved logon predates the last restart, or when the session no longer
   exists. Sleep, hibernation, locking and disconnecting keep it. The service
   retains the running handle until its worker exits, including after
   cancellation. A quick reopen preserves the generation that stops old work.
   Service restart retains the pause; an invalid run request is rejected
   without terminating the command listener.
   This describes the current implementation, not a prohibition on a service:
   before-sign-in backups must also support passwordless accounts. A future
   service must use an administrator-protected executable and protected storage
   inside the portable folder. Windows service registration can point at that
   existing executable; it does not require a second application installation.
   Windows does not require Program Files, a drive-root folder or any particular
   folder name. Where Backspace lives, and who may change its files, is the
   owner's decision (27-28 October 2026): any local folder is accepted,
   including a drive root, a folder shared with other files, or a shared
   parent such as C:\Users\Public\Downloads. Setup does not change the
   permissions of the program folder, Backspace.config or the program files.
   Accepted risk: any program running as an account that can modify those
   files (for example the owner's, or every account's in a shared folder) can
   replace Backspace.exe or an engine and have the privileged service run it.
   Portable restic or Duplicacy started from a SYSTEM service or task carries
   the same risk; installers avoid it by using Program Files. Only network
   drives, which the service cannot reach before sign-in, and read-only
   folders are refused. Do not change permissions on unrelated parent folders. A service must authenticate its
   clients and authorize individual backup plans; copying arbitrary desktop
   configuration, hooks, executable paths or environment overrides into a more
   privileged worker is unsafe. Preserve the user's existing encrypted vault
   while designing and testing any separate unattended secret storage.

    `ServiceInstallationSecurity` validates the service's own protected state
    before it is trusted: ordinary accounts must not be able to change or
    replace the windows-service folder's records or read its private data.
    The program files are checked only for presence, location and links; their
    permissions are the owner's choice, and folders above windows-service are
    not checked. Links, remote paths,
    alternate data streams and ambiguous path spellings are rejected. The
    registration step must establish these boundaries before starting the
    service; this validator does not itself register or activate anything.
    The code-tree check permits the separately validated private data folder
    beneath the program folder, without treating writable history and vault
    files as executable code. No other writable or redirected child is exempt.

    `BackupServicePipe` implements the local transport boundary for that
   service work; it is not yet connected to the production scheduler. The
   listener must run as its configured virtual service account. Its Windows
   ACL admits only the configured owner, the service and SYSTEM, rejects
   remote connections, and does not grant clients permission to create a
   competing pipe instance. Before sending data, the client verifies the
   kernel object's owner against the expected service SID. An ordinary
   process cannot impersonate the service by claiming its name or PID.
   Clients allow identification only, not impersonation of the desktop
   account. Messages have explicit size limits and cancellation deadlines.
   Administrators and SYSTEM remain trusted. This transport does not itself
   authorize backup plans, migrate secrets, install a service or make the
   existing scheduler safe for before-sign-in production use.

   `ServiceHistoryProtocol` adds a separate authenticated, read-only history
   endpoint. A request names a backup ID and an optional run-ID cursor, never
   a filesystem path. Pages are limited by both record count and serialized
   size. Newer runs do not shift an existing cursor; a removed cursor or an
   oversized single record produces an explicit error. Returned records are
   copies with private transcript paths removed. The service does not write
   into the desktop's writable history directory. `ServiceHistoryCache` pulls
   pages in the ordinary desktop process and saves them beneath the adjacent
   configuration folder. `LogStore` merges those outcomes into history and the
   timeline. A separate refresh event updates lists without treating old cached
   results as newly completed runs or changing the user's selection. Refresh runs
   off the UI thread, stops paging once it reaches cached history, and retains
   the preceding cache on incomplete reads. A stale reply cannot overwrite a
   concurrent cache update. Backup cards and the health banner use the newest
   completed backup result from this history without writing it into desktop
   settings. A later repository check cannot erase a failed backup, and a
   history refresh cannot replace a live or queued run. This history path supplies
   completed run metadata. `ServiceTranscriptProtocol` separately accepts a
   backup/run ID and returns at most the last 64 KiB of its recorded transcript.
   The path comes only from service-owned history, must remain under the service
   log folder, and cannot pass through a reparse point. Missing or expired logs
   produce an explicit message. The desktop does not receive private file paths;
   a late transcript reply cannot replace a newer run selection.

   Live activity uses the authenticated run endpoint's read-only `activity`
   request. The owned worker sends bounded snapshots over its inherited stdout
   pipe, at most twice per second, rather than writing progress files or blocking
   the engine's output handler. The host reports only its current worker's
   identity, phase, source/destination percentages and final metadata. Private
   transcript paths are removed. One desktop polling loop updates cards even
   when the backup began before the window opened. An unavailable service leaves
   the last known operation visibly unconfirmed; it never becomes a success.
   Stop carries that exact host run ID, so a delayed request cannot stop a newer
   run. Completed metadata is merged with history without changing saved settings.
   The same bounded worker feed carries the latest 32,768 characters of engine
   output and its run-history ID. Activity & logs shows that live output and merges
   the running entry without stealing a selected past run. A cumulative text
   position lets Clear view suppress previously displayed output across polling
   updates while still displaying new lines. Completed runs use the saved-log
   endpoint; the final live tail is not treated as an entire saved transcript.

   `SecretsStore` can open an explicit trusted path for a separate service
   identity. Its migration snapshot refuses an unreadable source. The
   synchronous migration transaction keeps the source vault locked through
   transfer, target verification and activation, preventing a concurrent
   refresh from being silently lost. Import is one-time: it checks an
   encryption/decryption round trip, flushes a temporary file, then publishes
   without replacing an existing target. The original vault is retained.
   These primitives do not activate migration themselves; registration and the
   authenticated service dispatcher must complete and verify the handoff.

   `ServiceSecretsBackend` routes existing `SecretsStore` consumers to one
   service-owned vault. `ServiceSecretsProtocol` permits only validate, get,
   set and remove, with bounded fields and strict JSON parsing. It accepts no
   file paths, executable commands or full-vault export. Every operation holds
   the vault transaction through the read check and mutation; a missing or
   unreadable service vault cannot be replaced by an automatic token refresh.
   The client authenticates the service before sending a secret and never
   falls back to the retained desktop vault. Writes are not automatically
   retried after a lost response. Enabling this backend still requires the
   protected installation binding and verified migration. Ordinary desktop
   construction uses `ServiceOwnerSecrets` only for the executable's adjacent
   configuration folder; test overrides and private worker data stay isolated.
   Each operation holds the original vault transaction while resolving the
   protected binding. Before activation it uses the original provider; after
   activation it uses the authenticated service endpoint, starting the service
   if necessary. An already-open desktop therefore follows the provider switch.
   Invalid binding or service access fails explicitly without a local fallback.

   `ServiceRunAuthorization` reads an administrator-protected approval with
   an owner SID, revision and configuration digest. A desktop request can name
   only an approved backup, the current revision and backup/check/prune; it
   cannot supply configuration, an executable, arguments or a restore target.
   Unknown and duplicate JSON fields, disabled plans, stale revisions and
   maintenance without its approved schedule are rejected. Each authorized
   run receives a separate configuration copy. The service host must reload
   approvals when installation changes, validate runtime paths and keep
   untrusted desktop files out of the privileged worker; the authorization
   parser alone does not establish those execution boundaries.

   `ServicePathLease` holds an approved local directory and its ancestors
   against deletion/renaming while a privileged operation uses the path.
   Initial handles also deny write sharing until reparse checks and terminal
   guard creation finish. Each terminal folder receives a unique zero-byte,
   hidden, delete-on-close marker that cannot be removed while held. The folder
   therefore cannot become an empty junction target; pinned child directories
   keep its ancestors nonempty. Read/write sharing is then permitted without a
   gap in protection, allowing child-file atomic saves and restic repository
   initialization. Keeping write sharing denied for the entire operation blocks
   those renames and is incorrect. The worker excludes only its own generated
   marker names from snapshots. It retains handles through engine disposal.
   This guard requires marker creation to succeed and is not yet wired into
   the production scheduler. Network/removable destinations remain unsupported
   by this unactivated service worker. See Microsoft's
   [reparse-point restrictions](https://learn.microsoft.com/en-us/windows/win32/fileio/reparse-points)
   for the nonempty-directory and name-surrogate rules.

   The hidden `--service-worker` entry connects these boundaries to the real
   orchestrator. It loads an administrator-protected installation binding,
   requires the actual noninteractive virtual service identity with Backup
   Operators membership, validates shipped code, opens the existing service
   vault, and holds approved local paths through engine disposal. A private
   file lock serializes workers across sessions. It enables only backup rights
   already assigned to that account; it cannot grant rights or register tasks.
   Applying approved settings preserves newer run and recovery outcomes.
   Environment overrides cannot redirect its configuration or scratch space.
   Backup operations hold both source and repository paths. Standalone checks
   and cleanup hold only repositories, so an unavailable original source does
   not prevent maintenance. A check explicitly configured to compare restored
   files with today's originals still protects those source paths. Network
   sources and removable/network destinations currently fail admission; they
   are not silently run without path protection. Registration, scheduling and
   desktop handoff use the integration described below. Current acceptance
   results and remaining release checks are recorded in the local Review desk.

   `BackupWindowsService` supplies the SCM host for this protected worker. Its
   authenticated run endpoint accepts only status, cancellation of the current
   run ID, or an approved backup/check/prune request. It reloads the protected
   approval for each start, rejects concurrent starts, records admission before
   replying, and keeps uncertain interrupted runs distinct from success. A
   child waits for its host's admission signal before touching a repository;
   the host first attaches it to the process-lifetime job. Service shutdown
   requests cooperative cancellation, then terminates a child that exceeds the
   shutdown allowance. The separate credential endpoint opens only the existing
   service vault. This host does not install itself, migrate credentials or
   register schedules; desktop activation remains separate.

   The service keeps the schedule itself: `BackupWindowsService` starts with
   Windows (automatic start) and runs `ScheduleLoop` with the approved
   configuration. It waits while an update, removal or settings change is
   pending, and while the owner's saved settings differ from the approved ones
   (when the service account can read them). A due plan is authorized against
   the approved revision, admitted through the Quit pause, and run by one worker
   at a time; a busy slot keeps it due. A time limit or battery stop is
   delivered to the worker like Quit's cooperative cancellation.
   `ServiceScheduledRun` serves Run now and the owner's command line. It asks
   the restricted service to run the approved operation and follows it. It
   checks every 250 ms during the first 15 seconds, then every five seconds.
   These checks renew a run-specific, monotonic 30-second lease. If the client
   disappears, the service requests cancellation within two seconds of lease
   expiry, then allows 15 seconds before terminating the worker tree. The worker
   slot stays occupied until exit is confirmed.
   Lost start replies are not replayed; uncertain results cannot become success.
   An explicit busy refusal waits before trying admission again. Terminal
   receipts are retained by run ID, so another backup starting cannot hide the
   result its predecessor's client is still waiting to collect.
   Public `--run`, `--run-all`, `--check` and `--prune` commands use
   `ServiceCommandLineRun` when the adjacent portable configuration has an
   activated service. The current Windows owner is checked before dispatch;
   the CLI sends the approved backup ID and revision, never paths or credentials.
   Its `start-cli` request shares scheduled-run pause handling and the liveness
   lease, so closing or cancelling the command cannot silently detach its worker.
   An explicit owner command may check or prune an enabled, approved backup
   without adding a maintenance schedule; automatic requests still require that
   schedule. Both routes use the approved retention settings and independently
   validate the worker's virtual-service identity. Unknown names, ambiguous names,
   disabled backups and unapplied settings fail without starting another engine.
   `ServiceScheduleRegistration` keeps the installation's protected task folder,
   which now holds only the "Apply saved settings" task (no trigger). Activation
   registers it disabled and enables it after the verified provider switch.
   Folders made by earlier versions also held one SYSTEM task per backup and a
   sign-in task for alerts; preparation, enabling and the settings task remove
   them after validating each one, and the desktop treats them as a pending
   settings change. Setting the service to start automatically also starts it;
   the owner's app starts a stopped service again when it opens. Settings →
   Administrator access explicitly enables this integration through
   `WindowsBackgroundBackupSetup`; opening Settings only inspects it.
   With the service, `AppScheduler.UpsertTask` checks the current approval.
   During activation, `ServiceLegacyTasks` archives and disables this copy's
   recognized older tasks before enabling replacements, then removes them.
   Matching requires the exact Windows owner, known task-name form and complete
   action for this executable and a known fallback helper location. A matching
   name alone is insufficient. Original cmd wrappers are compared without
   executing them. An active old task blocks completion until its operation ends;
   migration does not kill it or re-enable S4U. Original definitions remain in the
   adjacent protected control folder for inspection. Setup reports remaining
   recognized tasks as unfinished rather than claiming migration succeeded.
   Service lifecycle, native acceptance and release qualification remain separate
   checks; a visible setup button is not evidence that those checks passed.

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
   explicitly restores its normal startup behavior. Retirement removes the
   service and its task folder. Native sign-in, lock/unlock and visible
   delivery require VM acceptance.

   `ServiceRunRecovery` saves the admitted backup ID, name, operation, start time
   and failure-alert choices before the worker process starts. The first worker
   history ID is retained separately, without writing on every progress update.
   If the worker or SCM host dies before a normal outcome is saved, the next
   confirmed exit/startup creates a dated failed history row and its exact-run
   desktop alert. An explicit Stop or Quit is recorded as skipped instead.
   A normal terminal outcome already present in history wins over recovery;
   a service crash after a successful backup must not rewrite that success.
   Recovery uses the host run ID for its own fallback row and notification,
   making retries idempotent. A failed history or alert write keeps recovery
   pending and prevents a new worker from replacing the unresolved context.
   Stop still proceeds if recording its reason fails. Pending notifications
   are checked every two seconds while idle; delivered backlog alerts are
   spaced ten seconds apart.

   The desktop reads that protected binding through `LoadForOwner`, which
   verifies the installation and requires the current Windows user to match
   its owner. It does not open the service's private data directory. The
   service's own `Load` additionally validates that directory before serving
   requests. This separation lets an ordinary owner use the authenticated
   connection without granting direct access to the vault or approved settings.

   A protected `service-migration.json` grant enables the separate, authenticated
   one-time migration endpoint. `ServiceMigrationProtocol` reserves that grant
   durably before creating a vault; replay cannot replace refreshed credentials
   or recreate a missing target. A retained vault backup also blocks import.
   Verification rereads the encrypted target and returns a digest and count,
   never a full-vault export. `ServiceVaultHandoff` keeps the source transaction
   locked through import, read-only verification and synchronous activation.
   A lost import reply triggers verification rather than a repeated write.
   If setup was cancelled before activation and the original desktop vault
   subsequently changed, handoff may conditionally refresh the verified inactive
   copy. Refresh checks the previously read target digest and count while holding
   its vault lock, removes the old acceptance before replacement, retains the
   encrypted backup, and requires fresh verification. A protected migration lock
   excludes activation during refresh. An active, missing or unreadable service
   vault is never replaced through this path.
   After checking the target against its locked source snapshot, the owner
   accepts that exact digest and count. The service rereads its vault and
   publishes a private acceptance receipt without credential values or hashes.
   Elevated `ServiceActivation` requires this receipt before creating schedules.
   Before enabling them, `ServiceRepositoryPermissions` grants this service
   modify access to the approved local repository folders. It records each
   root's file identity before the first change, preserves ownership and other
   accounts' rules, and inspects descendants individually without following
   junctions or hard links. Removal deletes only this service's access rules;
   it must not restore old complete ACLs over later user changes. Replaced
   destination roots require review instead of receiving an old permission grant.
   Its durable activated binding selects the service vault before any schedule
   is enabled. Failures after that commit recover forward instead of reverting
   to old tokens. A consumed grant with no usable target needs explicit recovery;
   the source is preserved.
   `ServiceConfigurationUpdate` applies later settings with administrator
   approval. The desktop's expected configuration digest must still match
   before a protected recovery record is written. Existing work prevents the
   update from starting. A pending update blocks new service workers; the
   update stops the host, prepares disabled schedules and operation locks,
   removes obsolete repository grants, grants the new scope, and publishes
   the new approved configuration. Removing old grants before adding new ones
   preserves access when the new destination is inside the old destination.
   Only then is the record marked ready and its matching schedules enabled.
   Recovery uses the saved approved settings and never replaces newer desktop
   edits. Removing the last backup leaves no authorized work or schedules.
   The internal `--service-refresh <digest>` command starts an update;
   `--service-refresh` resumes one already recorded. Both require elevation.
   Schedule times are normalized to whole seconds before registration and
   comparison, matching Windows' persisted trigger precision. Invalid timing,
   cadence and weekly-day values are rejected before an update is recorded.
   Before each scheduled start the service compares the saved desktop settings
   with the approved settings when it can read them. Pending edits, disabled or
   deleted backups prevent the old approval from starting work. `ServiceConfigurationComparison` excludes only named presentation and
   completed-result fields, so a theme change or new history does not suspend
   schedules. Unknown new fields remain significant. The tray reports settings
   that are not applied yet explicitly.
   `ServiceSetup` now provides elevated preparation and cancellation commands.
   Preparation registers the existing portable executable, initially disabled,
   then starts its restricted virtual account on demand. Version 2 metadata is
   under `Backspace.config/windows-service/control`; private service data stays
   under `Backspace.config/windows-service/data`. No second program copy or
   ProgramData settings directory is created. Existing desktop data remains in
   its ordinary location. Setup never changes parent folders or requires a
   named folder.
   Service records are protected against replacement; the executable and
   bundled engines are not (see above). Directory ACL changes never propagate into existing settings,
   vaults and logs. The installation folder, `Backspace.config` and repository
   ancestors can be another program's current directory (Backspace itself
   starts in its own folder), and Windows holds a current directory without
   delete sharing. Those folders are opened without delete access and changed
   with `SetKernelObjectSecurity`, which updates only the folder itself.
   Folders setup creates, or deletes, use MAXIMUM_ALLOWED handles, for which
   `SetSecurityInfo` also skips propagation.
   `ServiceObjectPermissions` also pins each program file, opens reparse points
   without following them and refuses files with multiple hard links. Permission
   updates use the inspected handles, including rollback after the parent becomes
   writable, so replacing a pathname cannot redirect the permission change.
   `ServiceSetupRecovery` saves the original permissions and object identities
   in a protected journal before changing any existing ACL. It includes
   the desktop's existing `.backspace-runtime.lock` as well as the
   program files. Setup accepts only an empty regular lock file, pins it while
   protecting its permissions, and records its original identity and ACL.
   Older preparation journals without that file remain valid. Cancellation can
   therefore recover an interrupted preparation, including one that never
   registered its service. It validates the journal and every original object,
   prevents the prepared service from restarting, then removes only its own
   registration. The journal remains until every original ACL is restored;
   cleanup deletes the inspected objects through held handles. Credential
   handoff or any private service work blocks this preparation-only recovery.
   `ServiceCoordinationFiles` keeps shared operation, backup and repository
   locks in the protected control folder. Both the desktop owner and virtual
   service account can open those files, across Windows sessions, without
   permission to replace them or change their access lists. File-sharing locks
   release on process exit. Missing, redirected or unsafe lock files fail
   admission; the orchestrator never bypasses an unavailable cross-process gate.
   Preparation holds the original desktop operation lock before publishing its
   binding. An unfinished binding blocks new work until setup finishes or is
   cancelled. Clients recheck that binding after opening the old lock, closing
   the race with activation. Ordinary portable copies retain their existing
   named semaphore keys; isolated review configurations do not join service locks.
   Preparing version 2 does not authorize backup execution or register tasks.
   Credential activation is a separate elevated command. Settings coordinates
   preparation, credential transfer, activation and changed-settings approval.
   `ServiceRetirement` freezes new work and credential writes before returning
   credentials. The ordinary owner receives the latest service vault through
   the authenticated return pipe, encrypts it with that owner's DPAPI identity,
   rereads and verifies it, and holds the encrypted file through final removal.
   The service records acceptance of that exact returned file before the
   elevated helper removes its schedules, repository grants and registration.
   A durable removal record permits retry after interruption; every retry checks
   the returned file again. Missing or changed credentials prevent completion.
   Both vault copies and the original local backup remain available for recovery.
   Only after registration is gone does removal return access to retained private
   data and restore the saved original program/folder permissions. Every affected
   object is pinned and checked against the setup journal. Parents are restored
   first so Windows rebuilds child inheritance from the original parent ACL.
   The elevated helper enables its existing backup/restore privileges for these
   operations; it does not grant new privileges to the desktop account.
   Successful removal switches desktop credentials and operation locks back to
   ordinary portable mode. `ServiceRetiredHistory` retains old runs and relocates
   their transcript paths within the adjacent configuration folder. Re-enabling
   archives the completed service data under the same configuration folder and
   creates a new registration; it never replaces an existing archive. Updating
   or moving an active service still requires its separate lifecycle handling.
   The service stays beside the same portable executable and configuration; it
   does not copy the app into Program Files or move its data. It never changes
   the permissions of the program folder or its parents (see the accepted risk
   above). Backups from a network drive, which the service cannot use, run as
   the signed-in owner without VSS.
4. **Single executable.** `backrest`, `restic`, and `rclone` are bundled
   in `Assets/` at build time and extracted beside the portable executable
   on first run. No separate install step.
5. **Volume-name matching is first-class** for any local destination —
   never failed-state when an external drive is simply unmounted, always
   "skipped".
6. **No surprises in the config dir.** One `config.json`; a user can
   delete it to start over. Secrets in a separate machine-bound file so
   the JSON is shareable / diffable.
7. **Portable layout.** Everything under `Backspace.config/` next to the
   exe — see Storage locations above. Don't reach into `%APPDATA%` or
   `~/.config`.

## Invariants to protect during refactors

- The `--run` / `--run-all` code path must be usable **without** the GUI
  loaded (no `App.Current`-style references). It spins up only what it
  needs via `ServiceLocator.InitializeCore`.
- The engine pump (`BackupProgressService` + `ConnectStreamReader`) must
  consume Backrest's operation-event stream incrementally, never
  `ReadToEnd()`. Real backups produce GB of log over multi-day runs; a
  buffered read would blow heap before the first checkpoint.
- Config file reads and writes must be **atomic** (write to `.tmp`,
  rename) — a scheduled run losing config due to a power cut is
  unacceptable. Same applies to `rclone.conf`, the sidecar's PID file,
  and the per-backup history JSON.
- New S4U registration never happens, and no Windows task runs a backup. A
  missed run is caught up by `ScheduleState` (survive sleep and shutdown, run
  missed slots once later) without logging on as the user.
  See the unattended-access limitations
  in [GOTCHAS.md](GOTCHAS.md#unattended-windows-access). The task-pause policy
  controls whether work starts; it does not grant additional access.
- `ResticBackrestEngine.IsValidBackrestGuid` is the `SetConfig` safety
  net for the Backrest 1.13.0+ contract: every persisted repo guid must
  be 64-char hex AND mutually exclusive with `auto_initialize`. Don't
  loosen the validation — earlier 36-char UUID guids will fail
  `SetConfig` and the migration path in `EnsurePlanAndRepoCoreAsync` is
  the only thing that lets users recover without manually editing
  Backrest's config.
- Cross-platform code paths under `Services/Platform/`: never call a
  Windows API directly from `src/Backspace/Services/*.cs` — go through
  the `IScheduler` / `ISecretsEncryption` interface or the
  `PlatformInfo.IsWindows` guard. The csproj `Compile Remove` excludes
  `Services/Platform/Windows/` from the cross-platform TFM, so a direct
  call would compile on Windows but fail to load on macOS / Linux.

## CLI surface

- `Backspace`                              — launch GUI
- `Backspace --background`                — start in the tray only (the sign-in start entry)
- `Backspace --run <backup-name>`          — run one backup now, without the GUI
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

Settings and the local Backrest configuration also retain their previous
`.bak` and pending file when publication fails. Neither initializes a fresh
configuration if its primary is missing while that backup exists. Settings
saves retry known temporary replacement errors briefly; engine startup keeps
its existing visible, cancellable wait. Neither retries Windows errors that
may have already renamed the original file.

`MaintenanceCoordinator` keeps a read lease for the lifetime of backups, restores, verification and mounted-file engine reads. Destructive maintenance and password changes require exclusive access, including a portable-config file handle that coordinates separate Backspace processes. Cancelling an operation is not proof it stopped: restore cancellation sends the daemon Cancel request and waits for a terminal acknowledgement. An unacknowledged daemon operation blocks further repository work until restart. Repository locks are never force-removed for password rotation.

Restores pin the immutable snapshot ID selected by the user; display revision numbers never identify an already selected snapshot. Output first goes into a private staging directory. Only confirmed complete selected files are moved to the target; bracket-pattern cleanup never sweeps that target. The replacement and folder-layout options apply during publication. Duplicate flattened names are rejected before replacing any file, and linked targets or ancestors are rejected before staging. Whole-folder restores also preserve empty directories. If cancellation or a disk error occurs while publishing completed files, files already moved remain and the result reports how many were copied. Restore sessions retain their own wizard state, progress and result until dismissed.

Cleanup asks Backrest for snapshots belonging to each configured backup/source plan and forgets explicit snapshot IDs. It never deletes repository directories by their names. Removing a destination preserves unconfigured snapshots, unrelated files and reusable repository settings. Destination ownership is removed from configuration before its exclusive maintenance lease is released.

Backups and standalone maintenance refresh saved configuration after acquiring
their operation lease, before touching repositories. This includes a scheduled
process that was waiting while another process removed a backup or destination.
An unreadable configuration stops the operation. A run that finishes only some
sources or destinations reports incomplete coverage; it neither advances the
last complete-backup timestamp nor sends a successful monitoring ping.

Startup cleanup trusts persisted process identity records (owner PID plus start time; child PID plus start time and executable) and leaves unknown/live owners alone. Windows job ownership covers the daemon and inherited child processes. There is no process-name or bundled-path sweep.

On Windows, executable identity comes from `QueryFullProcessImageNameW` on
the process handle. Reading `Process.MainModule` immediately after launch
can return no module before the loader runs, leaving the recovery record
unwritten. The lookup never substitutes the requested executable path for
the actual process identity; a failed OS lookup remains unknown.

On Windows, the platform builder uses Avalonia's RedirectionSurface composition
path while retaining its hardware-rendering fallback order. Avalonia 12.1.3 can
pause its compositor render loop when there is nothing to draw; Avalonia 11 kept
ticking while idle. Native window-host measurements recorded lower idle CPU after
the upgrade. They use an isolated configuration and do not guarantee zero CPU on
every system. The test harness uses the matching Avalonia 12 headless packages
and xUnit v3. DataGrid has its own package release, 12.1.2.

Closing to the tray destroys the native main window and releases its visual
tree, icon resources and view subscriptions. `TrayWindowHost` retains the main
view model so an active restore, editor draft and selected page survive.
Opening from the tray, a forwarded launch or a notification creates a new
window around that same model. Explicit Quit still uses the operation drain
and disposes the model only when the application exits. A paused or running
operation must never be cancelled merely because the user closed the window.

The main window refreshes external configuration and run history on activation
and when entering Backups or Activity. A fingerprint check and transaction run
off the UI thread; no idle polling or watcher loop is introduced. Unsaved editor
drafts remain separate, and saving them preserves newer runtime results.
