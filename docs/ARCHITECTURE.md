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
file takes its folder's permissions; Backspace changes none. The engine's
port is chosen free and released just before the engine starts on it, so
another account could take it in between: before the first password is sent,
`LoopbackPortOwner` confirms that the process listening on that port is the
engine Backspace started, and startup stops otherwise (U152).

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

`SystemBackupService` reads the owner's `config.json` as saved, every minute
and before each run (`SystemServiceRuns`): there is no approval record and no
settings task. Whoever can change those settings can already replace
`Backspace.exe`, which the service runs as Windows; the user guide says so.
Backups with custom engine arguments, or network shares without saved
credentials, stay with the tray app. The service enables the backup and
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
`Backspace.config\service`. A service not yet converted to the vault still
holds the sign-ins in its earlier vault: removal then freezes it and takes
them back first (`ServiceVaultReturn`).

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
owner's `start-cli` request when the service runs the backup; the card follows
the run through the service's activity. Otherwise the desktop runs the backup
itself. A network share runs in the service when its destination has a
network path and a saved user name and password
(`NetworkSharePath.ServiceCanReach`): before the run the worker connects to
the share with them (`ServiceNetworkShares`, WNetAddConnection2) and
disconnects after. External drives run in the service; an unplugged drive is
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
│   │                                  backrest(.exe) + restic(.exe) + rclone(.exe)
│   │                                  (gitignored, fetched at build)
│   ├── FetchBackrest.targets        — MSBuild target that downloads the engines on demand
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
`verify` (scratch for restore checks), `locks` (operation locks shared with
the background service), `keys` (this computer's copies of the vault key) and
`engine\<16 hex>` (the engine's working files, below). All are recreated when
missing. Optional system changes are listed in the user guide: the background
service (administrator access), the sign-in start entry, and the WinFsp driver.

- `<exe-dir>/Backspace.config/config.json` — app settings, backups, destinations
- `<exe-dir>/Backspace.config/vault.bin` — the saved credentials (storage passwords, cloud sign-ins, API keys), one file for the app and the background service. See "Saved passwords (the vault)" below.
- `<exe-dir>/Backspace.config/vault-conversion.json` — only while the one-time move from the earlier files is unfinished: its stage, vault id, count and digest, never a value.
- `<exe-dir>/Backspace.config/secrets.bin` — the earlier credential file (Windows-protected for one account on one computer). Read only by the one-time move, and deleted after the first successful run on `vault.bin`.
- `<exe-dir>/Backspace.config/pending-removals.json` — removed backups whose stored copies are still to be erased; absent when nothing is waiting
- `%LOCALAPPDATA%/Backspace/engine/<16 hex of SHA-256 of the upper-case configuration folder>/` — this computer's engine working files (`EngineWorkPaths`): `backrest/` (the GUI-owned Backrest data directory: its config.json holds every repository password in readable form, plus its sign-in files, operation database, operation logs and mount cache), `rclone-remotes/` (one generated remote per cloud destination, with its token), `rclone.conf`, `engine-runs/` (isolated directories for scheduled runs and elevated VSS helpers, so one process cannot shut down another's daemon), `filters/<plan id>.txt` (each plan's exclude file: the backup's Filters text, written unchanged before every use of the plan, U151) and, for the background service, `scratch/`. Never in the synced folder; rebuilt at start from the settings and the vault (seed configuration, sign-in, plans and repositories through `EnsurePlanAndRepo`, rclone remotes). Backrest's operation database starts empty on a new computer; run history is unaffected (below). The service's folder is named after `Backspace.config\service`. `BACKSPACE_MACHINE_ROOT` overrides `%LOCALAPPDATA%\Backspace` (tests; the service sets it to the owner's folder); a test configuration outside an executable's folder without it uses `<config>\engine`.
- `<exe-dir>/Backspace.config/test-restores/` — saved test-restore plans, completed batch results and sample history. Temporary recovered files live separately under `verify/`.
- `<exe-dir>/Backspace.config/logs/app/` — Serilog daily-rolling Backspace logs
- `<exe-dir>/Backspace.config/logs/engine/<backup-name>/` — per-run engine transcripts (restic stdout via Backrest)
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

**The one-time move** (`VaultConversion`, `WindowsVaultConversionHost`) of an
install from before the vault: at start, with the earlier `secrets.bin` and no
`vault.bin`, the app asks once to choose a password. It freezes the service
and takes its earlier vault (`service\secrets.bin`) back over the return pipe
(`ServiceVaultReturnProtocol`); the service's copy wins and the desktop's
fills the gaps; newer tokens from every old rclone file are taken
(`IsNewerToken`). It writes `vault.bin.tmp`, reopens it with the password,
compares count and digest, publishes it without replacing anything and saves
the key copy. The service then checks that `vault.bin` holds every entry of
its earlier vault before keeping its key; it is thawed. The old engine folders
move to this computer under each sidecar owner lock; the old rclone files,
engine runs and service scratch are deleted. The earlier `secrets.bin` files
go after the first successful run on `vault.bin`. Each stage is recorded in
`vault-conversion.json` and resumes at the next start; nothing old is deleted
before `vault.bin` and the service's confirmation. This code exists only for
installs made before the vault and can be removed once the owner's install
is converted.

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
   (`ServiceHostRequest`): activity, start, cancel, heartbeat, measure, freeze,
   thaw and `unlock` (the vault key, U153). The return pipe, created only while
   an unconverted service's earlier vault goes back to the owner, also carries
   that return's random ID, which only the owner receives.

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
   updates while still displaying new lines. Completed runs show their saved log
   file; the final live tail is not treated as an entire saved transcript.

   The worker (`--service-worker-owned` / `--service-worker-cli`,
   `ServiceApprovedWorker`) runs one operation the service admitted: it checks
   that it runs as the installed service, opens the portable vault with the
   service's key copy (a locked vault stops it before any engine starts, with
   the reason in the run's history), connects network shares with their saved
   credentials, sets its configuration and scratch to the service's folders
   (scratch on this computer, U153) and runs the ordinary orchestrator. Its
   report (`last-service-run.json`) names the vault it used.

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

4. **Single executable.** `Backspace.exe` carries the pinned versions and
   SHA-256 fingerprints of `backrest`, `restic`, and `rclone`; on first
   start it downloads them from those projects' releases into
   `%LOCALAPPDATA%\Backspace\bin` and checks them. No separate install step.
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
- A backup's Filters text is restic's exclude file (U151). The engine writes it
  unchanged (`ResticBackrestEngine.WriteExcludeFile`) and the plan passes it
  with `--iexclude-file` on Windows, `--exclude-file` elsewhere
  (`FilterTranslator.ExcludeFileFlag`); the plan's `excludes`/`iexcludes`
  stay empty. Never rewrite, bind or append rules on the way: restic expands
  `$NAME` in a file but not in an argument, and the owner's rules must work
  with restic alone. Count, Simulate, the size estimates, the change listing
  and test-restore completeness read the same text with
  `FilterTranslator.CompileRules`/`Evaluate`, restic's matching on full paths
  (`C:/…`, `?:` for any drive, last match wins); E57 checks them against
  `restic backup --dry-run`. Duplicacy lines are converted once
  (`DuplicacyFilterConverter`: when settings load and at import) and saved; no
  code path reads them at run time. Recommended rules
  (`RecommendedFilters`) are written in the same syntax, `$` as `$$`, and the
  essential rules are a visible block at the end of each new backup's text.
- A destination missing its sign-in, S3 keys or storage password
  (`DestinationReadiness`, typically imported from Duplicacy) never reaches
  restic: Run now opens it for setup, and a run's cell fails at once with the
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
window around that same model. Explicit Quit still uses the operation drain
and disposes the model only when the application exits. A paused or running
operation must never be cancelled merely because the user closed the window.

The main window refreshes external configuration and run history on activation
and when entering Backups or Activity. A fingerprint check and transaction run
off the UI thread; no idle polling or watcher loop is introduced. Unsaved editor
drafts remain separate, and saving them preserves newer runtime results.
