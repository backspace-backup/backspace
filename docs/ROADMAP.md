# ROADMAP.md — work tracker

This file is the **source of truth** for in-flight work. Pair with
[GOTCHAS.md](GOTCHAS.md) for the upstream-driven limitations the code
already works around, and [AGENTS.md § The ROADMAP workflow](../AGENTS.md#the-roadmap-workflow-source-of-truth)
for the workflow rules an agent must follow before touching this file.

## Actively working

**2026-10-02 review queue:** Open the local `Launch-Audit-Dashboard.bat` launcher or [audit overview](../audit-dashboard/evidence/audit-overview.html). Inspect the P0 safety findings first. The referenced Word document is missing; supply its location or approve discarding that request. OneDrive/Google app registration, real-account checks, reboot and idle measurements remain open. Validation requests from June/July are stale and need explicit renewed review. Checkbox decisions are recorded locally and reconciled into this roadmap when the user asks to review them; no items are removed automatically.

### P0: rclone orphan-safe kill

- **Raised:** 2026-06-12 (audit of BACKSPACE_PORT_DESIGN.md § 6.3)
- **Type:** reliability
- **Status:** Open — reopened by the 2026-10-02 audit
- **Context:** Backrest spawns `rclone.exe` on demand. If Backrest
  itself crashes (not Backspace), the `KillOnExitJobObject` we
  attach to Backrest tears down rclone via job inheritance — but a
  hard kill of the rclone process tree under load is timing-
  sensitive. The original duplimate had a real-user-reported bug
  here: rclone left running for hours after a GUI crash holding a
  4G connection.
- **Plan:** Backspace-side scanner. New service `RcloneOrphanReaper`
  enumerates `rclone` processes whose main module is our bundled
  binary AND whose start time precedes our own process. Both
  guards must hit, so other rclone installs on PATH stay
  untouched. Called once early in `Program.Main` BEFORE
  Backrest sidecar starts, so the new Backrest's rclone children
  (started AFTER us) are outside the filter by definition.
- **Historical proof:** Superseded by the current audit evidence below.
- **Known gap (per AGENTS.md rule 7):** No E2E test yet. A realistic
  scenario would spawn a fake rclone.exe pointing at our path,
  start it before launching Backspace, run the reaper, and assert
  the process is gone. Filed to the E2E revamp queue.

- **2026-10-02 audit:** B005: A Windows kill-on-exit job object covers the Backrest process tree. The startup orphan sweep does not track rclone ownership: it kills any rclone at the bundled executable path which started before the current Backspace process. An active older Backspace run or an independent user command using that binary can match. Program calls the sweep before GUI single-instance handling and for headless launches too.
- **Proof — open and look:** [Audit overview](../audit-dashboard/evidence/audit-overview.html) — current findings, reproductions and limitations.
- **I need from you:** Review the corresponding local dashboard item and explicitly accept it or describe the remaining work.

### P0: Bracket-name restore cleanup deletes unrelated pre-existing target files

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Open
- **Context:** Audit finding A008. When selected include paths contain brackets and expectedLeaves is supplied, RestoreFilesAsync recursively sweeps the entire target and deletes every file outside the selected set. It does not distinguish files produced by this restore from files that already existed. Restoring a bracket-named file into the original source or any occupied folder can therefore delete unrelated user data.
- **Audit finding:** When selected include paths contain brackets and expectedLeaves is supplied, RestoreFilesAsync recursively sweeps the entire target and deletes every file outside the selected set. It does not distinguish files produced by this restore from files that already existed. Restoring a bracket-named file into the original source or any occupied folder can therefore delete unrelated user data.
- **Remaining:** Restore wildcard matches into a separate empty staging directory, validate the exact requested paths there, and move only requested output into the destination. Never sweep an occupied user target. Add preservation tests for unrelated pre-existing files, same-length wildcard siblings and cancelled restores.
- **Proof — open and look:** [Fresh safe reproduction: unrelated file deleted](../audit-dashboard/evidence/restore-safety-probes.html) — The first probe lists two files before cleanup and only the selected bracket-named file afterward; no personal files or real repositories were used.
- **Limitations / adversarial review:** Confirmed by invoking the actual private cleanup method against two audit-only temporary files; the selected file survived and the unrelated pre-existing file was deleted. Full real-engine original-source restore was deliberately not attempted. The existing E18 fixture has no bracket-name case, so its green result does not cover this defect.
- **I need from you:** Inspect A008 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P0: Restore wizard doesn't offer a revision picker (regression vs Duplimate) — Awaiting validation

- **Raised:** 2026-06-12
- **Type:** bug (feature regression vs Duplimate)
- **Status:** Awaiting validation — fix shipped, scenario screenshot
  renders the picker rows correctly.
- **Context:** User reported: "Bring Files back" doesn't even offer
  to pick the revision. Duplimate shipped a revision picker step.
- **Root cause + fix (short):** The picker's `IsVisible` binding
  depended on a computed property that wasn't auto-notified when
  the underlying collection changed. Hooked `CollectionChanged`
  in the VM so the notification fires automatically on every
  mutation.
- **Historical proof:** Superseded by the current audit evidence below.
- **I need from you:**
  - On the fresh dist, click "Restore files" on a backup with
    multiple revisions. Note that your "multi-2-t3xjwt" backup has
    TWO source paths (`C:\ntws` + `D:\Antigravity\Shared`), so the
    wizard's first step is a checkbox source picker:
    - If you tick just ONE source → wizard lands on a revision
      picker (the single-source path my fix covers). Should show
      every revision and let you pick an older one.
    - If you tick BOTH sources → wizard lands on a read-only
      "Confirm what you're restoring" summary instead, because the
      multi-source flow currently restores latest-of-each by
      design. No per-source revision picker today — that's a
      feature gap if you wanted it. Tell me if so and I'll add
      it as a separate ROADMAP entry.
  - Confirm which of those two paths you were on when you saw "no
    picker". If single-source and STILL no picker, drop a
    screenshot + the latest `dist\Backspace.config\logs\app\app-<date>.log`.

- **2026-10-02 audit:** R005: This item was already awaiting user validation in the interrupted roadmap. The current non-cloud run regenerated its scenario evidence where available; the old completion claim is not treated as acceptance.
- **Proof — open and look:** [Fresh scenario screenshot](../audit-dashboard/evidence/unit-visual/S14-restore-single-source-revisions/screenshots/01-revisions-loading.png) — Inspect the displayed controls and text for this roadmap item. Synthetic scenario data; not a live cloud or system-level test.; [Fresh scenario screenshot](../audit-dashboard/evidence/unit-visual/S14-restore-single-source-revisions/screenshots/02-revisions-empty-hint.png) — Inspect the displayed controls and text for this roadmap item. Synthetic scenario data; not a live cloud or system-level test.; [Fresh test results and their limits](../audit-dashboard/evidence/test-report.html) — 808/809 unit/visual and 37/39 local E2E passed. Open failed cases, focused rerun, test filters, screenshots and complete hash checks.
- **I need from you:** Review the corresponding local dashboard item and explicitly accept it or describe the remaining work.

### P0: E2E revamp pass — all actionable coverage-matrix rows covered — Awaiting validation

- **Raised:** 2026-06-12
- **Type:** test coverage
- **Status:** Open — reopened by the 2026-10-02 audit
  uncovered gap" status no longer applies to any row.
- **What it does (short):** Closes the unit-tests-prove-algorithm
  pattern by adding scenario coverage for every user-visible
  action the matrix listed. Also cleans up the layout: scenarios
  that drive real Backrest/restic round-trips live under
  `Scenarios/E2E/` with the `Exx` prefix; scenarios that drive
  VM + view + screenshot live at the loose `Scenarios/` level
  with the `Sxx` prefix. Previously the 17 new tests from earlier
  this session were misfiled as `Exx` despite being VM-only —
  renamed to `S28`–`S44` to match the project's own README convention.
- **What's covered now (matrix delta):**
  - Skipped-run alerts visibility, Reset grace period (S28).
  - Custom-block exclusion markers (S30).
  - Estimated savings label states (S34).
  - Headless `--run` / `--run-all` (S35).
  - Battery / network preconditions: skip-on-battery,
    stop-on-battery, require-network, metered-network watchdog,
    catch-up missed runs (S36 — 5 matrix rows).
  - Cancel a running restore (S37).
  - Browse snapshot in-app (S38).
  - Standalone prune + check cadences (S39).
  - Filter simulation pane (S40).
  - Destination + backup CRUD: edit dest, delete dest, delete
    backup (S41).
  - Onboarding: apply recommended exclusions + Easy→Advanced
    mid-wizard switch (S42).
  - Revision picker visibility (S44).
  - Settings toggle persistence: 9 notification gates + retry
    cascade ordering (S45 — new).
  - Restore folder → minimal cover (S47 — new).
  - Notifications wiring: 12 dispatch combos + topmost-failure
    path (S48 — new).
- **What's intentionally deferred and why:** Cloud destinations
  needing real creds (S3/Wasabi/R2), Healthchecks/email
  delivery (interactive HTTP/SMTP), Task Scheduler COM-interop
  (unit-covered separately), VSS shadow-copy (Windows
  infrastructure), `--migrate <duplicacy>` (needs sample config),
  external-drive volume-name matching (needs removable media
  simulation), restore over slow / bandwidth-limited network
  (no network-shaping infra in test). Marked ⛔ in the matrix
  with the reason next to each row, not silent ❌.
- **Historical proof:** Superseded by the current audit evidence below.
- **I need from you:**
  - Open the linked TESTING.md matrix, scan the right-hand
    "Status" column — confirm every row reads ✅ or ⛔, no
    ❌ left. If you'd like a specific deferred row pulled into
    coverage (e.g. you have S3 creds and want E07-shape S3
    coverage), name it and I'll add it.

- **2026-10-02 audit:** R006: The coverage matrix mixes unit, seeded-view and real-engine coverage. Fresh safety probes found defects behind green scenario claims; some scenarios report PASS even when the overall test fails.
- **Proof — open and look:** [Fresh test results and their limits](../audit-dashboard/evidence/test-report.html) — 808/809 unit/visual and 37/39 local E2E passed. Open failed cases, focused rerun, test filters, screenshots and complete hash checks.
- **I need from you:** Review the corresponding local dashboard item and explicitly accept it or describe the remaining work.

### P0: Investigate "why do we deviate from Backrest's native restore RPC?"

- **Raised:** 2026-06-12 (user feedback on the folder-collapse review)
- **Type:** architecture review
- **Status:** Open — reopened by the 2026-10-02 audit
- **Context:** Backspace's "thin wrapper" promise (see AGENTS.md design
  principle 1: "Backrest owns its own state") is straining at the
  restore boundary. We currently:
    1. Bypass `/v1.Backrest/Restore` in favour of `/v1.Backrest/RunCommand`
       carrying a raw `restic restore` invocation, because of the
       Bug-1 / Bug-2 bracket + ACL issues solved by the
       `snapshotID:subfolder` syntax.
    2. Maintain our own per-file `Files` list in
       `RestoreRequest` + `RestoreOutcome`, walk every leaf after
       restic returns to confirm landing, and run a per-failed-file
       retry cascade. Backrest's own restore RPC offers OVERALL
       progress (total files / bytes / restored counts) but no
       per-file callback shape.
  The user's question: why do we deviate, and is it worth it? "If a
  tradeoff needs to be decided, ALWAYS ask me." So this entry first
  inventories the deviations, then surfaces the tradeoff explicitly.
- **Plan:**
  1. Document EVERY current deviation from Backrest's native restore
     surface (this entry, body below as findings come in).
  2. Quantify the cost of each: how many extra RPCs / how much extra
     code / how much extra failure surface.
  3. For each, ask the user to decide: keep / narrow / drop.
  4. Apply the decision.
- **Findings (2026-06-12):**
  - **`/v1.Backrest/Restore` request shape** (from
    [backrest/proto/v1/service.proto](https://github.com/garethgeorge/backrest/blob/main/proto/v1/service.proto)):
    `plan_id`, `repo_id`, `snapshot_id`, `path` (single string),
    `target`. Returns `google.protobuf.Empty`. **One path per call.**
  - **Progress event** (`RestoreProgressEntry` in
    [backrest/proto/v1/restic.proto](https://github.com/garethgeorge/backrest/blob/main/proto/v1/restic.proto)):
    overall numbers only — `total_files`, `files_restored`,
    `total_bytes`, `bytes_restored`, `percent_done`. **No per-file
    callback shape.**
  - **Failure granularity:** Backrest reports the operation's
    terminal status (SUCCESS / WARNING / ERROR / CANCELLED) with a
    display message; restic's per-file failure messages land in the
    op log stream. **No structured per-file failure list.**
  - **Our current deviation surface:**
    1. We bypass `/v1.Backrest/Restore` for `/v1.Backrest/RunCommand`
       so we can use the `snapshotID:subfolder` literal-path syntax
       (Bug-1/Bug-2 workaround; the bracket half retires only when
       restic #21872 ships — Backrest #1254, merged 2026-06-30, fixes
       only Backrest's own Restore RPC, which we don't use).
    2. We accumulate the user's selection (folders + files) into one
       call. Backrest natively wants ONE path per call — multi-pick
       would be N RPCs.
    3. We synthesize a per-file outcome map by walking
       `request.Files` after restic returns and checking
       `File.Exists`. Backrest doesn't do this.
    4. We run a per-failed-file retry cascade in
       `RestoreEngine.RetryOneAsync`. Backrest doesn't do this.
- **Decision (2026-06-12):** Drop the per-file tracking layer to
  match Backrest's native overall-progress semantics. The user picked
  this when shown the three options (keep / narrow-with-stdout-parse
  / drop entirely). Refactor shipped in commit `0bd0d62`.
- **Historical proof:** Superseded by the current audit evidence below.
- **Known gap (per AGENTS.md rule 7):** the engine returns
  approximate FilesRestored numbers — assumes TotalFiles on
  Success, 0 on non-Success. Parsing restic's `--json` output for
  exact files_restored / bytes_restored is queued as a follow-up
  in this same entry: add `--json` to the restic invocation, fetch
  the operation's logs via `/v1.Backrest/GetLogs`, find the final
  `summary` line, extract authoritative numbers. Until then the
  UI percentages are coarse but never WRONG (Success → 100%,
  Warning → "some files failed — see log", Error → 0%).

- **2026-10-02 audit:** R007: The raw RunCommand restore path remains. Fresh probes reproduce wrong terminal-status handling and missing daemon cancellation; five-minute timeout and unsafe cleanup also need repair before this refactor can be accepted.
- **Proof — open and look:** [Fresh test results and their limits](../audit-dashboard/evidence/test-report.html) — 808/809 unit/visual and 37/39 local E2E passed. Open failed cases, focused rerun, test filters, screenshots and complete hash checks.
- **I need from you:** Review the corresponding local dashboard item and explicitly accept it or describe the remaining work.

### P0: Restore enumerates every leaf when a folder is selected

- **Raised:** 2026-06-11
- **Type:** bug + design
- **Status:** Awaiting validation
- **Context:** User restored 2 folders containing ~2400 files. Engine
  log showed `bulk restore of 2397 file(s)` and the restic invocation
  contained 2397 individual `--include /path/to/file` flags. The
  `RestoreViewModel.SelectedPaths` projection (line 179) reads from a
  flat `AllFiles` collection — when the picker UI ticks a folder, every
  descendant leaf flips `IsSelected = true` and the request carries
  every leaf individually rather than the folder path.
- **Plan:** Added `RestoreSelectionMinimizer` service that walks the
  snapshot's file tree, marks selected leaves, and promotes any
  "all descendants selected" subtree to its parent path. Wired into
  `RestoreViewModel` via `RestoreRequest.IncludePatterns` (new
  optional field — leaves the existing `Files` list intact so
  per-file accounting still tracks every leaf). Engine consumes
  `IncludePatterns` for the restic `--include-file` body; falls
  back to `Files` when null (legacy callers).
- **Historical proof:** Superseded by the current audit evidence below.

- **2026-10-02 audit:** R008: This item was already awaiting user validation in the interrupted roadmap. The current non-cloud run regenerated its scenario evidence where available; the old completion claim is not treated as acceptance.
- **Proof — open and look:** [Fresh test results and their limits](../audit-dashboard/evidence/test-report.html) — 808/809 unit/visual and 37/39 local E2E passed. Open failed cases, focused rerun, test filters, screenshots and complete hash checks.
- **I need from you:** Review the corresponding local dashboard item and explicitly accept it or describe the remaining work.

### P0: Restore crashes with "filename or extension is too long" on large file sets

- **Raised:** 2026-06-11
- **Type:** bug (regression from snap:subfolder refactor)
- **Status:** Open — reopened by the 2026-10-02 audit
- **Context:** Windows' CreateProcess command-line ceiling is ~32 KB.
  When `RestoreFilesAsync` built one `--include` flag per requested
  file, a real-world restore of ~2400 files produced ~96 KB of args and
  failed to launch restic. Error: `fork/exec ...\restic.exe: The
  filename or extension is too long.` Surfaced from production logs the
  user shared; E2E suite missed it because the largest `FixtureSize`
  (Large) caps at ~60 files per source — well under the threshold.
- **Plan:** Switched `RestoreFilesAsync` from N per-file `--include`
  flags to one `--include-file <tempfile>`. Patterns get written to a
  temp file under `Path.GetTempPath()` and cleaned up in `finally`.
  Same matching semantics (restic uses the same glob engine for both
  flag forms), so the bracket-fallback substitution and post-restore
  sweep still apply. Added `FixtureSize.Huge` (~8000 files, ~2 GB)
  and the `BACKSPACE_E2E_SCALE=huge` env-var override for pre-push
  scale validation. Regression test `E34_RestoreLongIncludeList`
  forces 1500 files into a single restore request.
- **Historical proof:** Superseded by the current audit evidence below.

- **2026-10-02 audit:** B021: Restore writes patterns into an --include-file, avoiding the Windows command-line length limit. E34 covers 1,500 files. A Huge fixture is defined at roughly 8,000 files and 2 GiB, and BACKSPACE_E2E_SCALE=huge has a resolver, but no scenario calls ResolveSize; E01 assigns the requested size directly and the other E2Es mostly build fixed-size fixtures.
- **Proof — open and look:** [Historical 1,500-file restore validation](../audit-dashboard/evidence/E34-restore-long-include-list-historical.html) — Earlier validation reports all 1,500 paths present with matching size/metadata and 15 sampled files byte-identical; this is not a newly run scale test.; [Fresh test results and their limits](../audit-dashboard/evidence/test-report.html) — 808/809 unit/visual and 37/39 local E2E passed. Open failed cases, focused rerun, test filters, screenshots and complete hash checks.
- **I need from you:** Review the corresponding local dashboard item and explicitly accept it or describe the remaining work.

### P0: Mount fails for the user's basic dist-folder backup

- **Raised:** 2026-06-11
- **Type:** bug + architectural decision needed
- **Status:** Open — reopened by the 2026-10-02 audit
- **Context:** User ran a simple backup against the bundled `dist/`
  config and clicked Mount; the UI reported "Couldn't mount the
  snapshot." `E04_SnapshotMountWindows` was treating any `MountResult`
  failure as a soft skip — recording the same `exit code 2` failure
  the user hit, but as `⊘ SKIP` in `SUMMARY.md`. False-green by
  construction.
- **Root cause** (diagnosed via `MountRepro.NetUseReproducesUserFailure`):
  Windows' WebDAV redirector silently REJECTS custom (non-80/443) ports.
  Every UNC shape (`\\127.0.0.1@PORT\`, `\\localhost@PORT\`,
  `\\…\DavWWWRoot\`, `http://127.0.0.1:PORT/`) returns
  `System error 67 — The network name cannot be found`. The
  diagnostic test added a TCP-accept probe listener on a parallel
  port — `net use` recorded **zero connect attempts** on the probe
  port, confirming the redirector rejects the URL outright without
  even dialling loopback. Our WebDAV server itself is fine: a
  direct `HttpClient.OPTIONS` probe returns `200 OK` with `DAV: 1`
  and `MS-Author-Via: DAV` set. The architectural mismatch is
  Windows', not ours.
- **What I did this turn:**
  - Tightened `E04_SnapshotMountWindows` to hard-fail on a mount
    failure (was soft-skip-on-any-failure). Future regressions
    surface as a red test, not a silent SKIP.
  - Added `MountRepro.NetUseReproducesUserFailure` capturing the
    exact `System error 67` + zero-connect-attempts evidence.
- **I need from you:** pick a fix path from the four candidates
  below by replying with "Use option N" or describing what you
  want. Until then this entry stays Open.
  1. **Replace WebDAV with WinFSP / Dokany** virtual file system.
     Real solution; new bundled dependency (~1 MB), still works
     without admin once the FUSE driver is installed. Restic
     Browser / SyncBack already use this pattern.
  2. **Replace "Mount in Explorer" with a synchronous tree-copy**
     to a temp dir. Open Explorer there. Works on every Windows
     without admin / WebClient / registry tweaks. Trade-off: temp
     disk usage; "mount" semantics weaken to "extract then browse".
  3. **Pre-flight registry tweaks** to teach the WebClient service
     custom ports + restart it. Requires one-time elevation at
     install. Brittle on locked-down hosts.
  4. **Hide the Mount button until one of 1-3 ships.** Honest but
     a feature regression.
  Option 2 is the smallest delivery that makes the feature work
  for everyone today; option 1 is the eventual right answer.
- **Historical proof:** Superseded by the current audit evidence below.

- **2026-10-02 audit:** R009: A one-mount test and past WinFSP work exist. Current multi-mount, Unmount and process-lifecycle requirements remain incomplete; the original packaged-app starting state has not been revalidated in this audit.
- **Proof — open and look:** [Fresh test results and their limits](../audit-dashboard/evidence/test-report.html) — 808/809 unit/visual and 37/39 local E2E passed. Open failed cases, focused rerun, test filters, screenshots and complete hash checks.
- **I need from you:** Review the corresponding local dashboard item and explicitly accept it or describe the remaining work.

### P0: Mount feature actually working (WinFSP-backed) — Awaiting validation

- **Raised:** 2026-06-12 (split from the original Mount investigation)
- **Type:** feature / architecture
- **Status:** Open — reopened by the 2026-10-02 audit
  implemented, bundled into the dist, validated end-to-end against the
  real runtime. The user installs nothing themselves: Backspace ships
  the signed WinFSP MSI and runs it via msiexec + UAC the first time
  the user clicks Mount.
- **Context:** See the parent entry for why the previous WebDAV path
  silently failed on Windows 10/11 (custom-port redirector rejects).
  WinFSP is the canonical user-mode-filesystem framework on Windows
  (Cygwin, sshfs, many other tools ship against it), so the snapshot
  shows up as a real Windows volume rather than a "is this a real
  drive" WebDAV alias.
- **What it does (short):** Bundled WinFSP MSI ships in the dist
  folder. First Mount click shows "One-time install needed" →
  Install → UAC → MSI installs (kernel driver lives in Program
  Files; Windows requires that for any FS driver — Kopia, rclone-
  mount, sshfs-win all do the same). Subsequent Mounts are
  instant. Backspace.exe itself stays portable.
- **The actual diagnosis (decompiled from WinFsp.Net source).** The
  type initializer's failure has nothing to do with native DLL
  loading. WinFsp.Net's `Fsp.Interop.Api..cctor()` calls
  `CheckVersion()`, which calls:
  ```csharp
  FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location);
  ```
  In a `PublishSingleFile` bundle (which is how we ship the dist),
  `Assembly.GetExecutingAssembly().Location` returns the **empty
  string** — that's a documented .NET limitation
  (`warning IL3000` already fires elsewhere in the codebase).
  Empty path → `FileVersionInfo.GetVersionInfo("")` → throws
  `ArgumentException: The path is empty (Parameter 'path')` →
  surfaces as "The type initializer for 'Fsp.Interop.Api' threw."
- **Earlier failed attempts (kept for posterity so future me doesn't
  repeat them):**
  - `SetDllImportResolver` — useless; WinFsp.Net calls
    `LoadLibraryW` directly, not via `[DllImport]`.
  - `AddDllDirectory` — adds the WinFSP bin folder to the loader
    search path. Necessary but unrelated to the actual bug.
  - `NativeLibrary.Load(absolutePath)` of winfsp-x64.dll plus a
    self-test that `GetModuleHandle("winfsp-x64.dll")` returns
    the loaded handle. Confirmed working — but again, unrelated
    to the actual cctor failure.
  - "Smoke test" `new FileSystemHost(new SmokeFs())` — useless;
    constructing FileSystemHost doesn't touch `Fsp.Interop.Api`.
    The first reference to Api happens inside `Mount()`.
- **Working fix:**
  - `Backspace.csproj` carves `winfsp-msil.dll` (WinFsp.Net's
    managed wrapper) OUT of the single-file bundle via an
    MSBuild target that removes it from `FilesToBundle` before
    `GenerateSingleFileBundle` runs. A post-Publish target then
    copies the build-output DLL to the publish dir next to
    `Backspace.exe`. End result: the wrapper assembly lives on
    disk; `Location` returns the real path; the cctor passes.
  - `WinFspNativeLoader.EnsureRegistered()` belt-and-braces:
    `Assembly.LoadFrom(<dist>\winfsp-msil.dll)` via reflection
    before any `Fsp.*` type is touched. Even if a future
    regression re-bundles the file, the LoadFrom from disk
    gives the assembly a proper `Location`.
  - Diagnostic sentinel at `%TEMP%\Backspace-winfsp-loader.log`
    writes "Fsp.Interop.Api cctor OK" on success or
    "cctor THREW: …" with full inner exception. Survives the
    pre-AppLogger window. Trust nothing about WinFsp.Net + single-
    file without checking the sentinel.
- **Historical proof:** Superseded by the current audit evidence below.
- **I need from you:**
  - Close any running Backspace, replace `dist\Backspace.exe`
    with the fresh build, launch it, click Mount on any backup.
    First time: "One-time install needed" dialog → Install →
    UAC → drive letter pops in ~5 s. Every Mount after is
    instant. If you still hit "Fsp.Interop.Api" please share
    `dist\Backspace.config\logs\app\app-<date>.log` — the new
    code logs `WinFSP bin folder added to DLL search path: …`
    on success or a Win32 error code on the failure path, so
    the log line will tell us exactly which step failed.

- **2026-10-02 audit:** R016: The WinFSP filesystem is present, but screenshots of a Backup card do not prove a readable Explorer mount. A native mount must be tested with its actual prerequisites and safe lifecycle.
- **Proof — open and look:** [Fresh test results and their limits](../audit-dashboard/evidence/test-report.html) — 808/809 unit/visual and 37/39 local E2E passed. Open failed cases, focused rerun, test filters, screenshots and complete hash checks.
- **I need from you:** Review the corresponding local dashboard item and explicitly accept it or describe the remaining work.

### P1: Headless test session degrades ~70s into combined suite runs

- **Raised:** 2026-07-05 (found while validating the hover refinements)
- **Type:** bug (test infrastructure + two real product leaks)
- **Status:** Open — reopened by the 2026-10-02 audit
- **Root cause (the chain):**
  1. **`MainWindow.Close()` never actually closed in tests.** The
     hide-to-tray branch (ShowTrayIcon + MinimizeToTrayOnClose both
     default true, `App.QuitRequested` never set by tests) CANCELS the
     close and hides the window instead. OnClosed never ran, the
     DataContext VM never disposed — every "closed" shell (9+ per run)
     kept its Config.Changed / AppStatus.Changed / Engine.LineWritten
     subscriptions, ran a 160ms icon-animation timer during every
     backup/restore/verify, and fired a live GitHub update check (and
     potentially a toast window) per construction.
  2. **Test-created VMs were almost never disposed** while their
     constructors subscribe to process-lifetime singletons — and
     UiThreadDebouncer's UI-thread fast path runs Config.Changed
     handlers SYNCHRONOUSLY, so each of the ~165 `Config.Update` calls
     in tests ran a full Refresh in every leaked VM accumulated so
     far. Cost grew linearly through the run: the ~70s cliff.
  3. **Theme/accent leaks amplified it:** S05 restored Light only on
     the happy path, S12 left the last cycled accent applied
     process-wide, AttentionPillScreenshotTests never restored the
     variant — every app-wide swap re-evaluated DynamicResources
     across all leaked windows.
- **Fixes shipped:** `App.QuitRequested = true` +
  `BACKSPACE_NO_UPDATE_CHECK=1` in TestSetup (closes become real,
  no live HTTP mid-suite); UpdateChecker gate; VM disposal everywhere
  tests construct them (using-wrap / try-finally, ~60 sites) and
  UiDriver.Dispose now disposes window DataContexts; failure-safe
  window closes in both screenshot CaptureWindow helpers and the
  AttentionPill helpers; theme/accent finally-restores in S05, S12,
  and AttentionPillScreenshotTests. Two REAL product fixes that fell
  out: `ActiveRestoresViewModel` had no unsubscribe path (leaked a
  registry handler for every visit to the Restores page, also in
  production) and tray-hidden MainWindows animated a title-bar icon
  nobody can see (160ms timer for the whole run duration).
- **Historical proof:** Superseded by the current audit evidence below.
- **I need from you:** treat this as validated if the suite stays
  green over your next few runs; the two product-side fixes (Restores
  page handler leak, tray-hidden icon animation) ride along in the
  same commit.
- **Context:** Full-suite runs (812 tests) intermittently fail 1-10
  tests; suite halves NEVER fail. Evidence gathered:
  - The failing set is always the same family: synthetic pointer-click
    tests (InteractionTests, InteractionDestinationClickTests) and
    scenarios that assert on freshly-shown windows' visual trees
    (S28/S29/S31/S50).
  - In a captured failing run, everything before ~70s passes, then
    EVERY family member after that point fails while burning its full
    5-second retry timeout — a cliff, not noise. Affected runs also
    get slower overall (1m31s → 2m19s).
  - Both suite halves (scenarios-only; everything-else-only) pass
    100% across repeated runs. Combined runs on identical binaries
    swing 0-10 failures. Day-over-day variance is large (three
    all-green combined runs on 2026-07-04/05 vs repeated failures
    later on 2026-07-05).
  - Pairing the last-passing heavyweight test before the cliff
    (`TestRestore_AfterSuccessfulBackup_PersistsRunRecordKind`) with a
    canary click test does NOT reproduce — the poison is cumulative,
    not a single test.
  - Parallelization is already fully disabled (xunit.runner.json +
    assembly attribute), so this is same-process cumulative state on
    the shared Avalonia headless session, not concurrency.
- **Hardening shipped (2026-07-05):** ClickAt helpers no longer fall
  back to clicking the window origin when TranslatePoint is null
  pre-layout (they wait for a real arranged position or throw), and
  the affected assertions go through bounded WaitFor/WaitUntil settle
  loops. This makes real failures readable but does not cure the
  cliff.
- **Leading hypotheses for the next session:** (a) leaked pointer
  capture or input state on the shared headless pointer device after
  some click sequence, breaking all later synthetic clicks; (b)
  accumulated dispatcher work (leaked DispatcherTimers / VMs from
  ~750 prior tests) starving template application on newly shown
  windows. Next diagnostic: binary-search the test ORDER (run first N
  classes + one canary click test) to find the smallest poisoning
  prefix; instrument UiDriver.Show to report template-application
  latency.
- **I need from you:** nothing yet; treat single-run suite failures in
  this family as suspect until this entry closes (halves-green is the
  reliable signal meanwhile).

- **2026-10-02 audit:** A014: Fresh full run had S36 fail after its body in Avalonia Dispatcher.PushFrame, while SUMMARY.md displayed S36 PASS and 48/48 overall. Isolated rerun passed. Scenario report alone is therefore insufficient proof.
- **Proof — open and look:** [Fresh visual scenario report — October 2](../audit-dashboard/evidence/unit-visual/index.html) — 48 scenario bodies completed; the xUnit run separately failed S36 on dispatcher cleanup, and focused S36 rerun passed. Report alone must not be treated as full-suite green.; [Fresh test results and their limits](../audit-dashboard/evidence/test-report.html) — 808/809 unit/visual and 37/39 local E2E passed. Open failed cases, focused rerun, test filters, screenshots and complete hash checks.
- **I need from you:** Review the corresponding local dashboard item and explicitly accept it or describe the remaining work.

### P1: Restore target radios ping-pong forever (test-suite hang)

- **Raised:** 2026-07-04 (found by us: the visual suite sat 40+ minutes
  with one core saturated; the user asked to investigate what was
  abnormally slow)
- **Type:** bug (latent UI infinite loop)
- **Status:** Awaiting validation
- **Context:** Managed stack dumps of the hung testhost showed
  `WebsiteScreenshotTests.Shot_04_Restore_Pick` spinning inside
  Avalonia layout, endlessly re-entering
  `RestoreViewModel.set_TargetOriginalSource` via
  `RadioButtonGroupManager.OnCheckedChanged`.
- **Root cause + fix (short):** In the restore wizard's "Where to?"
  step, BOTH radios of the `target` group were two-way bound to the
  SAME property, the second through a negating converter. The group
  manager's uncheck wrote back through the binding, flipping the
  source, which re-flipped the sibling, forever. A settable mirror
  property still looped, so the wiring is now structurally loop-proof:
  both radios bind One-Way (never write the source) and user clicks
  reach the VM through check-only `IsCheckedChanged` handlers that
  ignore unchecks; the VM setter no-ops on equal values. That graph
  cannot cycle.
- **Historical proof:** Superseded by the current audit evidence below.
- **Flakiness follow-up (2026-07-05):** repeated full-suite runs on
  identical binaries showed a persistent low-grade flake family (0-10
  failures per run, always pointer-click / visual-tree-lookup tests:
  InteractionTests, InteractionDestinationClickTests, S28/S29/S31/S50).
  Root mechanics: `ClickAt` fell back to clicking the window ORIGIN
  when `TranslatePoint` returned null pre-layout, and several tests
  asserted immediately after Show()/click instead of waiting for the
  dispatcher to settle. Hardened: ClickAt now waits for a real
  arranged position (throws instead of mis-clicking), and the flaky
  assertions go through bounded WaitFor/WaitUntil loops. Verified with
  repeated full-suite runs.
- **I need from you:** run a restore and flip between "original source"
  and "different folder" a few times; confirm the radios select
  normally and the folder field enables/disables correctly.

- **2026-10-02 audit:** R001: This item was already awaiting user validation in the interrupted roadmap. The current non-cloud run regenerated its scenario evidence where available; the old completion claim is not treated as acceptance.
- **Proof — open and look:** [Fresh scenario screenshot](../audit-dashboard/evidence/unit-visual/S23-restore-target-step/screenshots/01-target-original-source.png) — Inspect the displayed controls and text for this roadmap item. Synthetic scenario data; not a live cloud or system-level test.; [Fresh scenario screenshot](../audit-dashboard/evidence/unit-visual/S23-restore-target-step/screenshots/02-target-different-folder.png) — Inspect the displayed controls and text for this roadmap item. Synthetic scenario data; not a live cloud or system-level test.; [Fresh test results and their limits](../audit-dashboard/evidence/test-report.html) — 808/809 unit/visual and 37/39 local E2E passed. Open failed cases, focused rerun, test filters, screenshots and complete hash checks.
- **I need from you:** Review the corresponding local dashboard item and explicitly accept it or describe the remaining work.

### P1: Filters > Advanced > Simulate fails with exit status 1

- **Raised:** 2026-07-03
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** User clicked Filters > Advanced > Simulate and got:
  `[error] /v1.Backrest/RunCommand failed: unknown: failed to run
  command: command "backup --dry-run C:\\ntws": exit status 1`.
  The simulation pane never produces results.
- **Root cause + fix (short):** Three stacked defects. (1) Backrest
  splits RunCommand strings with POSIX shlex, which eats unquoted
  Windows backslashes: `C:\ntws` reached restic as `C:ntws`, a
  nonexistent path, hence exit 1. (2) Even quoted, the dry-run ran
  UNFILTERED (RunCommand bypasses the Backrest plan, so the rules
  being simulated were never applied). (3) The output parser expected
  legacy-engine lines restic never prints, so a "successful" run would
  still have shown an empty pane. Fix: Simulate no longer touches
  restic or the repo at all — it walks the sources locally with the
  exact same translated rules the engine sends restic (same
  FilterTranslator), works before any destination exists, and names
  the matching rule per exclusion. The dead engine dry-run
  (`EnumOnlyAsync`) was removed.
- **Historical proof:** Superseded by the current audit evidence below.
- **I need from you:** open your backup > Filters > Advanced and click
  Simulate on the real `C:\ntws` source; the right pane should list
  files with ✓/✗ instead of erroring.

- **2026-10-02 audit:** R002: This item was already awaiting user validation in the interrupted roadmap. The current non-cloud run regenerated its scenario evidence where available; the old completion claim is not treated as acceptance.
- **Proof — open and look:** [Fresh scenario screenshot](../audit-dashboard/evidence/unit-visual/S40-filter-simulation-pane/screenshots/01-simulate-pane-populated.png) — Inspect the displayed controls and text for this roadmap item. Synthetic scenario data; not a live cloud or system-level test.; [Fresh test results and their limits](../audit-dashboard/evidence/test-report.html) — 808/809 unit/visual and 37/39 local E2E passed. Open failed cases, focused rerun, test filters, screenshots and complete hash checks.
- **I need from you:** Review the corresponding local dashboard item and explicitly accept it or describe the remaining work.

### P1: Status text next to "Apply recommended exclusions" is cropped

- **Raised:** 2026-07-03
- **Type:** bug (visual)
- **Status:** Awaiting validation
- **Context:** The message "Nothing in your sources matched the
  recommended rules…" was cut off at the right edge instead of
  wrapping. User asked for a thorough pass: find and fix ALL text that
  can crop this way, not just this instance.
- **Root cause + fix (short):** Horizontal StackPanels measure children
  at infinite width, so TextWrapping/TextTrimming silently never
  engage and the overflow is clipped by the card or window edge. A
  sweep of all 17 views found the same defeated-wrap pattern in
  11 places, all fixed (star-column Grid, or MaxWidth + ellipsis +
  tooltip where a one-liner is wanted): the reported savings-status
  row, the Simulate hint row, Settings save-status and test-email
  rows, both destination auth callouts, backup-card last-run summary
  and verify lane, the change-password dialog status, destination-row
  URLs, and the Logs title bar.
- **Historical proof:** Superseded by the current audit evidence below.
- **I need from you:** open the same editor as before (Filters >
  Recommended on a narrow source) and confirm the message reads fully.

- **2026-10-02 audit:** R003: This item was already awaiting user validation in the interrupted roadmap. The current non-cloud run regenerated its scenario evidence where available; the old completion claim is not treated as acceptance.
- **Proof — open and look:** [Fresh scenario screenshot](../audit-dashboard/evidence/unit-visual/S50-recommended-row-text-wraps/screenshots/01-savings-message-wraps-beside-button.png) — Inspect the displayed controls and text for this roadmap item. Synthetic scenario data; not a live cloud or system-level test.; [Fresh test results and their limits](../audit-dashboard/evidence/test-report.html) — 808/809 unit/visual and 37/39 local E2E passed. Open failed cases, focused rerun, test filters, screenshots and complete hash checks.
- **I need from you:** Review the corresponding local dashboard item and explicitly accept it or describe the remaining work.

### P1: Estimated savings next to "Apply recommended exclusions" shows 0 B

- **Raised:** 2026-06-11
- **Type:** UX bug (the math was right, the message was wrong)
- **Status:** Awaiting validation
- **Context:** User reported "Estimated savings" showed 0 B. The
  estimator itself was correct — when the recommended rules
  (`pagefile.sys`, `/Windows/`, `ntuser.dat*`, etc.) are run against a
  narrow source like `C:\Users\me\Documents` they genuinely match
  nothing. But "Estimated savings: 0 B on this PC" reads as "broken
  feature", not "nothing to exclude".
- **Plan:** Split the zero-savings case in
  `RecEstimatedSavingsLabel`. When the walk found files but none
  matched the rules, surface a friendly "Nothing in your sources
  matched the recommended rules — that's normal for narrow source
  folders" message. When the walk found NO files at all
  (misconfigured source), say so explicitly. Pipe the `IncludedBytes`
  from `SavingsEstimate` through a new `RecScannedBytes` property to
  drive the distinction.
- **Historical proof:** Superseded by the current audit evidence below.

- **2026-10-02 audit:** R010: This item was already awaiting user validation in the interrupted roadmap. The current non-cloud run regenerated its scenario evidence where available; the old completion claim is not treated as acceptance.
- **Proof — open and look:** [Fresh test results and their limits](../audit-dashboard/evidence/test-report.html) — 808/809 unit/visual and 37/39 local E2E passed. Open failed cases, focused rerun, test filters, screenshots and complete hash checks.
- **I need from you:** Review the corresponding local dashboard item and explicitly accept it or describe the remaining work.

### P1: Skipped-run alerts block visibility logic

- **Raised:** 2026-06-11 (user asked 2026-07-03 to confirm the Easy-mode
  behavior: block hidden in Easy mode UNLESS the backup is currently in
  grace, so the Reset-grace control stays reachable)
- **Type:** bug
- **Status:** Open — reopened by the 2026-10-02 audit
- **Context:** In Easy mode the "Skipped run alerts" block is shown
  unconditionally and feels noisy when the backup is healthy. In
  Advanced mode the block doesn't show AT ALL — so users in grace
  period can't see the reset-grace control. Both modes are wrong in
  opposite directions.
- **Plan:** Added `Backup.ShouldShowGraceBlock(now)` — true iff the
  most recent run was `Skipped` AND we're still inside the grace
  window. Easy mode gates the block's `IsVisible` on
  `OnboardingViewModel.ShowGraceBlock` (which delegates to the model
  helper). Advanced mode now hosts the SAME grace-period controls
  unconditionally — `SkipNotificationGraceDays`,
  `GraceReferenceLabel`, `ResetGracePeriodCommand` mirror their Easy
  twins on `BackupEditorViewModel` and the values persist via
  `CommitToWorking`.
- **Confirmation (2026-07-04), re-verified end-to-end:** the behavior
  matches the requested rule exactly. Easy mode (Onboarding wizard)
  gates the block on `ShowGraceBlock` = last run was Skipped AND still
  inside the grace window (reference = max of created / last success /
  manual reset; 0 days disables). Advanced mode hosts the controls
  unconditionally so Reset stays reachable. 6 covering tests pass
  (5 model-level + the S28 scenario driving both modes).
- **Historical proof:** Superseded by the current audit evidence below.

- **2026-10-02 audit:** B027: A per-backup grace-days setting, last-success clock and manual reset timestamp exist and round-trip through Easy/Advanced editing. The promised notification suppression is missing: SendAlertsAsync checks only the 24-hour same-status cap, never IsWithinSkipGrace, and OnSkipped.Toast defaults true. After grace the run is promoted to Failed instead of emitting the requested skipped warning. B028: A per-backup grace-days setting, last-success clock and manual reset timestamp exist and round-trip through Easy/Advanced editing. The promised notification suppression is missing: SendAlertsAsync checks only the 24-hour same-status cap, never IsWithinSkipGrace, and OnSkipped.Toast defaults true. After grace the run is promoted to Failed instead of emitting the requested skipped warning.
- **Proof — open and look:** [Historical grace screenshot — incomplete proof](../audit-dashboard/evidence/historical/tests__Backspace.Tests__artifacts__scenarios__S28-grace-block-visibility__screenshots__01-advanced-grace-block-always-visible.png) — The historical image shows the section heading at the viewport bottom, but not the days/reset controls; notification suppression cannot be established from this screenshot.; [Fresh isolated audit probes](../audit-dashboard/evidence/cloud-config-probes.html) — Fresh fake-HTTP probe: withinGrace=true but one email is dispatched; explicitly disabling the email channel dispatches zero.
- **I need from you:** Review the corresponding local dashboard item and explicitly accept it or describe the remaining work.

### P1: Collapsible-section hover styling has weird stripe

- **Raised:** 2026-06-11. **Re-opened 2026-07-03:** user reports the
  hover background on collapsible section headers in Settings is still
  weird — the earlier fix covered the restore wizard's expander but the
  Settings sections still misbehave. Follow best-practice hover
  treatment (subtle full-header overlay, matching corner radius,
  smooth transition).
- **Type:** bug (visual regression)
- **Status:** Awaiting validation (refined 2026-07-05 per user
  feedback)
- **Context:** User screenshot showed a dark band across collapsible
  headers on hover; after the first pierced-style fix the user
  reported two refinements: (1) in DARK theme a white flash appeared
  at hover start before the wash settled (light theme clean); (2) the
  wash should extend slightly LEFT of the section icon instead of
  starting flush at it.
- **Refinements shipped (2026-07-05):**
  - **White flash:** the BrushTransition interpolated from
    `Transparent`, which Avalonia defines as WHITE at zero alpha
    (#00FFFFFF) — early fade frames blend visible whitish tones over
    a dark surface. The wash now RESTS on `BS.Brush.NeutralHoverRest`,
    an alpha-zero twin of the hover hue per theme, so the fade is
    pure alpha in both themes. S49 pins the rest hue (fails if it
    ever returns to #00FFFFFF).
  - **Wash extent:** the card's 20px horizontal padding is now split
    10px card + 10px header/content padding, so the header's wash box
    reaches 10px past the icon and the chevron while every piece of
    content sits exactly where it was. Positive paddings only: the
    first attempt used a negative Margin on the header template part,
    which caused layout churn suspected in a burst of headless-suite
    instability, and was replaced before commit.
- **Root cause + fix (short):** The earlier "transparent on hover"
  overrides were silent no-ops: FluentTheme's Expander ControlTheme
  paints its hover brushes DIRECTLY onto the template parts
  (`Border#ToggleButtonBackground` gets an 80%-alpha white/black band
  with ~3px corners, plus a 10%-alpha square behind the chevron), and
  a StyleTrigger on the part outranks the TemplateBinding that
  element-level overrides feed. The fix pierces to those same parts
  with app-frame styles: Settings headers get the app's quiet
  NeutralHover wash (8px corners matching the card, ~180ms ease-out,
  chevron pill removed, one tick darker when pressed); the restore
  wizard's "Advanced" text toggle is now genuinely flat and gets the
  hand cursor.
- **Historical proof:** Superseded by the current audit evidence below.
- **I need from you:** open Settings and hover the section headers;
  confirm the hover now feels right (soft wash, no stripe). Also hover
  the "Advanced" toggle in the restore wizard: flat, cursor change
  only.

- **2026-10-02 audit:** R011: This item was already awaiting user validation in the interrupted roadmap. The current non-cloud run regenerated its scenario evidence where available; the old completion claim is not treated as acceptance.
- **Proof — open and look:** [Fresh scenario screenshot](../audit-dashboard/evidence/unit-visual/S49-settings-expander-hover/screenshots/01-hover-wash-on-section-header.png) — Inspect the displayed controls and text for this roadmap item. Synthetic scenario data; not a live cloud or system-level test.; [Fresh scenario screenshot](../audit-dashboard/evidence/unit-visual/S49-settings-expander-hover/screenshots/02-header-at-rest.png) — Inspect the displayed controls and text for this roadmap item. Synthetic scenario data; not a live cloud or system-level test.; [Fresh test results and their limits](../audit-dashboard/evidence/test-report.html) — 808/809 unit/visual and 37/39 local E2E passed. Open failed cases, focused rerun, test filters, screenshots and complete hash checks.
- **I need from you:** Review the corresponding local dashboard item and explicitly accept it or describe the remaining work.

### P1: Adding a cloud destination erases other destinations from shared rclone.conf

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Open
- **Context:** Audit finding A001. The only production materialization call is BuildRepoFromDestination, which passes new[] { destination } into an overwrite-style builder at the shared default path. Creating repository B therefore replaces the file with only B. Existing repository A bypasses BuildRepoFromDestination, so later backup/restore/check calls for A reference a missing rclone section. This is not limited to a race: sequential A then B then A can reproduce it.
- **Audit finding:** The only production materialization call is BuildRepoFromDestination, which passes new[] { destination } into an overwrite-style builder at the shared default path. Creating repository B therefore replaces the file with only B. Existing repository A bypasses BuildRepoFromDestination, so later backup/restore/check calls for A reference a missing rclone section. This is not limited to a race: sequential A then B then A can reproduce it.
- **Remaining:** Materialize all required remotes atomically, or use isolated per-destination config paths. Refresh existing remote credentials after reconnect and preserve updated refresh tokens safely. Add an A/B/A regression and a concurrent two-cloud test without requiring real accounts for the config assertion.
- **Proof — open and look:** [Fresh isolated audit probes](../audit-dashboard/evidence/cloud-config-probes.html) — Fresh production-method probe: setting up B removes A from rclone.conf; directly rendering A and B together keeps both.
- **Limitations / adversarial review:** Reproduced by invoking actual BuildRepoFromDestination twice: only remote B remains. A control rendering both destinations retains both. No daemon or account operations were performed; this is configuration-path proof, not a live backup.
- **I need from you:** Inspect A001 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P1: OneDrive remote is unusable even after supplying an OAuth app ID

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Open
- **Context:** Audit finding A002. No destination field, OAuth response handling or config builder supplies drive_id. Personal remotes also lack drive_type. The official rclone OneDrive backend rejects either missing field before making an authenticated request; registering the app alone cannot complete the feature.
- **Audit finding:** No destination field, OAuth response handling or config builder supplies drive_id. Personal remotes also lack drive_type. The official rclone OneDrive backend rejects either missing field before making an authenticated request; registering the app alone cannot complete the feature.
- **Remaining:** Discover/select the account drive after OAuth, persist its ID/type, and test the generated configuration with the bundled rclone. Official reference: https://raw.githubusercontent.com/rclone/rclone/master/backend/onedrive/onedrive.go (NewFs checks DriveID and DriveType).
- **Proof — open and look:** [Fresh isolated audit probes](../audit-dashboard/evidence/cloud-config-probes.html) — Fresh bundled-rclone probe: generated OneDrive config exits 1 for missing drive_id/drive_type; local fixture control exits 0.
- **Limitations / adversarial review:** Fresh bundled-rclone probe reproduced the exact missing drive_id/drive_type error before authentication. A local backend control exited successfully. All HTTP proxy variables pointed to a closed loopback endpoint; no live Microsoft account or credentials were used.
- **I need from you:** Inspect A002 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P1: A scheduled run can overwrite newer GUI configuration edits

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Open
- **Context:** Audit finding A004. ConfigStore locks only its own in-memory instance, saves the entire cached config, and does not reload or merge before saving. Two already-loaded stores were exercised sequentially: the GUI changed a backup name; the worker saved LastRunStatus; the persisted name reverted to its old value. No simultaneous-write timing is required. The GUI and headless scheduler are independent processes and use this same persistence path.
- **Audit finding:** ConfigStore locks only its own in-memory instance, saves the entire cached config, and does not reload or merge before saving. Two already-loaded stores were exercised sequentially: the GUI changed a backup name; the worker saved LastRunStatus; the persisted name reverted to its old value. No simultaneous-write timing is required. The GUI and headless scheduler are independent processes and use this same persistence path.
- **Remaining:** Coordinate config writes across processes and load/merge under that lock, or have one owner service serialize changes. Add the independent-writer regression and real GUI-plus-headless coverage. Preserve new backup definitions, destination edits and run status together.
- **Proof — open and look:** [Fresh config lost-update reproduction](../audit-dashboard/evidence/cloud-config-probes.html) — A newer name edit disappears after a stale worker saves run status; the single-store control keeps the edit.
- **Limitations / adversarial review:** Reproduced using two actual ConfigStore objects against a disposable file, modeling independent GUI/headless snapshots. It does not launch Task Scheduler or the app. A single-store control retained both changes.
- **I need from you:** Inspect A004 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P1: Independent cached secret writers can drop newly saved keys

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Open
- **Context:** Audit finding A005. SecretsStore caches the entire vault per instance and writes that cached map after each mutation. In an isolated two-store probe, writer A saved a new key, then already-loaded writer B saved a different key. The fresh vault contained B but had lost A. Atomic replacement prevents torn bytes but does not prevent stale whole-vault overwrites.
- **Audit finding:** SecretsStore caches the entire vault per instance and writes that cached map after each mutation. In an isolated two-store probe, writer A saved a new key, then already-loaded writer B saved a different key. The fresh vault contained B but had lost A. Atomic replacement prevents torn bytes but does not prevent stale whole-vault overwrites.
- **Remaining:** Use cross-process vault coordination and refresh the current map under the write lock. Add a regression that verifies disjoint updates and removals from multiple store instances are preserved.
- **Proof — open and look:** [Fresh secret lost-update reproduction](../audit-dashboard/evidence/cloud-config-probes.html) — After two independently cached writers save different synthetic keys, key A is absent while key B remains.
- **Limitations / adversarial review:** Reproduced with production SecretsStore and pass-through encryption over synthetic values only. This proves the storage defect, not how frequently two production writers occur; headless runs mainly read secrets. No real tokens or DPAPI-protected data were accessed.
- **I need from you:** Inspect A005 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P1: Temporary secrets-file lock can cause a later save to discard existing credentials

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Open
- **Context:** Audit finding A006. SecretsStore.Load catches every exception as a decryption failure, including a temporary sharing violation. It fails to move the locked vault aside, caches an empty map, and permits later writes. The isolated probe held a temporary read lock, released it, then saved a new synthetic key through the same store: the old key disappeared from the active vault. A fresh store could read the original key before that save, proving it was not corrupt. A .bak copy retained the old bytes.
- **Audit finding:** SecretsStore.Load catches every exception as a decryption failure, including a temporary sharing violation. It fails to move the locked vault aside, caches an empty map, and permits later writes. The isolated probe held a temporary read lock, released it, then saved a new synthetic key through the same store: the old key disappeared from the active vault. A fresh store could read the original key before that save, proving it was not corrupt. A .bak copy retained the old bytes.
- **Remaining:** Treat IO/permission failures as retryable read failures, leave the current vault intact, and block mutation until it has been loaded successfully. Reserve decryption-recovery behavior for genuine cryptographic/format errors and preserve the original when recovery cannot be written safely.
- **Proof — open and look:** [Fresh temporary-lock reproduction](../audit-dashboard/evidence/cloud-config-probes.html) — The original synthetic key is readable after unlocking; saving through the failed reader then removes it from the active vault.
- **Limitations / adversarial review:** Reproduced using the real file-sharing failure and production SecretsStore with fixture encryption and disposable synthetic values; no real credentials or DPAPI were used. The prior .bak remains recoverable, but the active store has lost its prior key.
- **I need from you:** Inspect A006 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P1: Starting another sidecar can terminate a live Backspace session engine

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Open
- **Context:** Audit finding A007. BackrestSidecar.StartAsync unconditionally calls KillOrphanedSidecar and KillByExeFingerprint. The first verifies only PID plus executable path; the second kills matching Backspace-bundled backrest processes, including other installations. Neither verifies that the owning Backspace process has died. Headless scheduler runs bypass the GUI single-instance mutex and can start their own sidecar while the GUI or another backup is active.
- **Audit finding:** BackrestSidecar.StartAsync unconditionally calls KillOrphanedSidecar and KillByExeFingerprint. The first verifies only PID plus executable path; the second kills matching Backspace-bundled backrest processes, including other installations. Neither verifies that the owning Backspace process has died. Headless scheduler runs bypass the GUI single-instance mutex and can start their own sidecar while the GUI or another backup is active.
- **Remaining:** Track and verify live owner identity before cleanup, and define a shared-engine or isolated-engine strategy for GUI/headless concurrency. Test live owner, different backup, different installation, exited owner and PID reuse using disposable helper processes only.
- **Proof — open and look:** [Audit overview](../audit-dashboard/evidence/audit-overview.html#A007) — source-inspection finding and exact missing validation; no completion proof claimed.
- **Limitations / adversarial review:** Confirmed source-level control-flow defect; the audit deliberately did not invoke either kill routine. The per-backup semaphore prevents duplicate runs of one backup but does not protect a different active backup or an idle GUI engine.
- **I need from you:** Inspect A007 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P1: Failed or cancelled backend restores can be reported as successful

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Open
- **Context:** Audit finding A009. WaitForOperationTerminalAsync returns on success, warning, error or user cancellation without returning the terminal status. RestoreFilesAsync then assumes Success. If no final JSON summary is available, it also invents a restored count equal to the requested count. A backend error before summary emission can appear as a completed restore.
- **Audit finding:** WaitForOperationTerminalAsync returns on success, warning, error or user cancellation without returning the terminal status. RestoreFilesAsync then assumes Success. If no final JSON summary is available, it also invents a restored count equal to the requested count. A backend error before summary emission can appear as a completed restore.
- **Remaining:** Return and preserve the actual backend terminal status/error; never infer restored files merely from a requested count. Add error-without-summary and backend-cancel tests, and make the UI display failed output honestly.
- **Proof — open and look:** [Fresh reproduction: backend error becomes Success](../audit-dashboard/evidence/restore-safety-probes.html) — The error and backend-cancel probes return Success and restored=1 with outputFiles=0; mocked backend responses isolate the status-handling defect.
- **Limitations / adversarial review:** Reproduced through the actual production RestoreFilesAsync with in-memory HTTP responses: both STATUS_ERROR and STATUS_USER_CANCELLED became Success with one restored file even though the target was empty. The STATUS_SUCCESS control case completed as expected. No real daemon was run.
- **I need from you:** Inspect A009 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P1: Restore cancellation does not cancel the queued daemon operation

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Open
- **Context:** Audit finding A010. RestoreFilesAsync catches cancellation and reports Cancelled, but never sends /v1.Backrest/Cancel for its known operation id. The backup path has a cancellation RPC helper, but restore does not use it. Stopping the UI wait therefore does not stop the daemon's restore work.
- **Audit finding:** RestoreFilesAsync catches cancellation and reports Cancelled, but never sends /v1.Backrest/Cancel for its known operation id. The backup path has a cancellation RPC helper, but restore does not use it. Stopping the UI wait therefore does not stop the daemon's restore work.
- **Remaining:** Send cancellation using a separate bounded token, wait for terminal acknowledgement, and defer include-file deletion/cleanup until the daemon no longer uses those files. Test that output stops changing after cancellation against a disposable real repository.
- **Proof — open and look:** [Fresh cancellation request trace](../audit-dashboard/evidence/restore-safety-probes.html) — The cancellation probe returns Cancelled but records zero daemon Cancel requests; all HTTP requests are listed.
- **Limitations / adversarial review:** The safe production-method probe records every mocked HTTP request and confirms zero /Cancel requests after cancellation. It demonstrates the missing protocol action, not real post-cancel disk writes. S37 uses the in-memory engine and cannot catch this daemon mismatch.
- **I need from you:** Inspect A010 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P1: Every restore has a hard five-minute wait limit

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Open
- **Context:** Audit finding A011. The shared operation wait uses DateTime.UtcNow.AddMinutes(5). A legitimate large or slow restore exceeding that deadline throws, removes its temporary include file, and does not cancel its daemon operation. The UI can report failure while restore writes continue.
- **Audit finding:** The shared operation wait uses DateTime.UtcNow.AddMinutes(5). A legitimate large or slow restore exceeding that deadline throws, removes its temporary include file, and does not cancel its daemon operation. The UI can report failure while restore writes continue.
- **Remaining:** Use a restore-appropriate lifetime and progress-aware stall handling, preserve daemon ownership through timeout/cancellation, and add a slow restore test that passes the old boundary.
- **Proof — open and look:** [Audit overview](../audit-dashboard/evidence/audit-overview.html#A011) — source-inspection finding and exact missing validation; no completion proof claimed.
- **Limitations / adversarial review:** Source-confirmed control-flow finding. The five-minute duration was not waited out during this audit, and no network-shaping test exists for the reported long-running behavior.
- **I need from you:** Inspect A011 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P1: Password rotation force-removes repository locks without exclusive ownership

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Open
- **Context:** Audit finding A012. After restic key passwd reports an existing lock, ChangeStoragePasswordAsync automatically executes unlock --remove-all and retries. Its comment claims the maintenance gate is held, but neither the dialog nor its caller acquires that gate. The preflight check only observes currently running backups before the dialog opens; another operation or process can acquire the repository afterward.
- **Audit finding:** After restic key passwd reports an existing lock, ChangeStoragePasswordAsync automatically executes unlock --remove-all and retries. Its comment claims the maintenance gate is held, but neither the dialog nor its caller acquires that gate. The preflight check only observes currently running backups before the dialog opens; another operation or process can acquire the repository afterward.
- **Remaining:** Do not remove active repository locks automatically. Acquire a real exclusive maintenance lease, coordinate with daemon jobs and other Backspace processes, and wait or show an actionable lock error. Add a concurrency test proving an unrelated active lock is preserved.
- **Proof — open and look:** [Audit overview](../audit-dashboard/evidence/audit-overview.html#A012) — source-inspection finding and exact missing validation; no completion proof claimed.
- **Limitations / adversarial review:** Source-confirmed unsafe branch; this audit did not remove repository locks or run a conflicting writer. Existing E19 tests only a quiet happy path.
- **I need from you:** Inspect A012 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P1: Destructive maintenance does not wait for active restores or verification

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Open
- **Context:** Audit finding A013. RestoreEngine and BackupVerifier call WaitForReadAsync only before starting; they hold no read lease. MaintenanceCoordinator.AcquireAsync then checks/cancels only the orchestrator's backup registry. A restore or verification that already passed the initial check is not awaited or cancelled before erase/prune maintenance begins.
- **Audit finding:** RestoreEngine and BackupVerifier call WaitForReadAsync only before starting; they hold no read lease. MaintenanceCoordinator.AcquireAsync then checks/cancels only the orchestrator's backup registry. A restore or verification that already passed the initial check is not awaited or cancelled before erase/prune maintenance begins.
- **Remaining:** Track operation lifetimes with real read leases or a shared operation registry, and establish exclusive maintenance across the daemon and scheduled processes. Verify an already-running restore prevents erase until it has stopped safely.
- **Proof — open and look:** [Audit overview](../audit-dashboard/evidence/audit-overview.html#A013) — source-inspection finding and exact missing validation; no completion proof claimed.
- **Limitations / adversarial review:** Source-confirmed coordination gap. This audit did not run destructive maintenance concurrently with any real restore. Restic may reject some conflicts with repository locks, but that is not the application guarantee claimed by the coordinator.
- **I need from you:** Inspect A013 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P1: Scenario summary can show PASS while xUnit fails the same scenario

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Open
- **Context:** Audit finding A014. Fresh full run had S36 fail after its body in Avalonia Dispatcher.PushFrame, while SUMMARY.md displayed S36 PASS and 48/48 overall. Isolated rerun passed. Scenario report alone is therefore insufficient proof.
- **Audit finding:** Fresh full run had S36 fail after its body in Avalonia Dispatcher.PushFrame, while SUMMARY.md displayed S36 PASS and 48/48 overall. Isolated rerun passed. Scenario report alone is therefore insufficient proof.
- **Remaining:** Propagate test-session/teardown failures into the report or present xUnit/TRX alongside it; stabilize headless session ordering.
- **Proof — open and look:** [Fresh visual scenario report — October 2](../audit-dashboard/evidence/unit-visual/index.html) — 48 scenario bodies completed; the xUnit run separately failed S36 on dispatcher cleanup, and focused S36 rerun passed. Report alone must not be treated as full-suite green.; [Fresh test results and their limits](../audit-dashboard/evidence/test-report.html) — 808/809 unit/visual and 37/39 local E2E passed. Open failed cases, focused rerun, test filters, screenshots and complete hash checks.
- **Limitations / adversarial review:** One observed full-run failure, not reproducible in isolation.
- **I need from you:** Inspect A014 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P1: Edge-case restore reports zero restored files despite materializing every regular file

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Open
- **Context:** Audit finding A015. Fresh E11 backup succeeded, but restore returned Warning and0/9 restored, causing the test to fail its at-most2-known-failures threshold. Inspection found all8 regular files restored, and full SHA-256 comparison matches all8. Junction traversal is excluded from that count. This establishes inconsistent outcome accounting rather than loss of all9 files.
- **Audit finding:** Fresh E11 backup succeeded, but restore returned Warning and0/9 restored, causing the test to fail its at-most2-known-failures threshold. Inspection found all8 regular files restored, and full SHA-256 comparison matches all8. Junction traversal is excluded from that count. This establishes inconsistent outcome accounting rather than loss of all9 files.
- **Remaining:** Diagnose terminal operation/statistics parsing and restore result accounting; add fixture assertions for actual file contents alongside reported counts.
- **Proof — open and look:** [Fresh local-engine report](../audit-dashboard/evidence/local-e2e/index.html) — E11 fails with9 reported restore failures; scenario details preserve the exact failure.; [Full-file digest comparison](../audit-dashboard/evidence/full-hash-proof.html) — All8 regular E11 source files are present with identical complete SHA-256 hashes.; [Fresh test results and their limits](../audit-dashboard/evidence/test-report.html) — 808/809 unit/visual and 37/39 local E2E passed. Open failed cases, focused rerun, test filters, screenshots and complete hash checks.
- **Limitations / adversarial review:** Exact source of zero count is not yet isolated. NTFS ACL, ADS and sparse preservation remain known separate limits; no production changes made.
- **I need from you:** Inspect A015 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P1: Synced fixture attributes can change during validation

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Open
- **Context:** Audit finding A016. E01 Smoke failed because system-attr.txt was ReadOnly in the source and System in restored output. Minutes later both files had ReadOnly+Archive without audit edits. All40 full SHA-256 file hashes match. Asynchronous external attribute normalization is plausible because fixture data lives in a Dropbox-synced checkout, but the actor was not established.
- **Audit finding:** E01 Smoke failed because system-attr.txt was ReadOnly in the source and System in restored output. Minutes later both files had ReadOnly+Archive without audit edits. All40 full SHA-256 file hashes match. Asynchronous external attribute normalization is plausible because fixture data lives in a Dropbox-synced checkout, but the actor was not established.
- **Remaining:** Reproduce E01 Smoke with fixtures outside the synced checkout before attributing the metadata mismatch to the backup engine.
- **Proof — open and look:** [E01 Smoke failure detail](../audit-dashboard/evidence/local-e2e/index.html) — Scenario report shows the attribute failure; contents are checked separately.; [All40 source/restored digests](../audit-dashboard/evidence/full-hash-proof.html) — Complete file contents match for all40 files despite the transient metadata failure.; [Fresh test results and their limits](../audit-dashboard/evidence/test-report.html) — 808/809 unit/visual and 37/39 local E2E passed. Open failed cases, focused rerun, test filters, screenshots and complete hash checks.
- **Limitations / adversarial review:** The observed attribute values and mutation are real; Dropbox causation is an inference. Do not call this proven restic corruption or silently count the scenario as passed.
- **I need from you:** Inspect A016 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P1: VSS and elevation on Windows

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B004: - Make sure VSS always enabled on Windows (require app elevation if needed at launch/auto elevate)
- **Audit finding:** New backups default UseVss to true and Windows backup flags include --use-fs-snapshot. A helper, policy and UAC-launch implementation exist, but the real orchestrator calls the engine directly: neither VssElevationPolicy.Evaluate nor RunElevatedAsync has a production call site. The editor nevertheless promises automatic elevation.
- **Remaining:** Connect and exercise the elevation path with the actual daemon privilege boundary; decide whether an advanced off-switch is compatible with 'always enabled'. Prove a locked file can be backed up and restored from an unelevated GUI and an unattended schedule.
- **Proof — open and look:** [Audit overview](../audit-dashboard/evidence/audit-overview.html#B004) — source-inspection finding and exact missing validation; no completion proof claimed.
- **Limitations / adversarial review:** Policy/argument tests do not start UAC or capture a VSS snapshot. Existing real-engine scenarios explicitly set UseVss=false. No current visual or real locked-file proof exists.
- **I need from you:** Inspect B004 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P1: Test restore against the original bytes

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B006: - Test restore feature that actually compares restored files with known local files to see if bytes match (unless you think this is useless, if there is already some hashing happening, integrity can always be reliably verified right? Is it realistic to expect any kind of other data corruption or missing files from the backup compared to the local files?).
- **Audit finding:** BackupVerifier restores up to ten nonempty files per reachable destination into a scratch folder and compares size and source bytes. It skips or replaces files modified after the snapshot. This is useful end-to-end evidence beyond merely asking the repository to check its internal integrity.
- **Remaining:** Decide whether the requested feature must cover all sources, zero-byte files and missing-from-snapshot files: current VerifyCoreAsync selects SourcePaths.FirstOrDefault and samples from files already in the snapshot, so it cannot discover source files omitted from that snapshot. Add a visual report of the actual results and tests for corrupted bytes and a failing second source.
- **Proof — open and look:** [Audit overview](../audit-dashboard/evidence/audit-overview.html#B006) — source-inspection finding and exact missing validation; no completion proof claimed.
- **Limitations / adversarial review:** Passing the sample is not a guarantee of full backup completeness. An unchanged original may be unavailable, producing size-only comparisons; the summary distinguishes these. Historical E16 screenshot is a generic Backups page that still says 'Not yet tested', so it is not attached as positive visual proof.
- **I need from you:** Inspect B006 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P1: Healthchecks start, failure and completion pings

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B007: - Healthchecks.io integration (restic uses hooks for this, is it equivalent? to fire immediately in case of failure, and upon completion for healthy ping).
- **Audit finding:** Backspace implements check management plus start/success/fail pings directly. Terminal pings share the notification routine's 24-hour same-status cap, while start pings do not: a second successful run can send /start but no completion ping. Terminal pings also wait for email sending/retries and all backup cells to finish, so a destination failure is not reported immediately. Ping HTTP failures are only debug logged because no non-success status is checked.
- **Remaining:** Separate per-run Healthchecks lifecycle from human notification deduplication and email latency. Decide whether failure means first failed destination or terminal aggregate failure, then test it explicitly. Verify success/failure/skip, repeated runs within a day, HTTP errors and user grace resets with a local HTTP fake before a live account test.
- **Proof — open and look:** [Fresh isolated audit probes](../audit-dashboard/evidence/cloud-config-probes.html) — Fresh fake-HTTP probe: first successful lifecycle sends start + success, second same-day lifecycle sends start only.
- **Limitations / adversarial review:** No live Healthchecks requests were sent. Existing tests cover UUID validation/redaction rather than lifecycle requests. Self-hosted management BaseUrl exists but ping delivery is hard-coded to hc-ping.com, so custom-server support also needs review.
- **I need from you:** Inspect B007 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P1: Email errors stay nonfatal and visible

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B015: - Please confirm that if Mailgun is set as the Provider for example, but user doesn't enter any mailgun config values, or wrong ones, the failure won't cause the whole backup to  fail. BUT it should show an error notification so the user knows something is wrong with their config and if they want to receive emails, they'll need to fix it.
- **Audit finding:** Mail errors do not change the completed backup result: SendAsync catches ordinary failures and logs them, and the orchestrator has already persisted the run. However, no user-facing error notification is dispatched for a failed production send. With no recipients the method returns silently even when Mailgun is selected. Send test email shows a banner, but that only helps when the user explicitly runs the test.
- **Remaining:** Return a structured send result or raise a safe notification event; display a visible actionable configuration warning without changing backup success. Test missing recipients/key/domain and provider rejection through the actual backup-to-alert path, plus the valid configuration negative case.
- **Proof — open and look:** [Audit overview](../audit-dashboard/evidence/audit-overview.html#B015) — source-inspection finding and exact missing validation; no completion proof claimed.
- **Limitations / adversarial review:** Code-level control-flow finding, not proof from running an invalid Mailgun account. Existing MailService tests do not execute this end-to-end path or assert a notification.
- **I need from you:** Inspect B015 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P1: Explain quitting during active operations

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B023: Also it is misleading to say "Backspace keeps running in the background so scheduled backups still fire"! App doesn't need to run in the background for this! User should be reassured about this. Only useful to keep to reopen fast. How are things handled if a backup or any other operation is running and user wants to quit? I think we should still push to keep app running in the systray if you can confirm CPU impact will be zero when no operation is running. This can be reassuring for people to see Backspace if there. But we should reassure them even if Backspace is quit, it'll stop existing operations but scheduled operations will be triggered as usual. Find the best way to frame this.
- **Audit finding:** Closing the main window prompts when backups or maintenance are detected, but tray Quit sets QuitRequested first and bypasses this confirmation. The running-operation check does not inspect ActiveRestores; CancelEverything cancels only backup runs despite copy promising backup/prune/check/restore cancellation.
- **Remaining:** Unify close/tray Quit confirmation, include active restores and all maintenance, and await cancellation/cleanup before exiting.
- **Proof — open and look:** 🖼️ [Current quit caveat](../audit-dashboard/evidence/ui-probes/tray-and-schedule-copy.png) — Settings mentions only a backup run being cancelled, while the requested behavior covers every active operation.
- **Limitations / adversarial review:** This is source-path analysis; no destructive native quit was exercised against real work. Headless suite deliberately sets QuitRequested=true, so normal close behavior is bypassed.
- **I need from you:** Inspect B023 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P1: Tolerable destination outages are skipped

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B025: - If a destination is unreachable AND it is because of a known tolerable error (e.g. we're offline or not at home or drive is not present), always Skip with warning. Never consider as Failed.
- **Audit finding:** EngineErrorClassifier uses known network/offline signatures for Skipped and defaults unknown errors to Failed; wrong credentials, missing repositories and full disks are explicitly intolerable. Two paths break the requested distinction: expired grace promotes the aggregate from Skipped to Failed, and NetworkShare preflight treats every Directory.Exists=false or exception as tolerable, including missing/mistyped paths and access problems.
- **Remaining:** Reconcile the grace policy with the user, preserve reason/severity correctly, and replace the boolean network-share probe with an error-aware reachability check. Exercise offline share, missing share path, denied credentials, missing removable drive, unknown engine failure and manual cancellation.
- **Proof — open and look:** [Audit overview](../audit-dashboard/evidence/audit-overview.html#B025) — source-inspection finding and exact missing validation; no completion proof claimed.
- **Limitations / adversarial review:** Source comments claim a later decision to escalate after grace, while the pasted note explicitly asks never to call expected absence Failed. Treat that contradiction as a decision for the user, not permission to discard the note. Pure classifier tests cannot prove the preflight and aggregate paths.
- **I need from you:** Inspect B025 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P1: Unexpected destination errors remain failures

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B026: - If a destination is unreachable or failed and it is another error, mark as Failed.
- **Audit finding:** EngineErrorClassifier uses known network/offline signatures for Skipped and defaults unknown errors to Failed; wrong credentials, missing repositories and full disks are explicitly intolerable. Two paths break the requested distinction: expired grace promotes the aggregate from Skipped to Failed, and NetworkShare preflight treats every Directory.Exists=false or exception as tolerable, including missing/mistyped paths and access problems.
- **Remaining:** Reconcile the grace policy with the user, preserve reason/severity correctly, and replace the boolean network-share probe with an error-aware reachability check. Exercise offline share, missing share path, denied credentials, missing removable drive, unknown engine failure and manual cancellation.
- **Proof — open and look:** [Audit overview](../audit-dashboard/evidence/audit-overview.html#B026) — source-inspection finding and exact missing validation; no completion proof claimed.
- **Limitations / adversarial review:** Source comments claim a later decision to escalate after grace, while the pasted note explicitly asks never to call expected absence Failed. Treat that contradiction as a decision for the user, not permission to discard the note. Pure classifier tests cannot prove the preflight and aggregate paths.
- **I need from you:** Inspect B026 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P1: After grace, skipped backups warn without failing

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B029: In the backup settings (Easy Mode), there should be a grace period number of days selector setting to ignore skipped backups. During this grace period, no notifications should be sent if skipped; also add a button to restart the grace period (useful if user is offline for 2 weeks and grace period is 7 days). After grace period, skipped with warning notifications would be fired as normal. These are not as bad as real failure so should clearly be indicated as such (if we fired Failed just because we're offline, user would be unrightly worried, but of course after some time it is important user decides if it is normal backups are always skipped).
- **Audit finding:** A per-backup grace-days setting, last-success clock and manual reset timestamp exist and round-trip through Easy/Advanced editing. The promised notification suppression is missing: SendAlertsAsync checks only the 24-hour same-status cap, never IsWithinSkipGrace, and OnSkipped.Toast defaults true. After grace the run is promoted to Failed instead of emitting the requested skipped warning.
- **Remaining:** Apply grace suppression to skipped notifications; after expiry retain the agreed severity; test the real alert path inside grace, exactly at the boundary, after manual reset and after a healthy run. Decide whether the Easy-mode control should remain hidden outside an active skipped/grace state.
- **Proof — open and look:** 🖼️ [Historical grace screenshot — incomplete proof](../audit-dashboard/evidence/historical/tests__Backspace.Tests__artifacts__scenarios__S28-grace-block-visibility__screenshots__01-advanced-grace-block-always-visible.png) — The historical image shows the section heading at the viewport bottom, but not the days/reset controls; notification suppression cannot be established from this screenshot.; [Fresh isolated audit probes](../audit-dashboard/evidence/cloud-config-probes.html) — Fresh fake-HTTP probe: withinGrace=true but one email is dispatched; explicitly disabling the email channel dispatches zero.
- **Limitations / adversarial review:** Historical S28 screenshot was inspected: only the Quiet skipped-run alerts heading is visible at the bottom; the days input/reset button are below the captured viewport. It is not adequate visual proof that the full block is reachable. Current model tests cover timestamps and visibility, not notification delivery.
- **I need from you:** Inspect B029 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P1: Keep relocation separate from offline handling

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B030: The locate moved backup UX should have NOTHING to do with this flow (except that the skipped with warning should mention, IF the backup previously succeeded at least once, what you sufggested earlier so user knows it is possible to point to a new location)
- **Audit finding:** A separate destination-editor Locate moved repository action exists. Skipped summaries have a conditional hint that requires previous success, a local-like destination and a nonempty error not classified as tolerable. Ordinary offline share skips therefore do not show the hint, and the grace-expiry error unconditionally suggests locate the moved repository even for never-successful/cloud backups.
- **Remaining:** Confirm the desired hint trigger with the user, then test prior-success versus never-success, offline versus missing path, cloud versus local, and expired grace. Keep relocation opt-in and distinct from status classification.
- **Proof — open and look:** [Audit overview](../audit-dashboard/evidence/audit-overview.html#B030) — source-inspection finding and exact missing validation; no completion proof claimed.
- **Limitations / adversarial review:** The note asks for a previous-success-gated hint; current code narrows it further. No screenshot of the actual requested skipped message was found for this audit.
- **I need from you:** Inspect B030 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P1: Clarify when Expected Drive Letter is useful

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B032: - In which cases does it make sense to show Expected Drive Letter when creating a external drive destination?
- **Audit finding:** The editor now shows Expected volume label only after an external-drive path is set, with no Expected Drive Letter field. However, volume scanning only returns a verdict; it does not rewrite the repository path to the label's new drive letter. Saved legacy ExpectedDriveLetter values are also preserved invisibly.
- **Remaining:** Define whether label matching should relocate the path automatically; implement safe drive/path identity checks and expose or clear obsolete hidden letter constraints.
- **Proof — open and look:** 🖼️ [External-drive destination after choosing a path](../audit-dashboard/evidence/ui-probes/external-drive-volume-label.png) — The form shows Expected volume label ('My Passport'), no letter field, and explains matching by label.
- **Limitations / adversarial review:** UI removal is proven by a fresh screenshot. Actual removable-drive letter changes were not simulated. Source analysis shows the resolver continues using PathOrSubpath unchanged.
- **I need from you:** Inspect B032 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P1: Failure notification layout and link to logs

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B036: - Check that if a backup fail, the notification looks good and takes us to the failed run logs. It should jump a lineafter destinations FAILED, below show the failed destinations preceded by a bullet point, and then show X of Y destinations SUCCEEDED: 10 files restored, 10 byte-identical (same for all destinations normally), jump line and a bullet point list of successes.
- **Audit finding:** The real verify-summary formatter and alert-window builder render failed/succeeded bullet groups and counts, and a pointer press invokes the callback. Ordinary failed-backup notifications still show the first error rather than this grouped verify format. Production click handling stores only backup ID and selects the latest run, so a newer run can replace the failed run the user intended to inspect.
- **Remaining:** Capture and navigate to the exact RunRecord identity; decide whether grouped formatting applies to ordinary backups or only Test restore; add integration tests for an old failure toast clicked after a later success.
- **Proof — open and look:** 🖼️ [Actual grouped verify popup with synthetic outcomes](../audit-dashboard/evidence/ui-probes/grouped-verify-failure-notification.png) — One failed destination appears as a bullet; two successes appear below the restored/byte-identical count.
- **Limitations / adversarial review:** Fresh probe uses synthetic verify outcomes and the actual UI builder. It proves formatting and generic callback dispatch, not real native delivery or exact production log selection. Existing S48 runs log-only with onClick:null.
- **I need from you:** Inspect B036 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P1: Exercise storage password rotation

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B037: - Test password rotation
- **Audit finding:** The engine runs restic key passwd, and E19 backs up with the original password, rotates, backs up with the new password, then restores the latest revision. The old revision is explicitly not restored, and the old password is not explicitly tested for rejection. The dialog's preflight check is not a held maintenance lock.
- **Remaining:** Remove the unsafe automatic force-unlock behavior described in the separate audit defect. Exercise the actual dialog, verify old-password rejection, recover an old snapshot, and cover cancellation and failures while keeping the saved secret consistent with the repository.
- **Proof — open and look:** [Historical post-rotation restore validation](../audit-dashboard/evidence/E19-change-storage-password-historical.html) — Earlier test restored six files after rotation, matching file presence/size/metadata and one sampled byte comparison; it does not test an old snapshot or rejected old password.; [Fresh test results and their limits](../audit-dashboard/evidence/test-report.html) — 808/809 unit/visual and 37/39 local E2E passed. Open failed cases, focused rerun, test filters, screenshots and complete hash checks.
- **Limitations / adversarial review:** Historical validation covers six restored files, with one byte-compared sample; the screenshot shows a generic backup card and cannot demonstrate rotation. This audit did not rotate any real destination.
- **I need from you:** Inspect B037 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P2: UI copy overuses em dashes

- **Raised:** 2026-07-03
- **Type:** polish (copy)
- **Status:** Awaiting validation
- **Context:** User: the app contains many em dashes, which look weird
  in UI copy (and read as AI-written). Replace with natural punctuation
  (comma, period, colon, parentheses) in all user-facing strings unless
  an em dash is strictly the right choice. Code comments are out of
  scope.
- **Root cause + fix (short):** An audit classified ~1,900 em dashes
  across the source; ~174 were user-facing and were all rewritten
  (period/colon/semicolon/comma/parentheses; plain hyphen for window
  titles and name separators; middle dot inside status lines). Coupled
  test assertions were updated in the same pass. **Deliberately kept**
  (each is machine-matched or non-UI): the recommended-filter block
  marker lines (matched byte-for-byte in existing user configs), the
  Windows scheduled-task NAMES (persisted in Task Scheduler; the
  legacy-cleanup sweep pattern-matches them), log-file-only strings,
  the standalone "—" empty-value placeholder, and code comments. A
  final scan confirms zero remaining em dashes in strings the UI
  renders.
- **Historical proof:** Superseded by the current audit evidence below.
- **I need from you:** skim any screen you use often (Settings,
  editor, restore wizard); if a remaining em dash bothers you
  anywhere, point at it.

- **2026-10-02 audit:** R004: This item was already awaiting user validation in the interrupted roadmap. The current non-cloud run regenerated its scenario evidence where available; the old completion claim is not treated as acceptance.
- **Proof — open and look:** [Fresh scenario screenshot](../audit-dashboard/evidence/unit-visual/S40-filter-simulation-pane/screenshots/01-simulate-pane-populated.png) — Inspect the displayed controls and text for this roadmap item. Synthetic scenario data; not a live cloud or system-level test.; [Fresh test results and their limits](../audit-dashboard/evidence/test-report.html) — 808/809 unit/visual and 37/39 local E2E passed. Open failed cases, focused rerun, test filters, screenshots and complete hash checks.
- **I need from you:** Review the corresponding local dashboard item and explicitly accept it or describe the remaining work.

### P2: Folder-picker rows should toggle expand on click anywhere left of the checkbox

- **Raised:** 2026-06-11
- **Type:** bug (UX)
- **Status:** Awiting validation — but **scope narrowed** vs. original
  request.
- **Context:** Picker rows today require the user to click the small
  chevron to expand/collapse. The friendly behaviour: any click on the
  row that ISN'T the checkbox toggles expand/collapse — mirrors
  Explorer's pattern.
- **Plan:** Added `PointerPressed="OnTreeRowPointerPressed"` on the
  `SourceTreePickerWindow` row grid. Handler walks the click's source
  ancestors; if any is a `CheckBox` the click is left alone (selection
  semantics intact). Otherwise the bound `FileSystemNode.IsExpanded`
  flips. `Background="Transparent"` on the row grid ensures the entire
  row receives hit-tests, not just the painted children.
- **Historical proof:** Superseded by the current audit evidence below.

- **2026-10-02 audit:** R012: This item was already awaiting user validation in the interrupted roadmap. The current non-cloud run regenerated its scenario evidence where available; the old completion claim is not treated as acceptance.
- **Proof — open and look:** [Fresh scenario screenshot](../audit-dashboard/evidence/unit-visual/S29-lenient-folder-picker-click/screenshots/01-after-lenient-clicks.png) — Inspect the displayed controls and text for this roadmap item. Synthetic scenario data; not a live cloud or system-level test.; [Fresh test results and their limits](../audit-dashboard/evidence/test-report.html) — 808/809 unit/visual and 37/39 local E2E passed. Open failed cases, focused rerun, test filters, screenshots and complete hash checks.
- **I need from you:** Review the corresponding local dashboard item and explicitly accept it or describe the remaining work.

### P2: Filter editor wording + recommended-exclusion polish

- **Raised:** 2026-06-11
- **Type:** bug + cleanup
- **Status:** Awaiting validation
- **Context:** Three sub-issues in the filter editor's Advanced section.
  (a) Help text says `-glob` excludes case-sensitively, but the user
  doesn't actually type a `-` prefix anywhere — confusing instruction.
  (b) Recommended exclusions emit `ntuser.dat`, `ntuser.dat.LOG`,
  `ntuser.dat.LOG1`, `ntuser.dat.LOG2` as four separate lines when one
  `ntuser.dat*` line covers all of them. (c) Recommended exclusions
  go through bare globs; case handling is the
  platform-default in `FilterTranslator` — Windows already routes them
  through restic's `--iexclude`, so the case-insensitive coverage was
  already correct, just under-documented.
- **Plan:** Rewrote the help-text copy in
  `src/Backspace/Views/BackupEditorWindow.axaml:741` to drop the
  bogus `-glob` / `-i:glob` prefix references; the new text explains
  bare-glob semantics and notes the platform-default case behaviour.
  Replaced the four `ntuser.dat*` lines in
  `RecommendedFilters.EssentialPatterns` with a single `ntuser.dat*`
  glob that covers the whole family (including future `.blf` /
  `.regtrans-ms` siblings Windows adds).
- **Historical proof:** Superseded by the current audit evidence below.

- **2026-10-02 audit:** R013: This item was already awaiting user validation in the interrupted roadmap. The current non-cloud run regenerated its scenario evidence where available; the old completion claim is not treated as acceptance.
- **Proof — open and look:** [Fresh test results and their limits](../audit-dashboard/evidence/test-report.html) — 808/809 unit/visual and 37/39 local E2E passed. Open failed cases, focused rerun, test filters, screenshots and complete hash checks.
- **I need from you:** Review the corresponding local dashboard item and explicitly accept it or describe the remaining work.

### P2: Custom-exclusion markers should sit BEFORE Backspace-generated block

- **Raised:** 2026-06-11
- **Type:** UX
- **Status:** Awaiting validation
- **Context:** When the editor seeds a filter file, the Backspace-curated
  block (system + caches) lands at the top and the user's custom-edit
  section sits at the bottom. The user expects the opposite — they want
  the edit zone front and centre. Use explicit
  `# === Backspace custom exclusions — start ===` / `# === ... — end ===`
  fences to demarcate the edit zone unambiguously, and put it ABOVE the
  generated block.
- **Plan:** Added `CustomBlockHeader` / `CustomBlockFooter` constants
  to `RecommendedFilters`. Rewrote `MergeIntoFilters` to ALWAYS emit
  the custom block at the top of the file (with whatever the user
  had inside it preserved) and the recommended block below.
  Migration: any loose user-typed rules outside both marker pairs get
  pulled INTO the custom block on the first re-apply. Idempotent: a
  second apply with the same arguments produces the same output.
- **Historical proof:** Superseded by the current audit evidence below.

- **2026-10-02 audit:** R014: This item was already awaiting user validation in the interrupted roadmap. The current non-cloud run regenerated its scenario evidence where available; the old completion claim is not treated as acceptance.
- **Proof — open and look:** [Fresh test results and their limits](../audit-dashboard/evidence/test-report.html) — 808/809 unit/visual and 37/39 local E2E passed. Open failed cases, focused rerun, test filters, screenshots and complete hash checks.
- **I need from you:** Review the corresponding local dashboard item and explicitly accept it or describe the remaining work.

### P2: E2E coverage audit — user-action ↔ scenario matrix

- **Raised:** 2026-06-11
- **Type:** coverage / process
- **Status:** Open — reopened by the 2026-10-02 audit
- **Context:** Two production bugs in this session (Mount, long-include
  crash) shipped despite an existing scenario suite. The matrix the user
  wants: every action a user can take in the app, listed against the
  scenario(s) that exercise it, with gaps flagged.
- **Plan:** Walked menus, dialogs, per-card actions, the headless code
  path, and the maintenance/monitoring/edge-case surfaces. Mapped each
  action to one or more existing scenarios. Surfaced gaps in their own
  table column. The output landed in `docs/TESTING.md § Coverage
  matrix`. Top-of-file priority backlog: seven gaps worth filing as
  ROADMAP entries when active work picks them up (headless `--run`,
  battery/network preconditions, restore cancel, in-app browse,
  standalone prune/check, filter simulation, healthchecks/email).
- **Historical proof:** Superseded by the current audit evidence below.

- **2026-10-02 audit:** R015: The matrix is written, but many green cells prove a model or synthetic view rather than the promised operation. The audit evidence explicitly separates these and records the gaps.
- **Proof — open and look:** [Fresh test results and their limits](../audit-dashboard/evidence/test-report.html) — 808/809 unit/visual and 37/39 local E2E passed. Open failed cases, focused rerun, test filters, screenshots and complete hash checks.
- **I need from you:** Review the corresponding local dashboard item and explicitly accept it or describe the remaining work.

### P2: Custom Healthchecks management server does not control the ping host

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Open
- **Context:** Audit finding A003. Management requests use Monitoring.HealthchecksBaseUrl, but PingAsync always posts to https://hc-ping.com/{uuid}. The returned check PingUrl is not used for backup lifecycle pings. A check created on a custom Healthchecks instance will therefore not receive lifecycle pings at its own endpoint.
- **Audit finding:** Management requests use Monitoring.HealthchecksBaseUrl, but PingAsync always posts to https://hc-ping.com/{uuid}. The returned check PingUrl is not used for backup lifecycle pings. A check created on a custom Healthchecks instance will therefore not receive lifecycle pings at its own endpoint.
- **Remaining:** Persist/use the management response ping URL or expose a verified ping base, and cover a local self-hosted fake endpoint end to end. Alternatively remove unsupported custom-server configuration and document the limitation.
- **Proof — open and look:** [Audit overview](../audit-dashboard/evidence/audit-overview.html#A003) — source-inspection finding and exact missing validation; no completion proof claimed.
- **Limitations / adversarial review:** Confirmed URL-construction mismatch; no external ping sent. If custom-server support is intentionally out of scope, the user can choose to discard the feature rather than the mismatch being silently ignored.
- **I need from you:** Inspect A003 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P2: Dropbox with our own app ID

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B001: - Easily adding Dropbox, OneDrive, GoogleDrive destinations (consumer things) through RClone with our own app ID (so faster and we can control the scope).
- **Audit finding:** Dropbox has a built-in Backspace app ID, a browser authorization flow and an rclone connection probe. A fresh account round-trip was not run. The shared cloud-configuration overwrite and process-ownership defects still affect reliability.
- **Remaining:** After fixing the cloud configuration and process ownership defects, prove a real Dropbox connect, backup, restore and token refresh with a disposable folder.
- **Proof — open and look:** 🖼️ [Historical Dropbox editor](../audit-dashboard/evidence/historical/tests__Backspace.Tests__artifacts__scenarios__S08-destination-editor-variants__screenshots__05-dropbox-app-scoped.png) — Historical scenario image: Dropbox destination controls are present; this does not prove a successful OAuth or cloud backup.; [Fresh isolated audit probes](../audit-dashboard/evidence/cloud-config-probes.html) — Fresh isolated probe: generated OneDrive remote fails before authentication because drive identity is absent; a local-rclone control succeeds.
- **Limitations / adversarial review:** Fresh offline configuration probes confirm the OneDrive blocker and multi-cloud overwrite; no live account authorization/backup was attempted. Historical editor screenshots show controls only. The obsolete OAuth-provider documentation was corrected during this audit.
- **I need from you:** Inspect B001 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P2: Email delivery integration

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B008: - Email sending (does Restic have something equivalent to what we have?)
- **Audit finding:** Backspace owns email delivery through Resend and Mailgun HTTP APIs. Settings support provider credentials, recipients, per-event email gates and Send test email; the orchestrator calls mail after run history and final status have been persisted. No mail delivery is required from restic itself in this architecture.
- **Remaining:** Run a controlled provider delivery test and verify the received message, configured recipients and per-event gates. Add fake-HTTP tests covering success, authentication errors, rate limiting, timeout and retry behavior.
- **Proof — open and look:** [Audit overview](../audit-dashboard/evidence/audit-overview.html#B008) — source-inspection finding and exact missing validation; no completion proof claimed.
- **Limitations / adversarial review:** No emails were sent during the audit. The current retry tests test a string classifier rather than actual HTTP attempts. HttpClient timeout produces OperationCanceledException, which the retry helper rethrows immediately instead of retrying as its comments promise. The separate Mailgun warning requirement is not fulfilled.
- **I need from you:** Inspect B008 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P2: Useful logs for every operation

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B009: - Better logging info (per backup and per all other operations) like what we've achieved.
- **Audit finding:** The app has rolling application logs, per-backup run history, per-cell engine transcripts, live output and operation kinds for backup, restore, test restore, prune and check. LogStore retains 30 runs and retains each cell's referenced log. Application-log retention is separately configurable in days.
- **Remaining:** Review the current Logs view with fresh real operations and verify each operation's selected transcript and retention boundaries. Distinguish the last-30-runs history policy from the app-log day setting in the UI/docs. Resolve any current tests before accepting this broad request as complete.
- **Proof — open and look:** 🖼️ [Historical Logs view](../audit-dashboard/evidence/historical/tests__Backspace.Tests__artifacts__scenarios__S11-logs-view-populated__screenshots__01-logs-populated-past-run.png) — Synthetic historical scenario shows the backup selector and a populated transcript; the run selector says Pick a run, so it does not prove correct run selection.; 🖼️ [Historical live-output view](../audit-dashboard/evidence/historical/tests__Backspace.Tests__artifacts__scenarios__S11-logs-view-populated__screenshots__02-logs-live-tail-with-output.png) — Historical scenario image demonstrates the live-output presentation, not current real-engine completeness.
- **Limitations / adversarial review:** The linked historical screenshots are synthetic scenario output, not real backup/restore evidence. The historical screenshot still shows Pick a run while the terminal contains a run; it should not be used to certify selection behavior. No audit run has proven log completeness under concurrent operations or process restart.
- **I need from you:** Inspect B009 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P2: Review the referenced Word document

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B010: (also check Docx file to make sure all is good)
- **Audit finding:** No .docx file was found in the repository, including ignored files outside generated dependencies. The only getting-started documents found outside README/site belonged to the separate upstream Backrest checkout.
- **Remaining:** Identify or provide the specific Word document and intended getting-started guide before this review can be completed.
- **Proof — open and look:** [Audit overview](../audit-dashboard/evidence/audit-overview.html#B010) — source-inspection finding and exact missing validation; no completion proof claimed.
- **Limitations / adversarial review:** No claim is made about the missing document's correctness.
- **I need from you:** Inspect B010 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P2: Visible editing cursor in text areas

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B011: - Filters > Advanced: clicking on the textarea doesn't show an editing cursor which is counter intuitive. See if other places with editable textarea are affected as well.
- **Audit finding:** A real headless pointer click into Filters > Advanced focuses the editable pane and renders a blue caret at the start of the text. CaretBrush is configured globally. The TextBox Cursor property is unset, and the screenshot does not capture the native mouse pointer.
- **Remaining:** Verify the I-beam mouse pointer and blinking caret on the native Windows app; exercise other multiline editable fields, themes and empty text.
- **Proof — open and look:** 🖼️ [Fresh Filters click screenshot](../audit-dashboard/evidence/ui-probes/filters-after-real-pointer-click.png) — The left editable pane has a blue focus outline and caret at the start of '# Custom rules'; the right pane is read-only.
- **Limitations / adversarial review:** Probe exercises pointer focus, not keyboard edits or all textareas. Text is vertically centered within the tall pane, which may also make the insertion position less obvious.
- **I need from you:** Inspect B011 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P2: Separate drive letters for multiple mounts

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B012: - If we click Mount on multiple backup, will it reuse the same drive letter?
- **Audit finding:** Mounting an already-mounted backup reopens its existing letter. A different backup scans occupied drives from Z down to D, so after the first mount is visible, the next uses another letter. Allocation is a scan rather than a reservation: concurrent mounts can both choose the same free letter before either registers it.
- **Remaining:** Serialize or reserve drive-letter allocation and retry a collision. Add a two-backup simultaneous-mount test and show both drives in Explorer with their distinct contents.
- **Proof — open and look:** [Audit overview](../audit-dashboard/evidence/audit-overview.html#B012) — source-inspection finding and exact missing validation; no completion proof claimed.
- **Limitations / adversarial review:** E04 covers one mount only. Its historical screenshot shows the Backups card, not Explorer or the drive contents; it does not prove this multi-mount requirement.
- **I need from you:** Inspect B012 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P2: Replace Mount with Unmount after success

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B013: - If we clicked Mount on a backup AND it succeeded, it should replace the Mount button by Unmount.
- **Audit finding:** BackupCardViewModel exposes MountInExplorer only. ISnapshotMountRegistry exposes MountForAsync and DisposeAllAsync, with no per-backup unmount or mounted-state notification. The card retains its Mount label after success.
- **Remaining:** Add per-backup unmount and observable mounted state, switch the button only after successful mount, and restore Mount when unmounted or the drive disappears.
- **Proof — open and look:** 🖼️ [Historical post-mount card: still says Mount](../audit-dashboard/evidence/historical/tests__Backspace.Tests__artifacts__scenarios__E04-snapshot-mount-windows__screenshots__01-mounted-at-z.png) — This existing screenshot shows a Mount button after the scenario's mount step; it contains no Unmount control or mounted drive view.
- **Limitations / adversarial review:** The historical post-mount screenshot visibly still says Mount; it is negative evidence of the incomplete UX, not evidence that the underlying mount currently succeeds.
- **I need from you:** Inspect B013 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P2: Platform-aware notification wording

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B014: - Show a Windows Notification => Where this string is used, confirm it is still platform neutral (windows or macos would show up instead if Backspace was running on those OSes).
- **Audit finding:** All three event rows literally say 'Show a Windows notification'; the failure explanation also says Windows/Focus Assist. These strings do not change by platform. The notification implementation is a custom Avalonia popup, not a native Windows notification-center toast.
- **Remaining:** Use platform-neutral wording or platform-specific bindings and verify non-Windows labels.
- **Proof — open and look:** 🖼️ [Current notification wording](../audit-dashboard/evidence/ui-probes/notifications-platform-copy.png) — Success, skip and failure controls all say 'Show a Windows notification'.
- **Limitations / adversarial review:** Windows screenshot confirms current copy; macOS/Linux runtime behavior was not executed.
- **I need from you:** Inspect B014 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P2: Roadmap requires proof and explicit validation

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B020: Whenever I ask you about a bug or feature, you need to add it first to ROADMAP, at the top (actively working on list of items). This way you'll never forget about them. And to remove them, you need to tell the agent that he absolutely should PROVE TO ME with a screenshot/screenshots/test results or any adequate proof that the issue has been taken care of. This is an important workflow I want going forward to not waste time reporting things where are always half fixed. This ROADMAP will be our source of truth (top will be an immediate todo list, our RAM with strict criteria to remove items, only with my validation I am satisified with the proofs youve given). Clearly outlien this process in Agent.MD and follow it systematically. Start followoing it with all the items i just asked you about.
- **Audit finding:** AGENTS.md states the requested rule: log requests first, require inspectable evidence, and remove items only after explicit user validation. The previous roadmap nevertheless contains stale validation claims, nonvisual proof and optimistic coverage statements. This audit has reintroduced each note individually and leaves all acceptance boxes unchecked.
- **Remaining:** Use the dashboard decisions to validate or redirect each item. When asked to review comments, reconcile explicit decisions into ROADMAP without treating silence or a passing happy-path test as approval.
- **Proof — open and look:** [Fresh test results and their limits](../audit-dashboard/evidence/test-report.html) — 808/809 unit/visual and 37/39 local E2E passed. Open failed cases, focused rerun, test filters, screenshots and complete hash checks.
- **Limitations / adversarial review:** This is a process requirement. Written instructions prove the rule exists, not that every historical completion claim complied with it.
- **I need from you:** Inspect B020 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P2: Explain scheduling independently of the tray

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** Original note B022: Also it is misleading to say "Backspace keeps running in the background so scheduled backups still fire"! App doesn't need to run in the background for this! User should be reassured about this. Only useful to keep to reopen fast. How are things handled if a backup or any other operation is running and user wants to quit? I think we should still push to keep app running in the systray if you can confirm CPU impact will be zero when no operation is running. This can be reassuring for people to see Backspace if there. But we should reassure them even if Backspace is quit, it'll stop existing operations but scheduled operations will be triggered as usual. Find the best way to frame this.
- **Audit finding:** Settings explicitly states scheduled backups run even when Backspace is not open, and describes tray retention as faster reopening.
- **Remaining:** User wording approval; real reboot/scheduler firing proof remains its own open item.
- **Proof — open and look:** 🖼️ [Tray and schedule explanation](../audit-dashboard/evidence/ui-probes/tray-and-schedule-copy.png) — The blue note says scheduled backups run even if Backspace is not open, and mentions active-run cancellation.
- **Limitations / adversarial review:** Copy capture does not prove Windows Task Scheduler actually fires after reboot.
- **I need from you:** Inspect B022 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P2: Measure idle tray CPU use

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B024: Also it is misleading to say "Backspace keeps running in the background so scheduled backups still fire"! App doesn't need to run in the background for this! User should be reassured about this. Only useful to keep to reopen fast. How are things handled if a backup or any other operation is running and user wants to quit? I think we should still push to keep app running in the systray if you can confirm CPU impact will be zero when no operation is running. This can be reassuring for people to see Backspace if there. But we should reassure them even if Backspace is quit, it'll stop existing operations but scheduled operations will be triggered as usual. Find the best way to frame this.
- **Audit finding:** The UI says tray mode uses essentially no CPU, but no idle measurement was found. Periodic scheduler and UI work mean a literal zero-CPU promise is not established.
- **Remaining:** Measure a quiet tray session with no active operations and report duration, CPU time and memory. Decide on accurate user-facing wording.
- **Proof — open and look:** [Audit overview](../audit-dashboard/evidence/audit-overview.html#B024) — source-inspection finding and exact missing validation; no completion proof claimed.
- **Limitations / adversarial review:** Code inspection cannot substitute for a resource-use measurement. This audit did not open the user’s configured app for an idle benchmark.
- **I need from you:** Inspect B024 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P2: Check policy remains fully reachable

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B031: - Check policy doesn't show fully even when scrolled down max (see screenshot).
- **Audit finding:** At the supported minimum 980×640 window, scrolling fully down reveals all Check policy controls. Its card bottom is 2 pixels below the viewport (369 versus 367), so the complete card remains slightly clipped. The existing S26 screenshot never scrolled down and did not show the policy cards at all.
- **Remaining:** Correct the residual scroll extent/clipping and verify normal/minimum window sizes with native scaling.
- **Proof — open and look:** 🖼️ [Check policy at maximum scroll](../audit-dashboard/evidence/ui-probes/check-policy-at-scroll-bottom.png) — Cadence and time controls are visible; the card's bottom edge is cut off just above the fixed action buttons.
- **Limitations / adversarial review:** Fresh headless screenshot reproduces a small 2 px card-edge defect, not missing input controls; the original reported screenshot was not attached.
- **I need from you:** Inspect B031 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P2: Document snapshot browsing and mounting

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B033: - Make sure to update the docs and readme to reflect that backspace supports snapshot browsing like backrest, and mounting (exclusive feature). The Getting started guide will have to reflect that as well, but for now no need to regenerate screenshots, we'll do that once the app is actually working.
- **Audit finding:** README and website docs describe browsing and mounting. This audit corrected README/GOTCHAS to describe the actual WinFSP path, repaired AGENTS.md references, and corrected obsolete OAuth-provider and destructive-confirmation documentation. The unavailable Word/getting-started document and product behavior still require review.
- **Remaining:** Review the current docs and provide the referenced Word/getting-started document location if it is still relevant. Product behavior claims must be reconciled with the open safety and cloud findings.
- **Proof — open and look:** [Audit overview](../audit-dashboard/evidence/audit-overview.html#B033) — source-inspection finding and exact missing validation; no completion proof claimed.
- **Limitations / adversarial review:** No screenshots were regenerated specifically for website publication.
- **I need from you:** Inspect B033 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P2: Interactive OAuth test switch and reusable tokens

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B034: - To simplify the obtaining of tokens for the test, and also test at the same time what a user would face for this, please add a config flag for the test (set to false by default, can be changed by the developer manually in the tests project main file) that'll make the test always show the UX to the user while testing while trying to get the token, so the user can interact only at that point. Then save for all further tests. This way if the E2E test suite runs, it'll reuse any existing tokens, and always ask the developer if a token is missing (or the always ask config flag is set to true, only helpful when trying to test specifically the auth screens).
- **Audit finding:** E2EConfig.AlwaysShowOAuthUx defaults false; AcquireMissingTokensInteractively defaults true; BACKSPACE_E2E_NONINTERACTIVE=1 disables interaction. The helper uses the same production OAuth coordinator and persists tokens for reuse. It bypasses the destination editor button/status UI, so it does not establish that complete user-facing setup works.
- **Remaining:** Complete provider configuration and run an explicit interactive test of the editor and callback; reconcile stale E2EConfig comments still claiming rclone config create.
- **Proof — open and look:** [Fresh visual scenario report — October 2](../audit-dashboard/evidence/unit-visual/index.html) — 48 scenario bodies completed; the xUnit run separately failed S36 on dispatcher cleanup, and focused S36 rerun passed. Report alone must not be treated as full-suite green.
- **Limitations / adversarial review:** No cloud accounts or credentials were touched. Legacy Cloud tests have a separate BACKSPACE_TEST_INTERACTIVE gate.
- **I need from you:** Inspect B034 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P2: Measure cloud-file hydration after backup

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B035: - Test if --exclude-cloud-files is OFF, whether it'll cause the files to be downloaded permanently, or only temporarily (useful to backup dropbox to another destination without cluttering the local drive)/
- **Audit finding:** ExcludeCloudFiles defaults true and emits --exclude-cloud-files on Windows. A helper explains that disabling it hydrates local files and that Backspace does not automatically free them afterward. However, the tests only inspect ordinary local files/missing paths and compare explanation strings; they never create or read a real cloud placeholder.
- **Remaining:** Use a small disposable online-only file from each relevant installed cloud provider; record placeholder attributes and allocated disk space before backup, immediately after and later; restore and compare bytes. Preserve the source file and avoid automatic de-hydration without explicit intent.
- **Proof — open and look:** [Audit overview](../audit-dashboard/evidence/audit-overview.html#B035) — source-inspection finding and exact missing validation; no completion proof claimed.
- **Limitations / adversarial review:** The code's claim of empirically established provider behavior has no accompanying provider experiment or artifact in this suite. Provider cleanup policies can vary; the current unit tests do not answer the user's permanence question.
- **I need from you:** Inspect B035 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P2: Check layout with ten sources and destinations

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** Original note B038: Test adding 10 sources and destinations to see how it looks (scrollbar)
- **Audit finding:** A new audit-only fixture loads ten sources and ten destinations in one backup. Both lists have independent scrollbars and can display item 10; the action shelf stays visible. Editor top/bottom captures are available too.
- **Remaining:** User visual approval of layout; optional native mouse-wheel test at high display scaling.
- **Proof — open and look:** 🖼️ [Ten-source/destination card, top](../audit-dashboard/evidence/ui-probes/ten-sources-destinations-top.png) — Counts read 10 SOURCES and 10 DESTINATIONS; independent list scrollbars keep actions visible.; 🖼️ [Both lists scrolled to the bottom](../audit-dashboard/evidence/ui-probes/ten-sources-destinations-bottom.png) — Source-10 and Destination 10 are visible at the bottom of their lists.; 🖼️ [Ten-source editor, top](../audit-dashboard/evidence/ui-probes/ten-sources-editor-top.png) — The source list occupies the scrollable form, with Save buttons fixed below.
- **Limitations / adversarial review:** Data is seeded directly into view models; this does not prove ten repetitions of picker/add/save interactions or ten actual backup destinations.
- **I need from you:** Inspect B038 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P2: Review the sidebar grouping and label

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B039: Maybe move the down menu block right below Destinations, see what to do with label Your Backups, ask Claude for advice.
- **Audit finding:** The old sidebar YOUR BACKUPS label is removed. Settings and Activity & logs remain docked at the bottom rather than immediately beneath Destinations. The request to 'maybe' move that block is a design decision, not a completed move.
- **Remaining:** Confirm the current grouping or choose a compact top-aligned navigation block.
- **Proof — open and look:** 🖼️ [Current sidebar with an active restore](../audit-dashboard/evidence/unit-visual/S31-restores-nav-item/screenshots/01-one-restore-in-flight.png) — Main navigation is above; Settings and Activity & logs remain in the bottom footer.
- **Limitations / adversarial review:** The page still appropriately has a 'Your backups' heading; only the extra sidebar heading was removed.
- **I need from you:** Inspect B039 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P2: Prove the schedule fires after a real reboot

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** Original note B040: Before pointing it at anything you care about, do this once with a throwaway folder: - Reboot the machine — wait for the scheduled task to fire on its own, confirm a second snapshot appears. - Restore the first snapshot to a different folder. - fc /b (or PowerShell Get-FileHash) the restored files against the originals.
- **Audit finding:** Scheduler argument/unit tests exist, but they do not reboot Windows or establish that an OS task independently creates the second snapshot. No reboot was performed during this audit.
- **Remaining:** Arrange a controlled throwaway scheduled backup, reboot, and record task history plus second snapshot; preserve the original fixture for restore/hash validation.
- **Proof — open and look:** [Audit overview](../audit-dashboard/evidence/audit-overview.html#B040) — source-inspection finding and exact missing validation; no completion proof claimed.
- **Limitations / adversarial review:** Existing TaskSchedulerCmdWrapperTests assert strings only. No real OS task was created or modified during this audit.
- **I need from you:** Inspect B040 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P2: Restore the first snapshot to a different folder

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** Original note B041: Before pointing it at anything you care about, do this once with a throwaway folder: - Reboot the machine — wait for the scheduled task to fire on its own, confirm a second snapshot appears. - Restore the first snapshot to a different folder. - fc /b (or PowerShell Get-FileHash) the restored files against the originals.
- **Audit finding:** The fresh E22 real-engine scenario restored an older snapshot to a separate folder. An additional complete SHA-256 check confirms all five restored files match the original snapshot fixture and differ from the later edited source.
- **Remaining:** Review the full-hash report for this restore step. The real reboot and automatic scheduled-run acceptance sequence remains separately open in B040.
- **Proof — open and look:** [Fresh complete SHA-256 comparison](../audit-dashboard/evidence/full-hash-proof.html) — Every file is hashed, not sampled: older revision 5/5 match, ordinary round-trip 40/40 match, edge fixture 8/8 regular files match. Mutated-source negative controls differ.
- **Limitations / adversarial review:** This proves an older-revision restore from a disposable local repository. It does not prove a real reboot, cloud restore, occupied-target safety or every restore edge case.
- **I need from you:** Inspect B041 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P2: Compare restored files with the original hashes

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** Original note B042: Before pointing it at anything you care about, do this once with a throwaway folder: - Reboot the machine — wait for the scheduled task to fire on its own, confirm a second snapshot appears. - Restore the first snapshot to a different folder. - fc /b (or PowerShell Get-FileHash) the restored files against the originals.
- **Audit finding:** Fresh independent SHA-256 manifests compare every restored file in the selected fixtures: all 5 older-revision files, all 40 ordinary round-trip files and all 8 regular edge-fixture files match. The older-revision check also proves the five later-edited files differ, so it can detect the wrong snapshot.
- **Remaining:** Open the full-hash report and decide whether this comparison satisfies the requested step. Repeat it as part of the still-open reboot acceptance sequence after the restore safety defects are fixed.
- **Proof — open and look:** [Fresh complete SHA-256 comparison](../audit-dashboard/evidence/full-hash-proof.html) — Every file is hashed, not sampled: older revision 5/5 match, ordinary round-trip 40/40 match, edge fixture 8/8 regular files match. Mutated-source negative controls differ.
- **Limitations / adversarial review:** This is complete byte verification for these specific fixtures, not a guarantee about all backups. The built-in verifier and ordinary scenario helper still sample bytes.
- **I need from you:** Inspect B042 in the dashboard and mark it complete, comment on the remaining work, or explicitly approve discarding it.

### P2: Audit interrupted work against the original notes

- **Raised:** 2026-10-02
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** Thoroughly audit the interrupted implementation, tests, docs and original notes; preserve each request and distinguish implementation from proof.
- **Result:** 76 issues in the local dashboard, including every original request and the older active roadmap entries. No existing item was deleted.
- **Proof — open and look:** [Audit overview](../audit-dashboard/evidence/audit-overview.html) — test results, per-issue evidence and remaining work; [dashboard screenshot](../audit-dashboard/evidence/dashboard-overview.png) — separate issues, evidence links and user-controlled completion.
- **Limitations / adversarial review:** Real-account OAuth/email, VSS, reboot, native simultaneous mounts and idle CPU remain unproven. Restore safety and persistence probes reproduce negative cases, and complete hash checks include a wrong-revision control. Historical screenshots are not promoted to fresh results.
- **I need from you:** Review the dashboard and identify which findings or scope need revision.

### P3: "Restores" left-nav item — single-restore variant shipped

- **Raised:** 2026-06-11
- **Type:** feature
- **Status:** Open — reopened by the 2026-10-02 audit
  multi-restore parallel-cards evolution stays Open
- **What landed this turn (single-restore):**
  - New `HasActiveRestores` observable property on
    `MainWindowViewModel` mirrors `RestoreViewModel.IsRunning`.
  - New `Icon.ArrowDownTray` (Lucide-style stroked path) +
    `PathIcon.active-restore-icon` style with a gentle 1.2 s bob
    animation (sine-ease, ±2 px) matching DESIGN.md "calm motion".
  - New left-nav `RadioButton.nav` between Destinations and the
    footer, conditionally `IsVisible="{Binding HasActiveRestores}"`.
    Click → switches to the existing `Restore` view (whose
    `IsRunning` guard from the earlier partial-fix commit
    preserves the in-flight progress state).
- **What's still pending (multi-restore evolution):** A separate
  `NavItem.ActiveRestores` with a cards view listing every parallel
  restore + per-card click-to-resume. Today's `RestoreViewModel` is a
  singleton; multi-restore requires a registry keyed by request id.
  Filed as the next layer of this entry, no priority until a user
  asks.
- **Historical proof:** Superseded by the current audit evidence below.
- **Known gap (per AGENTS.md rule 7):** No automated test. The
  scenario shape: start a long restore, navigate to Backups, assert
  the "Restores" nav-item is visible, click it, assert progress is
  intact. Filed to E2E revamp queue.

- **2026-10-02 audit:** B016: The Restores sidebar item appears while the registry has sessions, a download icon has an animation style, and navigating back preserves a running single restore wizard. Cards list registered sessions. However ActiveRestoreSession does not implement property-change notifications for its bindings, the real restore engine reports progress only at the end, and OpenSession ignores which card was chosen. A newly started restore also does not automatically select the Restores navigation item. B017: A download-icon animation style exists on the Restores navigation item. The static scenario picture cannot demonstrate motion, and the real restore progress path is incomplete.
- **Proof — open and look:** [Historical layout proof: two restore cards](../audit-dashboard/evidence/historical/tests__Backspace.Tests__artifacts__scenarios__S31-restores-nav-item__screenshots__03-two-cards-parallel.png) — Two injected restore sessions render as separate cards, both at 0%; this does not show live progress or correct card navigation.
- **I need from you:** Review the corresponding local dashboard item and explicitly accept it or describe the remaining work.

### P3: "Restores" multi-restore cards — shipped

- **Raised:** 2026-06-12
- **Type:** feature
- **Status:** Open — reopened by the 2026-10-02 audit
- **What landed:**
  - `ActiveRestoreRegistry` singleton service tracks N concurrent
    restore sessions in an `ObservableCollection<ActiveRestoreSession>`.
    Sessions register on `Begin`, update via `UpdateProgress`, end
    via `End`. End fades the card from the collection after 4 s
    (same UX rhythm as a transient toast).
  - Engine integration: `RestoreEngine.RunAsync` calls
    `ActiveRestores.Begin` on start, forwards the engine's
    `onProgress` events to `UpdateProgress`, and calls `End` on
    both clean exit and the catch block (so a thrown restore still
    leaves a visible failure card briefly).
  - `NavItem.ActiveRestores` + `ActiveRestoresViewModel` +
    `ActiveRestoresView.axaml`: cards list with empty-state
    fallback. Each card shows backup name, destination, target
    path, progress bar, "X of Y files" + status. Click a card to
    return to the Restore page (single-VM today; per-session VM
    scoping queued).
  - `MainWindowViewModel.HasActiveRestores` rebound to the
    registry's `HasAny` so the sidebar item's visibility tracks
    the registry, not just the Restore VM's IsRunning.
  - Concurrency: registry centralises UI-thread marshalling
    (engine fires from thread pool; ObservableCollection mutations
    must hit the dispatcher).
- **Historical proof:** Superseded by the current audit evidence below.
- **Known gap (per AGENTS.md rule 7):** Per-session distinct VM
  scoping. Today's RestoreViewModel is still a singleton, so
  clicking a card returns to whichever restore the wizard is
  currently focused on, not necessarily the one the user clicked
  on (in the rare multi-restore case). The scenario covered: a
  user with ONE in-flight restore sees the card and navigates back
  to its progress. Multi-restore parallel-pick is queued behind a
  real-user case.

- **Raised:** 2026-06-11
- **Type:** feature
- **Status:** Partial fix shipped; full nav-item still Open
- **Context:** Today, starting a restore and navigating away forfeits
  the live progress view — no way back without re-initiating. User
  wants a left-nav item that appears the moment a restore starts
  (mirroring how the syncing taskbar icon currently animates), shows a
  list of active restore operations as cards, and lets the user click
  a card to return to the in-flight restore screen state. The nav
  item should highlight in the active-accent colour like Backups /
  Destinations do today.
- **What this turn fixed (minimal):** Adjusted
  `MainWindowViewModel.OnSelectedNavChanged` to skip `RestartWizard()`
  when `_restore.IsRunning == true`. The user can now click Backups /
  Destinations / Logs / Settings during a restore and come back to
  find the live progress view intact. This is the most painful
  symptom; not the full feature.
- **What's still pending (the full feature):** A separate
  `NavItem.ActiveRestores` with conditional visibility (shows only
  while a restore is in flight), a cards view listing each active
  operation, an animated icon (reuses the existing `AppStatus`
  syncing lease infrastructure), and click-to-return semantics that
  pick the right tab for multi-restore parallelism. Sketch lives
  here — implement when prioritised:
  1. Add `NavItem.ActiveRestores` to the enum.
  2. Bind sidebar entry's `IsVisible` to
     `MainWindowViewModel.HasActiveRestores` (computed from a new
     `ActiveRestoreRegistry` service that the engine populates).
  3. Replace the single `RestoreViewModel` field with a dictionary
     keyed by restore-request-id so parallel restores each retain
     their own VM state.
  4. New `ActiveRestoresView` with cards bound to the registry's
     observable collection; click → SelectedNav = Restore + load
     that key's VM as `CurrentPage`.
  5. Animated icon — pre-render frames per accent in the syncing-
     icon pipeline (see `docs/icon.md`); switch icon resource on
     `HasActiveRestores` flip.
- **Historical proof:** Superseded by the current audit evidence below.

- **2026-10-02 audit:** B018: The Restores screen draws multiple cards, but its session objects do not raise property-change notifications for the bound fields. Real engine progress is reported only at the end, so the existing card-count scenario does not prove live status. B019: The OpenSession handler ignores the chosen session and returns to a shared Restore view model. It cannot reliably reopen the specific card the user selected.
- **Proof — open and look:** [Historical layout proof: two restore cards](../audit-dashboard/evidence/historical/tests__Backspace.Tests__artifacts__scenarios__S31-restores-nav-item__screenshots__03-two-cards-parallel.png) — Two injected restore sessions render as separate cards, both at 0%; this does not show live progress or correct card navigation.
- **I need from you:** Review the corresponding local dashboard item and explicitly accept it or describe the remaining work.

### P3: OneDrive + Google Drive OAuth client_ids

- **Raised:** 2026-06-12 (audit of BACKSPACE_PORT_DESIGN.md § 6.2)
- **Type:** feature
- **Status:** Open — reopened by the 2026-10-02 audit
  the official apps OR users to provide BYO client_ids
- **Context:** `OAuthAppRegistry` ships with empty strings for both
  client_ids today; providers are wired and ready, the only gap is
  one of you / a BYO user pasting a client_id in.
- **What landed this turn (provider code):**
  - [OneDriveOAuthProvider.cs](../src/Backspace/Services/OAuth/OneDriveOAuthProvider.cs) —
    Microsoft Identity Platform v2.0, "common" tenant (Personal +
    Business under one app reg), `Files.ReadWrite.All` +
    `offline_access`.
  - [GoogleDriveOAuthProvider.cs](../src/Backspace/Services/OAuth/GoogleDriveOAuthProvider.cs) —
    Google Identity Platform, `drive.file` scope (NOT `drive` —
    dodges CASA Tier-2), `access_type=offline` + `prompt=consent`
    (Google's quirk: prompt=consent is what GUARANTEES the
    refresh_token comes back, not just access_type=offline).
  - [OAuthProviderFactory.cs](../src/Backspace/Services/OAuth/OAuthProviderFactory.cs) —
    wired both. Returns null only when the client_id is empty in
    BOTH the user's BYO override and the baked-in registry, so the
    UI surfaces "OAuth not yet wired" rather than dispatching a
    400-bound authorize request.
  - [docs/OAUTH_REGISTRATION.md](OAUTH_REGISTRATION.md) — step-by-
    step Entra + Google Cloud Console guide.
- **Historical proof:** Superseded by the current audit evidence below.
- **I need from you:** two app registrations and their client IDs.
  Step-by-step in [docs/OAUTH_REGISTRATION.md](OAUTH_REGISTRATION.md).
  1. Register the OneDrive app in [Entra](https://entra.microsoft.com/).
     Paste the Application (client) ID into
     `OAuthAppRegistry.OneDriveClientId`.
  2. Register the Google Drive app in
     [Cloud Console](https://console.cloud.google.com/). Use the
     `drive.file` scope (NOT `drive` — the audited variant is
     deliberately avoided). Paste the Client ID into
     `OAuthAppRegistry.GoogleDriveClientId`.

  Until those land, BYO users can paste their own client_ids via
  Settings → OAuth and the providers will pick them up. The
  in-app "not wired" surface is the only thing gated on the
  baked-in registry values.

- **2026-10-02 audit:** B002: OneDrive provider code exists, but the official app ID is empty. Even with an override, the generated remote lacks the required drive identity, so registration alone will not make the feature usable. B003: Google Drive provider code exists, but its official app ID is empty. OAuth requests drive.file while the generated rclone section declares drive. Registration, scope alignment and a real account round-trip remain.
- **Proof — open and look:** [Historical Dropbox editor](../audit-dashboard/evidence/historical/tests__Backspace.Tests__artifacts__scenarios__S08-destination-editor-variants__screenshots__05-dropbox-app-scoped.png) — Historical scenario image: Dropbox destination controls are present; this does not prove a successful OAuth or cloud backup.; [Fresh isolated audit probes](../audit-dashboard/evidence/cloud-config-probes.html) — Fresh isolated probe: generated OneDrive remote fails before authentication because drive identity is absent; a local-rclone control succeeds.
- **I need from you:** Review the corresponding local dashboard item and explicitly accept it or describe the remaining work.

### P3: Delete BACKSPACE_PORT_DESIGN.md once port-plan gaps close

- **Raised:** 2026-06-12 (user request)
- **Type:** docs cleanup
- **Status:** Open — reopened by the 2026-10-02 audit
- **Context:** `BACKSPACE_PORT_DESIGN.md` is the historical port brief.
  The architecture it describes is shipped; only four items remain
  as `❌` or `🟡` per its §14 audit. The doc's stated end-state is to
  be deleted once those close — the `README` + `AGENT` + `docs/ARCHITECTURE`
  set covers the current contributor-facing picture.
- **Plan:** Delete the file once all of these close:
  1. **OneDrive + Google Drive OAuth client_ids** (entry below)
  2. **rclone orphan-safe kill** (entry below)
  3. **Auto-update channel** (entry below)
  4. **Mount feature actually working** (separate P0 entry above)
- **Proof:** _pending — depends on the four entries above closing._

- **2026-10-02 audit:** R017: The local historical port plan still exists. Its removal is destructive housekeeping and remains a user decision; this audit did not delete it.
- **Proof — open and look:** [Fresh test results and their limits](../audit-dashboard/evidence/test-report.html) — 808/809 unit/visual and 37/39 local E2E passed. Open failed cases, focused rerun, test filters, screenshots and complete hash checks.
- **I need from you:** Review the corresponding local dashboard item and explicitly accept it or describe the remaining work.

### P3: Auto-update channel — light variant

- **Raised:** 2026-06-12 (audit of BACKSPACE_PORT_DESIGN.md § 13)
- **Type:** infrastructure
- **Status:** Open — reopened by the 2026-10-02 audit
  variant deferred)
- **Context:** Today users download a fresh zip from Releases when
  they want to update. The light variant — "check GitHub Releases
  on launch + notify if newer" — keeps the trust boundary clean
  (user sees the version they're running + picks when to switch)
  while removing the "you didn't know there was an update" friction
  the prior plan flagged. Heavy variants (Squirrel.Windows,
  Sparkle) defer until user volume justifies the operational cost.
- **Plan:**
  - `UpdateChecker.Begin()` fires a thread-pool task on launch,
    queries `api.github.com/repos/backspace-backup/backspace/releases/latest`,
    compares the parsed tag to the running assembly's
    `AssemblyInformationalVersion`. If newer: non-blocking toast
    with the release URL.
  - Failure-isolated everywhere: no network, GitHub rate limit, DNS
    blip, prerelease tag, draft, unparseable version — all silent
    skips. The user never sees an "update check failed" notification
    because the worst-case is "we check again next launch."
- **Historical proof:** Superseded by the current audit evidence below.
- **Known gap (per AGENTS.md rule 7):** No automated test. The
  realistic shape: serve a fake GitHub-shaped response over a
  loopback HttpListener, point UpdateChecker at it, assert the
  notification fires with the right URL. Filed to E2E revamp.
  Heavy auto-update path (download + relaunch) remains Open for
  user direction when volume justifies it.

- **2026-10-02 audit:** R018: The production check reads GitHub releases and displays release text, but supplies no notification click callback. The scenario constructs a synthetic toast and does not drive the real update checker, so it cannot prove release discovery or navigation.
- **Proof — open and look:** [Fresh test results and their limits](../audit-dashboard/evidence/test-report.html) — 808/809 unit/visual and 37/39 local E2E passed. Open failed cases, focused rerun, test filters, screenshots and complete hash checks.
- **I need from you:** Review the corresponding local dashboard item and explicitly accept it or describe the remaining work.

### P3: Local issue review dashboard with persistent discussions

- **Raised:** 2026-10-02
- **Type:** feature
- **Status:** Awaiting validation
- **Context:** An uncommitted repository-root subproject with Chrome dashboard, BAT launcher, exact original quotes, evidence, completion checkboxes, threaded user/agent replies and JSON persistence.
- **Proof — open and look:** 🖼️ [Dashboard](../audit-dashboard/evidence/dashboard-overview.png) — issue list and detailed evidence; [persistence verification](../audit-dashboard/evidence/dashboard-verification.html) — browser/restart, conflict and thread checks passed on disposable review data.
- **Limitations / adversarial review:** Tests exercise negative requests and concurrent edits as well as the happy path. A draft-while-saving loss was found in review and fixed. Posted activity persists; unposted drafts remain in browser memory. Dashboard and launcher are locally Git-excluded.
- **I need from you:** Launch the dashboard, inspect an issue and record your decision or comment.

## Adversarial self-review (2026-06-12)

Per AGENTS.md rule 7. Each `Awaiting validation` item gets one
honest "what would still trip this up" pass before claiming
"shipped." Findings either become inline fixes (resolved this turn)
or get logged as `Known gap` on the item.

### Fixed this turn from the review

- **Item 1 (lenient click target).** Handler triggered on right-
  click too — would expand a node when the user wanted a context
  menu. Now gated on `IsLeftButtonPressed`. _Inline fix landed in
  this commit._
- **Item 2 (savings label).** Original branch said "Nothing matched —
  that's normal for narrow folders" even when the walk had
  truncated with zero matches in a LARGE source. Added a fourth
  branch ("Walk hit the time cap before finding a match. Try again")
  and a regression test. _Inline fix landed in this commit._

### Known gaps surviving the review (Status: Awaiting validation
*with* documented limitation)

- **Item 9a (long-include).** E34 proves the `--include-file` path
  works at 1500 files. It DOESN'T combine the bracket-fallback with
  large file counts in one test. If someone restores 1500 files
  where one has `[` in its name, two code paths fire together (bracket
  substitution + post-restore sweep + tempfile write) — the bracket-
  fallback's sweep iterates `Directory.EnumerateFileSystemEntries`
  recursively, which on 1500 files is O(1500) but well-tested
  separately. Risk: low.
- **Item 9b (folder collapse).** The minimizer's algorithm is unit-
  tested (9 cases). The WIRING — RestoreViewModel → RestoreRequest →
  RestoreEngine → ResticBackrestEngine reading IncludePatterns — is
  code-read-verified, not end-to-end asserted. A regression where
  `_currentRequest.IncludePatterns` accidentally goes null because
  somebody refactors the init flow would silently fall back to
  per-file patterns and pass tests. Mitigated by code structure
  (required init list) but not bullet-proof. Risk: low-to-medium.
- **Item 5 (skipped-run alerts visibility).** Unit-tested at the
  model layer. No scenario asserts the actual `IsVisible` binding
  in the live UI for either Easy or Advanced mode. A typo in the
  XAML binding (e.g. `ShowGraceBlock` vs `ShouldShowGraceBlock`)
  would silently leave the block always-hidden or always-shown.
  Risk: low.
- **Item 7 (collapsible hover).** Pure XAML style change, no
  test. Relies on the next visual-scenario sweep catching it.
  Risk: low — Avalonia would error on a typo'd selector at startup.
- **Item 4 (custom-exclusion markers).** Handles single-pair
  markers cleanly. Doesn't gracefully handle the user manually
  inserting a SECOND custom-marker pair (e.g. via copy-paste). The
  second pair's content would get pulled into the first on
  re-apply. Probably harmless; documenting for the next reader.
- **Item 8 (restore state preservation).** The fix is one
  line — `!_restore.IsRunning`. No automated test asserts "nav
  during running restore preserves state." A future regression
  where `IsRunning` flips false earlier than expected would
  silently reintroduce the bug. Risk: medium — manual testing is
  cheap, automated coverage gap is real.
- **Item 6b (coverage matrix).** Doc-only. Its accuracy depends
  on someone keeping it current as new scenarios land. Mitigated by
  the acceptance criterion ("every action shows a scenario name or
  ❌"); enforcement is social.

### Pattern observed

The recurring gap shape: **unit tests prove the algorithm, no
test proves the integration.** The folder-collapse, savings label,
and grace-block items all share this shape. Future scenarios for
the gaps above should assert the user-visible BEHAVIOUR (click X,
see Y), not just the underlying mechanism (X.Y() returns Z). That
discipline is what would have caught the Mount false-green earlier.

## Scale-test toggle (proposed alongside this work)

User asked for a global flag that bumps every E2E fixture to a much
larger size (~2 GB, tens of thousands of files), enabled only before
pushing a commit. The intent: catch class-of-scale bugs (like the
32 KB command-line one) without paying their runtime cost on every
local iteration.

**Shape:**
- New env var `BACKSPACE_E2E_SCALE=huge` (default: today's matrix).
- When set, every scenario that takes a `FixtureSize` parameter uses
  `Huge` regardless of what the test attribute says.
- `Huge` fixture targets: ~2400 files per source × 3 sources, file sizes
  sampled up to ~3 MB so the total lands ~2 GB. Hardcoded ceilings to
  avoid runaway local disk use.
- The new P0 regression test `E34_RestoreLongIncludeList` always uses
  ≥1500 files so it catches the 32 KB crash even WITHOUT the env var —
  the scale-toggle is a belt-and-suspenders for everything else.

## Test coverage gaps

Concrete and actionable:

- **OneDrive / Google Drive / S3 interactive E2E.** Copy of
  `E07_DropboxRoundTrip` for each provider — swap the destination
  `Kind` + auth helper. Gated behind a per-provider secret in
  `tests/Backspace.Tests/secrets.env`.
- **Scheduling integration test.** Register a throwaway
  `\Backspace\test-*` task, assert Windows picked it up, delete it.
  Needs a Windows test environment with rights to create scheduled
  tasks.
- **Log retention enforcement.** `LogRetentionDays` is configurable but
  `LogStore` doesn't actually sweep old files today — add the sweep +
  a test.
- **Healthchecks.io live integration test.** Hits the real API with a
  per-run test key; gate behind `BACKSPACE_TEST_INTERACTIVE=1`.
- **Email delivery test** (Resend + Mailgun). Same pattern — live send
  to a test inbox, gate behind the interactive flag.
- **Metered-network watchdog.** `MeteredNetworkWatchdog` reads
  Windows' `NetworkInformation` API. Needs either a real metered
  connection or an API fake.
- **VSS shadow-copy path.** Requires admin + a locked file to prove the
  snapshot is used. Skip until a user reports an issue in practice.
- **Dark-mode screenshot coverage.** Only 3 views + 1 dialog re-captured
  in dark theme today; the rest depend on the light-mode baseline plus
  a contrast-check pass. Worth widening.

## Features deferred from the original roadmap

- **Hot-folder analytics & exclusion suggestions.** Parse per-backup
  logs to extract churn stats per path, SQLite under
  `Backspace.config/analytics.db`, surface a "top hot paths not yet
  excluded" suggestion list with persistent ignore.
- **Passphrase-mode secrets** (cross-machine portability). Optional
  master passphrase on first launch → Argon2id-derived AES-GCM key for
  `secrets.bin`. DPAPI stays default; this is opt-in for users who copy
  the config folder between PCs.
- **Copy a backup between destinations.** Needs a redesign for restic
  — `restic copy` exists but its model differs from Duplicacy's `copy`.
  Useful for "move from Dropbox to Google Drive without re-uploading
  from source."
- **Additional restic-native destinations** not yet wired: Azure Blob
  (`azure:`), B2 native (`b2:`), Storj (`storj:`), WebDAV
  (`rclone:webdav:`), SFTP (`sftp:`). All supported by restic out of
  the box; each is a destination-kind enum + UI form +
  `ResticUriResolver` case.
- **i18n.** English only today; the strings are centralised enough in
  code (TextBlock literal strings + a few enum-bound humanisers) that a
  `resx`-based pass would be tractable.

## Known restore limitations (carry-over from restic + Backrest)

### Bracket-name per-file restore may over-fetch same-length siblings on Windows

Root cause is upstream and documented in
[Backrest's `repo.go`](https://github.com/garethgeorge/backrest/blob/main/internal/orchestrator/repo/repo.go#L457):
`escapeGlob` returns the base verbatim on Windows because Go's
`filepath.Match` on Windows treats `\` as a path separator (not an
escape character), and rejects POSIX class-self-escape `[[]…[]]` as
`invalid pattern(s)`. Restic itself has no literal-include mode today.

**Workaround in `ResticBackrestEngine.RestoreFilesAsync`** (already
shipped): substitute each `[` and `]` with `?` (Go's single-char
wildcard) when any requested file has glob metachars in its leaf name;
NTFS forbids `?` in real filenames, so the wildcard cannot collide with
a sibling named exactly like the requested file, only with same-length
siblings — typically zero, occasionally one or two. After the restic
call returns, the engine sweeps anything outside the requested set so
the user sees only the files they asked for. Metadata round-trip stays
perfect (we use `restic restore`, never `dump`).

**Frequency**: rare. Brackets in filenames are uncommon; this only
fires for partial restore on Windows when the user picks at least one
file with `[` or `]` in its name. Folder picks with bracketed folder
names work natively via the `snapshotID:subfolder` syntax. The
over-fetch is small (only same-length siblings in the same directory)
and never visible to the user — the post-restore sweep deletes the
over-fetched files before the restore reports success.

**Upstream status**: prior attempts at a literal-include mode in restic
were closed (#2276, #2448, #3005, #3658 — the last explicitly as
`not planned`; #4867 remains open as the original feature request;
#5565 closed as duplicate of #4867).
- [`garethgeorge/backrest#1254`](https://github.com/garethgeorge/backrest/pull/1254)
  added the same `?`-substitution to Backrest's `escapeGlob` —
  **merged 2026-06-30** (not yet in a Backrest release; latest is
  v1.13.0). It fixes Backrest's `/v1.Backrest/Restore` RPC, which
  Backspace does not use (we build a raw `restic restore` invocation and
  run it via `/v1.Backrest/RunCommand`), so it does **not** gate our
  workaround — it only helps other Backrest users.
- [`restic/restic#21872`](https://github.com/restic/restic/pull/21872)
  adds `--include-from-raw` (literal-path matching, mirrors
  `backup --files-from-raw` from PR #3017). Closes #4867 and #5565.
  **Still open** — this is the PR that actually lets us retire the
  `?`-substitution + sweep.

The in-process workaround stays load-bearing until restic #21872 ships
in a restic release we bundle. The writeup for the (now merged) Backrest
side lives at
[`docs/backrest-upstream-bracket-bug.md`](backrest-upstream-bracket-bug.md).

**Linux / macOS**: not affected. Backrest's `escapeGlob`
backslash-escapes `[` and `]` correctly there, and restic honors the
escape via `filepath.Match`'s documented `'\\' c` rule on non-Windows.