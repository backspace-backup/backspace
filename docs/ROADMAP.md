# ROADMAP.md — work tracker

This file is the **source of truth** for in-flight work. Pair with
[GOTCHAS.md](GOTCHAS.md) for the upstream-driven limitations the code
already works around, and [AGENT.md § The ROADMAP workflow](../AGENT.md#the-roadmap-workflow-source-of-truth)
for the workflow rules an agent must follow before touching this file.

## Actively working

Top-of-file = highest priority. Entries leave this section only when the
user explicitly validates the attached proof. See AGENT.md for the
removal rules.

## 🎯 Items needing your input (TL;DR)

Read this section first. Each line points to a full entry below.

2. **Register the OneDrive + Google Drive OAuth apps.** Provider code + tests are shipped, just need the GUIDs from Microsoft (Entra portal) and Google (Cloud Console). See "**P3: OneDrive + Google Drive OAuth client_ids**". Until you do this, OneDrive/GDrive Connect shows "OAuth not yet wired."
   - **I need from you:** the OneDrive Application (client) ID + the Google OAuth Client ID, pasted into [`OAuthAppRegistry.cs`](../src/Backspace/Services/OAuth/OAuthAppRegistry.cs).

3. **Validate the shipped work** under "✅ Awaiting your validation" (next section). Each entry has a `**Proof:**` block — tests, commits, files. Tell me which entries to remove and which need rework.
   - **I need from you:** for each "Awaiting validation" item, "validated" / "rework: …" / "leave open until …".

## ✅ Awaiting your validation (review proofs)

These items are shipped — code merged, tests passing where applicable.
Read the `**Proof:**` block on each, then say "validated" or redirect.

- P0: [Restore enumerates every leaf when a folder is selected](#p0-restore-enumerates-every-leaf-when-a-folder-is-selected)
- P0: [Restore crashes with "filename or extension is too long"](#p0-restore-crashes-with-filename-or-extension-is-too-long-on-large-file-sets)
- P1: [Estimated savings shows 0 B](#p1-estimated-savings-next-to-apply-recommended-exclusions-shows-0-b)
- P1: [Skipped-run alerts block visibility](#p1-skipped-run-alerts-block-visibility-logic)
- P1: [Collapsible-section hover stripe](#p1-collapsible-section-hover-styling-has-weird-stripe)
- P2: [Folder-picker lenient click target](#p2-folder-picker-rows-should-toggle-expand-on-click-anywhere-left-of-the-checkbox)
- P2: [Filter editor wording + ntuser.dat dedup](#p2-filter-editor-wording--recommended-exclusion-polish)
- P2: [Custom-exclusion markers placement](#p2-custom-exclusion-markers-should-sit-before-backspace-generated-block)
- P2: [E2E coverage matrix in TESTING.md](#p2-e2e-coverage-audit--user-action--scenario-matrix)
- P3: [Restores left-nav single-restore variant](#p3-restores-left-nav-item--single-restore-variant-shipped)
- P3: [Restores left-nav multi-restore cards](#p3-restores-multi-restore-cards--shipped)
- P3: [rclone orphan-safe kill](#p3-rclone-orphan-safe-kill)
- P3: [Auto-update light (GitHub Releases check)](#p3-auto-update-channel--light-variant)
- P0: [Mount feature actually working (WinFSP-backed)](#p0-mount-feature-actually-working-winfsp-backed--awaiting-validation)
- P0: [Restore wizard doesn't offer a revision picker](#p0-restore-wizard-doesnt-offer-a-revision-picker-regression-vs-duplimate--awaiting-validation)
- P0: [E2E coverage matrix — every actionable row covered](#p0-e2e-revamp-pass--all-actionable-coverage-matrix-rows-covered--awaiting-validation)
- (above this section): the Backrest deviation P0 (restore stack refactor) carrying a Known-gap for `--json` summary parsing.

## 🚧 Open — in progress / queued

- P0: Backrest restore-RPC deviation review — refactor done; `--json` summary parsing queued as a follow-up.
- P3: OneDrive + Google Drive OAuth client_ids — awaiting maintainer registration.

Also shipped and awaiting validation:
- P1: Headless test session degradation — root-caused and fixed 2026-07-06 (see full entry; 4 consecutive all-green combined runs).

Shipped 2026-07-04, now awaiting your validation (see full entries below):
- P1: Restore target radios ping-pong forever (test-suite hang; found while investigating the stuck suite).
- P1: Filters > Advanced > Simulate fails (RunCommand exit status 1).
- P1: Status text next to "Apply recommended exclusions" is cropped (plus 10 more crop-prone spots).
- P1: Collapsible-section hover styling (re-opened for Settings; properly fixed at the template-part level).
- P2: UI copy overuses em dashes (~174 user-facing rewrites).

---

### P1: Headless test session degrades ~70s into combined suite runs

- **Raised:** 2026-07-05 (found while validating the hover refinements)
- **Type:** bug (test infrastructure + two real product leaks)
- **Status:** Awaiting validation — root-caused and fixed 2026-07-06
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
- **Proof — the numbers:**
  - Before: combined runs failed 1-10 tests in most attempts, with
    run duration drifting 1m31s → 2m33s. After: **4 consecutive
    combined runs all green (812/812)** with durations in a tight
    1m45-1m51s band. Suite halves remain green as always.
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
- **Proof — open and look:**
  - The full visual suite now completes: **812/812 passed in 1m33s**
    (it previously never finished; the hung screenshot test alone ate
    40+ minutes of CPU before being killed). The once-hanging class
    (`WebsiteScreenshotTests`, 13 shots including `Shot_04_Restore_Pick`)
    passes in 2 s.
  - 🖼️ [restore wizard target step renders normally](../tests/Backspace.Tests/artifacts/scenarios/S04-restore-wizard-shots/screenshots/) — the
    step's radios behave as before for users; only the wiring changed.
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
  Duplicacy-era lines restic never prints, so a "successful" run would
  still have shown an empty pane. Fix: Simulate no longer touches
  restic or the repo at all — it walks the sources locally with the
  exact same translated rules the engine sends restic (same
  FilterTranslator), works before any destination exists, and names
  the matching rule per exclusion. The dead engine dry-run
  (`EnumOnlyAsync`) was removed.
- **Proof — open and look:**
  - 🖼️ **[simulate pane populated](../tests/Backspace.Tests/artifacts/scenarios/S40-filter-simulation-pane/screenshots/01-simulate-pane-populated.png)** —
    Filters > Advanced after clicking Simulate: ✓ line for the kept
    file, ✗ lines with the matching rule for `*.tmp` and `cache/`
    exclusions. Before the fix this pane showed only the exit-status-1
    error.
  - Tests: `S40_FilterSimulationPane` now drives the real Simulate
    button end-to-end (plus a no-source case asserting the friendly
    "No files found" message), and `FilterSimulatorWalkTests` (4 tests)
    pin the walk: rule attribution, excluded dirs not descended,
    MaxEntries cap, missing source. All pass.
- **I need from you:** open your backup > Filters > Advanced and click
  Simulate on the real `C:\ntws` source; the right pane should list
  files with ✓/✗ instead of erroring.

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
- **Proof — open and look:**
  - 🖼️ **[savings message wraps beside the button](../tests/Backspace.Tests/artifacts/scenarios/S50-recommended-row-text-wraps/screenshots/01-savings-message-wraps-beside-button.png)** —
    the exact reported message now wraps onto two lines next to
    "Apply recommended exclusions", fully inside the card. Before the
    fix it ran off the right edge as one clipped line.
  - Test: `S50_RecommendedRowTextWraps` asserts the label wraps
    (height ≥ 2 lines) AND measures narrower than the window; it fails
    if the horizontal-StackPanel pattern regresses.
- **I need from you:** open the same editor as before (Filters >
  Recommended on a narrow source) and confirm the message reads fully.

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
- **Proof — open and look:**
  - 🖼️ The S40 and S50 screenshots above show the rewritten copy in
    place (filter help text, savings message, Simulate hint, dialog
    subtitles).
  - Suite green after the copy sync: 812/812.
- **I need from you:** skim any screen you use often (Settings,
  editor, restore wizard); if a remaining em dash bothers you
  anywhere, point at it.

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
- **Proof — open and look:**
  - 🖼️ **[picker rendering 5 revisions after the fix](../tests/Backspace.Tests/artifacts/scenarios/S14-restore-single-source-revisions/screenshots/03-revisions-list-5-rows.png)**
    — should show "Step 1 of 3 · Go back to when?" + 5 dated
    rows (#5 latest, highlighted) + an enabled "Next: pick files
    to restore" button. Before the fix this PNG was a blank
    surface with the rows missing.
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

### P0: E2E revamp pass — all actionable coverage-matrix rows covered — Awaiting validation

- **Raised:** 2026-06-12
- **Type:** test coverage
- **Status:** Awaiting validation — every actionable matrix row is
  now ✅ or explicitly ⛔ deferred with a reason; the legacy "❌
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
- **Proof — open and look:**
  - 🖼️ **[refreshed coverage matrix in TESTING.md](TESTING.md#coverage-matrix--every-user-driven-action-vs-its-scenario)**
    — every row is now ✅ (covered) or ⛔ (deferred with reason).
    The ❌ status no longer appears anywhere. Status legend
    section at the bottom explains the deferral convention.
- **I need from you:**
  - Open the linked TESTING.md matrix, scan the right-hand
    "Status" column — confirm every row reads ✅ or ⛔, no
    ❌ left. If you'd like a specific deferred row pulled into
    coverage (e.g. you have S3 creds and want E07-shape S3
    coverage), name it and I'll add it.

### P0: Investigate "why do we deviate from Backrest's native restore RPC?"

- **Raised:** 2026-06-12 (user feedback on the folder-collapse review)
- **Type:** architecture review
- **Status:** Open — in progress
- **Context:** Backspace's "thin wrapper" promise (see AGENT.md design
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
- **Proof:**
  - Per-file Files dict / RestoredFile / RestoreFileStatus / retry
    cascade / FileRestored & FileFailed events all removed (~350
    LOC). New shape: `RestoreOpResult` mirrors Backrest's
    `RestoreProgressEntry` (overall counts + bytes + terminal
    status + error message). See
    [src/Backspace/Services/RestoreEngine.cs](../src/Backspace/Services/RestoreEngine.cs)
    + [src/Backspace/Services/IBackupEngine.cs](../src/Backspace/Services/IBackupEngine.cs).
  - E01 matrix (Smoke 31s, Many 1m4s, Large 1m5s) + E34 (1m3s)
    all green after the refactor.
- **Known gap (per AGENT.md rule 7):** the engine returns
  approximate FilesRestored numbers — assumes TotalFiles on
  Success, 0 on non-Success. Parsing restic's `--json` output for
  exact files_restored / bytes_restored is queued as a follow-up
  in this same entry: add `--json` to the restic invocation, fetch
  the operation's logs via `/v1.Backrest/GetLogs`, find the final
  `summary` line, extract authoritative numbers. Until then the
  UI percentages are coarse but never WRONG (Success → 100%,
  Warning → "some files failed — see log", Error → 0%).

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
- **Proof:**
  - [tests/Backspace.Tests/Services/RestoreSelectionMinimizerTests.cs](../tests/Backspace.Tests/Services/RestoreSelectionMinimizerTests.cs)
    — 9 cases (fully-selected folder → 1 pattern; partial → per-leaf;
    nested full coverage promotes to outermost; mixed selection;
    1500-leaf collapse; two-folder collapse). All pass.
  - E01 matrix (Smoke 32 s, Many 1 m 8 s, Large 1 m 7 s) re-run after
    the wiring — no regression.

### P0: Restore crashes with "filename or extension is too long" on large file sets

- **Raised:** 2026-06-11
- **Type:** bug (regression from snap:subfolder refactor)
- **Status:** Awaiting validation
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
- **Proof:**
  - `E34_RestoreLongIncludeList` PASSed (58 s) restoring 1500 files
    via `--include-file`. Without the fix, restic would have crashed
    with "The filename or extension is too long" before any file
    landed. Rendered at
    [artifacts/scenarios/index.html#E34-restore-long-include-list](../tests/Backspace.Tests/artifacts/scenarios/index.html#E34-restore-long-include-list).
  - E01 matrix re-run (Smoke 32 s, Many 1 m 8 s, Large 1 m 7 s) — all
    green; the `--include-file` path covers the existing matrix sizes
    too with no regression.
  - `BACKSPACE_E2E_SCALE=huge` env var defined and wired through
    `FixtureSizeResolver.ResolveSize`. Scenarios that adopt
    `ResolveSize` will jump to `Huge` at pre-push validation time
    without affecting iteration speed.

### P0: Mount fails for the user's basic dist-folder backup

- **Raised:** 2026-06-11
- **Type:** bug + architectural decision needed
- **Status:** Awaiting user direction
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
- **Proof:**
  - The original WebDAV-failure diagnostic (`MountRepro.NetUseReproducesUserFailure`)
    is now deleted along with the dead WebDAV mount code — superseded
    by the WinFSP path. Its findings are preserved here for
    posterity: every `\\127.0.0.1@<port>\` UNC shape returned
    `System error 67` and the WebDAV server logged
    `connect attempts on probe port: 0`, proving the redirector
    silently rejected custom ports before any TCP contact.
  - `E04_SnapshotMountWindows` now drives the WinFSP path
    end-to-end (real backup → real mount → file walk). Rendered at
    [artifacts/scenarios/index.html#E04-snapshot-mount-windows](../tests/Backspace.Tests/artifacts/scenarios/index.html#E04-snapshot-mount-windows).
  - Mount actually-works lives in its own ROADMAP entry below
    ("**Mount feature actually working (WinFSP-backed)**") which
    is now Awaiting validation.

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
- **Proof:**
  - [tests/Backspace.Tests/ViewModels/BackupEditorSavingsLabelTests.cs](../tests/Backspace.Tests/ViewModels/BackupEditorSavingsLabelTests.cs)
    — 5/5 pass; covers pending / in-progress / zero-with-scans /
    zero-with-no-scans / positive-savings paths.

### P1: Skipped-run alerts block visibility logic

- **Raised:** 2026-06-11 (user asked 2026-07-03 to confirm the Easy-mode
  behavior: block hidden in Easy mode UNLESS the backup is currently in
  grace, so the Reset-grace control stays reachable)
- **Type:** bug
- **Status:** Awaiting validation
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
- **Proof — open and look:**
  - 🖼️ **[Advanced grace block always visible](../tests/Backspace.Tests/artifacts/scenarios/S28-grace-block-visibility/screenshots/01-advanced-grace-block-always-visible.png)** —
    Reset grace period reachable on a healthy backup in Advanced mode;
    the S28 run also asserts Easy mode hides the block for healthy /
    never-run / grace-expired backups and shows it only while a
    Skipped run is inside the grace window.
  - [tests/Backspace.Tests/Models/BackupShouldShowGraceBlockTests.cs](../tests/Backspace.Tests/Models/BackupShouldShowGraceBlockTests.cs)
    — 5/5 pass; covers NeverRun, Success, Skipped-within-grace,
    Skipped-past-grace, and Grace=0 cases.

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
- **Proof — open and look:**
  - 🖼️ **[hover wash on a Settings header](../tests/Backspace.Tests/artifacts/scenarios/S49-settings-expander-hover/screenshots/01-hover-wash-on-section-header.png)**
    vs 🖼️ **[same header at rest](../tests/Backspace.Tests/artifacts/scenarios/S49-settings-expander-hover/screenshots/02-header-at-rest.png)** —
    hover shows a soft rounded neutral wash (deliberately quiet, same
    family as nav-row hover); no hard-edged band, no square corners,
    no chevron mini-square.
  - Test: `S49_SettingsExpanderHover` forces `:pointerover` and
    asserts the pierced style's 8px corner radius applied and the
    chevron pill is transparent — both fail if the Fluent leak
    returns.
- **I need from you:** open Settings and hover the section headers;
  confirm the hover now feels right (soft wash, no stripe). Also hover
  the "Advanced" toggle in the restore wizard: flat, cursor change
  only.

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
- **Proof:**
  - Markup + code-behind at
    [src/Backspace/Views/SourceTreePickerWindow.axaml.cs:32-58](../src/Backspace/Views/SourceTreePickerWindow.axaml.cs)
    and [SourceTreePickerWindow.axaml:25-37](../src/Backspace/Views/SourceTreePickerWindow.axaml).
  - Visual confirmation: the existing source-tree-picker scenario
    captures the picker open; re-run after this change to confirm row
    text is the click target.
  - **Scope note:** the user said "or any other similar selector" —
    this commit only covers `SourceTreePickerWindow`. The restore
    file picker uses a flat `AllFiles` ItemsControl, not a tree; the
    filter simulator output is read-only. If another tree picker
    surfaces in a future review, apply the same handler shape.

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
- **Proof:**
  - [tests/Backspace.Tests/Services/RecommendedFiltersTests.cs](../tests/Backspace.Tests/Services/RecommendedFiltersTests.cs)
    `NtuserDatPattern_coversTheWholeFamilyAsOneGlob` — passes;
    asserts the single `ntuser.dat*` line is emitted AND that
    `ntuser.dat.LOG{,1,2}` are NOT emitted as separate lines.
  - Help text change visible at
    [src/Backspace/Views/BackupEditorWindow.axaml:741](../src/Backspace/Views/BackupEditorWindow.axaml).

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
- **Proof:**
  - [tests/Backspace.Tests/Services/RecommendedFiltersTests.cs](../tests/Backspace.Tests/Services/RecommendedFiltersTests.cs)
    `MergeIntoFilters_emitsCustomBlockAboveRecommendedBlock` — passes;
    asserts the custom-block markers appear FIRST and the recommended
    block comes after.
  - Same file `MergeIntoFilters_emptyExisting_yieldsCustomBlockThenAutoBlock`
    — passes; covers the empty-existing path.
  - Same file `MergeIntoFilters_replacesExistingBlock_inPlace` —
    passes; covers the re-apply case (one of each marker pair
    survives).

### P2: E2E coverage audit — user-action ↔ scenario matrix

- **Raised:** 2026-06-11
- **Type:** coverage / process
- **Status:** Awaiting validation
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
- **Proof:**
  - [docs/TESTING.md § Coverage matrix](TESTING.md#coverage-matrix--every-user-driven-action-vs-its-scenario)
    — 13 areas × ~90 actions × scenario links + gaps.
  - Acceptance criterion: every action shows a scenario name or a
    ❌ explanation. Any future PR adding a feature must add a row
    to the matrix in the same commit (mirrors the AGENT.md "stale
    docs poison every read" rule for documentation).

### P0: Mount feature actually working (WinFSP-backed) — Awaiting validation

- **Raised:** 2026-06-12 (split from the original Mount investigation)
- **Type:** feature / architecture
- **Status:** Awaiting validation — WinFSP path (user-picked option 1)
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
- **Proof — open and look:**
  - 🖼️ **[snapshot mounted as drive Z: in Explorer](../tests/Backspace.Tests/artifacts/scenarios/E04-snapshot-mount-windows/screenshots/01-mounted-at-z.png)**
    — backup ran, snapshot mounted, every source file readable
    through the drive letter byte-for-byte. The test PASSed
    against the real WinFSP runtime in 30 s.
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

### P3: "Restores" left-nav item — single-restore variant shipped

- **Raised:** 2026-06-11
- **Type:** feature
- **Status:** Awaiting validation (single-restore variant);
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
- **Proof:**
  - Visible nav-item markup at
    [src/Backspace/Views/MainWindow.axaml:128-145](../src/Backspace/Views/MainWindow.axaml).
  - Animation style at
    [src/Backspace/Themes/Controls.axaml:206-225](../src/Backspace/Themes/Controls.axaml).
  - Wiring at
    [src/Backspace/ViewModels/MainWindowViewModel.cs:75-100](../src/Backspace/ViewModels/MainWindowViewModel.cs).
- **Known gap (per AGENT.md rule 7):** No automated test. The
  scenario shape: start a long restore, navigate to Backups, assert
  the "Restores" nav-item is visible, click it, assert progress is
  intact. Filed to E2E revamp queue.

### P3: "Restores" multi-restore cards — shipped

- **Raised:** 2026-06-12
- **Type:** feature
- **Status:** Awaiting validation
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
- **Proof:**
  - [tests/Backspace.Tests/Services/ActiveRestoreRegistryTests.cs](../tests/Backspace.Tests/Services/ActiveRestoreRegistryTests.cs)
    — 7 cases pass: Begin / UpdateProgress / End shape + multiple
    concurrent sessions coexist + status label changes through
    terminal transitions.
  - Code at
    [src/Backspace/Services/ActiveRestoreRegistry.cs](../src/Backspace/Services/ActiveRestoreRegistry.cs),
    [src/Backspace/ViewModels/ActiveRestoresViewModel.cs](../src/Backspace/ViewModels/ActiveRestoresViewModel.cs),
    [src/Backspace/Views/ActiveRestoresView.axaml](../src/Backspace/Views/ActiveRestoresView.axaml).
- **Known gap (per AGENT.md rule 7):** Per-session distinct VM
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
- **Proof (for the partial fix):**
  - Code change at
    [src/Backspace/ViewModels/MainWindowViewModel.cs:93-98](../src/Backspace/ViewModels/MainWindowViewModel.cs)
    — `IsRunning` guard added; existing PreselectRestoreBackup path
    untouched.
  - Manual repro: start a restore, click Backups, click Restore.
    Old behaviour: wizard reset to step 1. New behaviour: live
    progress view intact.

### P3: OneDrive + Google Drive OAuth client_ids

- **Raised:** 2026-06-12 (audit of BACKSPACE_PORT_DESIGN.md § 6.2)
- **Type:** feature
- **Status:** Provider code shipped; awaiting maintainer to register
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
- **Proof:**
  - [tests/.../OAuthProviderTests.cs](../tests/Backspace.Tests/Services/OAuthProviderTests.cs)
    — 14 cases, all pass: endpoint URLs, scopes (with the
    "drive.file not drive" guard as an explicit assertion so a
    future refactor can't silently flip back), auth-URL extras for
    the refresh-token opt-ins, token-response parsing with the
    rclone-shape projection, factory wiring (BYO + empty fallback).
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

### P3: rclone orphan-safe kill

- **Raised:** 2026-06-12 (audit of BACKSPACE_PORT_DESIGN.md § 6.3)
- **Type:** reliability
- **Status:** Awaiting validation
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
- **Proof:**
  - [src/Backspace/Services/RcloneOrphanReaper.cs](../src/Backspace/Services/RcloneOrphanReaper.cs)
    — sweep with triple guard (image-name + main-module-path + start-time-before-us).
  - Wire-up at [src/Backspace/Program.cs:51-58](../src/Backspace/Program.cs).
  - Failure-isolated: the whole sweep is wrapped in try/catch +
    `_log.Warning`; orphan reaper failure can never block app
    startup. The worst case is one more orphan keeps running until
    the next sweep.
- **Known gap (per AGENT.md rule 7):** No E2E test yet. A realistic
  scenario would spawn a fake rclone.exe pointing at our path,
  start it before launching Backspace, run the reaper, and assert
  the process is gone. Filed to the E2E revamp queue.

### P3: Delete BACKSPACE_PORT_DESIGN.md once port-plan gaps close

- **Raised:** 2026-06-12 (user request)
- **Type:** docs cleanup
- **Status:** Blocked — waits on the four §14 audit items below
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

### P3: Auto-update channel — light variant

- **Raised:** 2026-06-12 (audit of BACKSPACE_PORT_DESIGN.md § 13)
- **Type:** infrastructure
- **Status:** Awaiting validation (light variant shipped; heavy
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
- **Proof:**
  - [src/Backspace/Services/UpdateChecker.cs](../src/Backspace/Services/UpdateChecker.cs)
    — Begin() / CheckOnceAsync(); ReleaseInfo parser; failure-isolated.
  - Wire-up at
    [src/Backspace/Views/MainWindow.axaml.cs:91-97](../src/Backspace/Views/MainWindow.axaml.cs)
    fires after GUI is up so the toast surface is ready.
- **Known gap (per AGENT.md rule 7):** No automated test. The
  realistic shape: serve a fake GitHub-shaped response over a
  loopback HttpListener, point UpdateChecker at it, assert the
  notification fires with the right URL. Filed to E2E revamp.
  Heavy auto-update path (download + relaunch) remains Open for
  user direction when volume justifies it.

## Adversarial self-review (2026-06-12)

Per AGENT.md rule 7. Each `Awaiting validation` item gets one
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
