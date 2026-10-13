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

## Windows release updates

`WindowsUpdateViewModel` owns the Settings update controls and a one-shot timer for the chosen check frequency. Checks use GitHub's latest stable release endpoint; downloads and restarts require a click. `WindowsUpdatePackage` verifies the official asset URL, exact size, GitHub SHA-256 digest, safe archive entries and application version before preparing a complete bundle under `.backspace-updates` beside the executable.

`WindowsUpdateRuntime` shares a process-lifetime file lease with GUI, scheduled and helper runs. Replacement requires its exclusive lease after the GUI closes and owned engines stop. The application operation gate also prevents new work during a requested restart. Replacement journals the old and new program files and rolls back failures; configuration is outside that file list. A recovery launcher can restore the previous bundle. A successful process start is not proof of a healthy new GUI: native launch/crash behavior remains an acceptance-test requirement. The immediately preceding bundle is retained. Older completed update records can be removed after the next successful replacement; unknown or unfinished records are preserved.

Only portable Windows x64 single-file builds in writable, non-linked folders support installation. Other builds keep the manual release-page action. The internal `--recover-update` path runs before engine/configuration initialization.

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
│   ├── Backspace.csproj             — multi-targets net10.0-windows10.0.19041.0 + net10.0
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
│   └── BuildStub.targets            — Windows-only: publishes the fallback stub
├── src/Backspace.Stub/              — tiny "main exe missing" fallback (Windows scheduled-task safety)
└── tests/Backspace.Tests/           — see TESTING.md for the layout + harness
```

## Storage locations (on the user's machine)

Everything is **portable**: the app keeps all state in one folder next to
the executable. There is **no** install footprint under `%APPDATA%` or
`%LOCALAPPDATA%`.

- `<exe-dir>/Backspace.config/config.json` — app settings, backups, destinations
- `<exe-dir>/Backspace.config/secrets.bin` — encrypted secrets (API keys, OAuth tokens, storage passwords)
- `<exe-dir>/Backspace.config/backrest/` — the GUI-owned Backrest data directory. Scheduled runs and elevated VSS helpers use isolated directories under `engine-runs/` so one process cannot shut down another process's daemon.
- `<exe-dir>/Backspace.config/rclone-remotes/` — one generated remote configuration per cloud destination; refreshing one account cannot erase another.
- `<exe-dir>/Backspace.config/logs/app/` — Serilog daily-rolling Backspace logs
- `<exe-dir>/Backspace.config/logs/engine/<backup-name>/` — per-run engine transcripts (restic stdout via Backrest)
- The actual restic repositories live wherever the user pointed each
  destination (a local folder, an external drive, a cloud bucket via
  rclone). We don't reach into them.

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
   Backrest's operation history. Password changes and local snapshot-tree
   fast paths also invoke bundled restic directly.
2. **All destructive actions are type-to-confirm with the single token
   "OK"** (case-insensitive). Erase-destination, prune-to-latest,
   remove-backup, remove-destination, and restore-overwrite all gate on
   the user typing OK into the destructive-confirm dialog. The subtitle
   copy is what tells the user what they're about to destroy — the
   gating word is intentionally the same across actions so muscle memory
   doesn't have to remember a per-action keyword. No "are you sure"
   button.
3. **No always-on service.** The platform scheduler is the cron. The
   only times our process runs are: user has GUI open, or the scheduler
   invoked us with `--run`. (The Backrest sidecar is started by
   Backspace and dies when we do — `WindowsKillOnExitJobObject` enforces
   it on Windows so the sidecar can't outlive a Backspace crash.)
   Windows task actions use stock Windows PowerShell with an encoded script
   containing literal paths and arguments. This adds a short shell startup
   cost per scheduled run and requires Windows PowerShell to remain available.
   The wrapper waits for the app, reports its exit code, and launches the
   fallback stub if the app was moved; no profile or execution-policy override
   is used. Free-form backup names are passed as native argument data.
   The tray's schedule badge reads Windows Task Scheduler rather than inferring
   readiness from saved settings. It checks enabled backup tasks, their account,
   action, timing and execution settings. Paused, off, attention and unverified
   states remain distinct. Checks run on startup, focus, menu opening, task or
   configuration changes, and once when a snooze expires; there is no recurring
   polling timer. A ready badge records the check time. It does not guarantee
   that drives, networks or cloud credentials will be available at the next run.
4. **Single executable.** `backrest`, `restic`, and `rclone` are bundled
   in `Assets/` at build time and extracted to the portable config dir
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
- Tasks registered with Windows Task Scheduler must use S4U logon and
  `StartWhenAvailable` (survive sleep, run missed tasks later).
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
- `Backspace --run <backup-name>`          — unattended single-backup run (scheduler entry point)
- `Backspace --run-all`                    — unattended, all enabled backups
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

On Windows, the platform builder uses Avalonia's RedirectionSurface composition
path while retaining its hardware-rendering fallback order. Avalonia 12.1.3 can
pause its compositor render loop when there is nothing to draw; Avalonia 11 kept
ticking while idle. Native window-host measurements recorded lower idle CPU after
the upgrade. They use an isolated configuration and do not guarantee zero CPU on
every system. The test harness uses the matching Avalonia 12 headless packages
and xUnit v3. DataGrid has its own package release, 12.1.2.

The main window refreshes external configuration and run history on activation
and when entering Backups or Activity. A fingerprint check and transaction run
off the UI thread; no idle polling or watcher loop is introduced. Unsaved editor
drafts remain separate, and saving them preserves newer runtime results.
