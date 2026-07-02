# ARCHITECTURE.md — code map for contributors

How Backspace is wired, where state lives, and the invariants a refactor
must preserve. Pair with [AGENT.md](../AGENT.md) for working conventions
and [GOTCHAS.md](GOTCHAS.md) for upstream-specific traps.

## High-level shape

```
Program.cs
 ├── if (--run <name> / --run-all):  UnattendedRunner.RunAsync(...)   [headless]
 ├── if (--migrate <path>):          ConfigMigrator.MigrateFrom(...)  [one-shot]
 └── else:                           AppBuilder ... StartWithClassicDesktopLifetime(...)  [GUI]

Avalonia GUI
 ├── MainWindow
 │    ├── Sidebar nav → Backups | Destinations | Restore | Logs | Settings
 │    └── Content host swaps view based on NavItem (see MainWindowViewModel)
 └── Modal dialogs: OnboardingWindow, BackupEditorWindow,
     DestinationEditorWindow, DestructiveConfirmWindow,
     ChangeStoragePasswordWindow, GeneratedPasswordWindow,
     SourceTreePickerWindow, ErasingProgressWindow

Services (singletons, accessed via ServiceLocator)
 └── src/Backspace/Services/  — ~60 classes; every one has a top-of-file
                                XML doc comment explaining its role.
                                Read the comment; don't rely on a list here.
                                Platform-specific code lives under
                                Services/Platform/{Windows,Unix}/.
```

The headless code path (`--run` / `--run-all`) must work **without** the
GUI loaded — no `App.Current` references, no Avalonia dispatcher
assumptions. `ServiceLocator.InitializeCore` brings up only the services
a backup needs.

## File layout

```
Backspace/                          ← repo root (folder name may vary locally)
├── README.md, AGENT.md, DESIGN.md, LICENSE, CONTRIBUTING.md, .gitignore
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
│   ├── Assets/                      — embedded icons, fonts (Fraunces, Inter, JetBrains Mono),
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
- `<exe-dir>/Backspace.config/backrest/` — Backrest's own data dir (config DB, operation history, restic logs)
- `<exe-dir>/Backspace.config/rclone.conf` — generated rclone remote config (cloud OAuth tokens land here)
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
   `Forget` / `DoRepoTask` / `RunCommand`. Restic is downstream of
   Backrest; we never shell it out directly except for one-off helper
   commands (e.g. change storage password) where Backrest has no API.
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
- `Backspace --migrate <path>`             — one-off import from an existing Duplicacy / duplicacy-util setup (single repo or parent-of-many)
- `Backspace --version`
- `Backspace --help | -h | /?`

On Windows the binary is `Backspace.exe`; on Unix it's `Backspace`.

Build and test commands live in [README.md § Building from source](../README.md#building-from-source) and
[TESTING.md](TESTING.md). They are not duplicated here.
