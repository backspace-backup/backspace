# TESTING.md — test suite & scenario harness

How to run the tests, what each tier covers, and the standing
instructions for any agent driving the visual scenario suite.

## Audit qualification and remediation (2026-10-02)

The matrix below is an inventory of tests, not proof that every complete user flow has been exercised. Scenario pages report **STEPS COMPLETED** and explicitly exclude the runner's later teardown; the test-runner result (TRX) is authoritative for pass/fail. Scenario-owned cleanup is included in its recorded outcome. Always inspect both results and visual artifacts.

Real backup/restore scenarios now compare SHA-256 for every regular restored file, including large fixtures. Mutable sources, repositories and restore targets live in unique temporary directories outside the synced checkout. Screenshots and rendered scenario reports remain under the repository's artifacts directory. This avoids attributing a sync client's attribute changes to restore code.

The original audit reproduced data-safety, ownership and reporting defects. The active ROADMAP records the repair evidence and remaining limits. Fake-provider HTTP tests cover deterministic OAuth and notification contracts; they do not prove real account registration, email delivery, real VSS elevation, online-only file hydration or a reboot-triggered schedule.

S33 now exercises persisted ownership records with injected process inspection/termination actions. It never sweeps the developer's running processes. Real-engine tests use disposable data and exclusively owned daemons; live cloud tests require explicit credentials and their opt-in gate.

The test host supplies a quiet network-cost probe so a hosted runner's metered or background-restricted connection does not cancel unrelated scenarios. The runtime-guard tests override that per instance and explicitly trigger cancellation. Neither proves a physical Windows network transition. Clock-format tests cover both 12-hour and 24-hour cultures. Engine files are selected for copying after the download target has completed, including on a clean checkout's first build.

Release checks also build the missing-executable fallback and set `BACKSPACE_REQUIRE_STUB=1`. The corresponding test opens the embedded executable and checks its size and Windows executable header. This catches a first-build resource glob that would otherwise miss the newly generated file. Developer runs that explicitly skip the stub do not establish this packaging check.

### Production scale

Set `BACKSPACE_E2E_SCALE=huge` to lift the shared E2E source builders and manual E11/E30/E31/E34/E35 fixtures to production-sized input. The standard huge fixture has at least 20,000 files and approximately 2 GiB; exact edge fixtures retain their special files in addition to the scale payload. Multi-source or multi-backup tests can create multiple such payloads, and backup/restore copies need additional free space. The default remains small. This switch increases local I/O and, for explicitly enabled cloud scenarios, upload/download traffic.

`FixtureFactory.Generate` resolves the override centrally; `ScenarioContext.MakeSource` honors it too. E34 keeps its include-file regression at 1,500 files normally and exercises 20,000 files/2 GiB under the override. Exact generation asserts file count and byte budget, rather than relying on a random tree reaching its target.

## Commands

```bash
# Full unit + visual suite (no live cloud)
dotnet test tests/Backspace.Tests \
  --filter "FullyQualifiedName!~E2E"

# Local restic round-trip tests (boots the Backrest sidecar + runs real
# restic against a temp local repo)
dotnet test tests/Backspace.Tests --filter "FullyQualifiedName~E2E&FullyQualifiedName!~E07_DropboxRoundTrip&FullyQualifiedName!~E08_OneDriveRoundTrip&FullyQualifiedName!~E09_GoogleDriveRoundTrip&FullyQualifiedName!~E10_TwoConcurrentMultiCloud"

# Cloud tests (interactive — requires live OAuth tokens via secrets file
# or env var, gated by BACKSPACE_TEST_INTERACTIVE=1)
dotnet test tests/Backspace.Tests --filter "FullyQualifiedName~E07_DropboxRoundTrip|FullyQualifiedName~E08_OneDriveRoundTrip|FullyQualifiedName~E09_GoogleDriveRoundTrip|FullyQualifiedName~E10_TwoConcurrentMultiCloud"
```

The visual / scenario harness writes PNGs under
`tests/Backspace.Tests/artifacts/` and a `SUMMARY.md` after each scenario
run — see [tests/Backspace.Tests/Scenarios/README.md](../tests/Backspace.Tests/Scenarios/README.md)
for the deep-dive on the harness + how to add a scenario.

To regenerate the 13 screenshots used on the website:

```powershell
pwsh scripts/regenerate-website-screenshots.ps1
```

## Visual + interaction E2E test philosophy

The standing rule: don't QA by hand every time something changes. Build
the test pipeline so an agent (or CI) can:

1. **Boot the real app** headlessly — same `App.axaml.cs`, same DI
   graph, same resources — pointing at a throwaway
   `BACKSPACE_CONFIG_ROOT`.
2. **Generate dummy source data** under
   `%TEMP%/backspace-scenarios/<name>-<unique-id>/sources/...`
   via `SyntheticFileTree.Generate(...)`. Keep totals small (≤ a few MB
   per scenario) so the suite stays fast.
3. **Drive each scenario** through scripted UI interactions — show
   MainWindow, click buttons, fill text, navigate the wizard, run the
   backup, restore, verify. Each step renders a PNG under
   `artifacts/scenarios/<name>/screenshots/<NN>-<step>.png`.
4. **Report missing credentials explicitly.** `SecretsLoader` reads the
   ignored `tests/Backspace.Tests/secrets.env` file. The scenario report
   records a skip and its reason; under xUnit v2 that scenario may still
   appear as a passing runner result. Inspect both. Cloud E2E scenarios
   can acquire missing tokens interactively unless
   `BACKSPACE_E2E_NONINTERACTIVE=1` is set.
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

## Coverage matrix — every user-driven action vs. its scenario

Built as a deliverable for ROADMAP item 6b (2026-06-12). The goal: no
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
| Switch Easy ↔ Advanced mid-wizard | `S56_EditorModeAndApply` clicks the real coordinator's mode buttons, edits draft text, applies rules, verifies custom ordering, preserves both storages/overrides, tests Cancel isolation and actual Save plus fresh reload | ✅ |

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
| Savings message wraps beside Apply button (no crop) | `S50_RecommendedRowTextWraps` | ✅ |
| Grace controls available before and during skips | `S28_GraceBlockVisibility` | ✅ |
| Reset grace period (Easy + Advanced) | `S28_GraceBlockVisibility` (Easy Save/reopen and Advanced reset) | ✅ |
| Delete backup | `S41_DestinationAndBackupCrud` | ✅ |

### Backup execution

| Action | Scenario(s) | Status |
|---|---|---|
| Run backup manually | `E01_LocalRoundTrip` (Smoke/Many/Large), `S22_BackupCardRunningState` | ✅ |
| Run backup at scale (≥ 1500 files) | `E34_RestoreLongIncludeList`, `BACKSPACE_E2E_SCALE=huge` lift | ✅ |
| Stop mid-run | `E02_StopAndResume` | ✅ |
| Resume after stop | `E02` | ✅ |
| Two concurrent backups | `E03_TwoConcurrentBackups` | ✅ |
| Two concurrent multi-cloud backups | `E10_TwoConcurrentMultiCloud`; requires explicit real-account opt-in | ⛔ |
| Queue a 2nd run while one's running | `E21_QueueWhileRunning` | ✅ |
| Skip-on-battery → run skips | `RuntimeGuardCoverageTests` inspects the actual unregistered Windows task setting both on/off; `S36` covers editor persistence. OS battery transition unexercised | ⛔ OS acceptance |
| Stop-on-battery → run stops | Actual unregistered Windows task setting both on/off; no battery transition during a running task | ⛔ OS acceptance |
| Require-network → run skips when offline | Actual unregistered Windows task network setting both on/off; no machine connectivity change or registered-task execution | ⛔ OS acceptance |
| Metered-network watchdog stops mid-run | `RuntimeGuardCoverageTests`: actual backup/prune/check orchestration, injected metered signal after first cell starts, cancellation/persistence and opt-out negatives. Real WinRT network-state detection unexercised | ✅ injected signal |
| Catch-up missed runs after sleep | Actual unregistered Windows task catch-up setting both on/off; no sleep/resume or reboot | ⛔ OS acceptance |
| Headless `--run <name>` | [Actual published workflows](../audit-dashboard/evidence/packaged-workflows-final/report.html): existing option-shaped name `-h`, real snapshot and both files verified by SHA-256; missing backup exits 2 | ✅ isolated local repository |
| Headless `--run-all` | [Actual published workflows](../audit-dashboard/evidence/packaged-workflows-final/report.html): both enabled/unpaused backups run; paused and disabled repositories remain untouched | ✅ isolated local repositories |
| Schedule fires at the configured time (Task Scheduler) | `RuntimeGuardCoverageTests` inspects real unregistered daily/weekly/monthly definitions and exact per-kind CLI actions; does not register tasks or advance OS time | ⛔ OS acceptance |

### Restore

| Action | Scenario(s) | Status |
|---|---|---|
| Restore everything | `E01` (round-trip), `S04_RestoreWizardShots` | ✅ |
| Restore from older revision | `E22_RestoreFromOlderRevision`, `S14_RestoreSingleSourceRevisionPicker` | ✅ |
| Restore cherry-picked files | `E17_RestoreCherryPickSubset`, `S15_RestoreFilesStep` | ✅ |
| Restore over original source (overwrite) | `E18_RestoreToOriginalSource` | ✅ |
| Multi-source restore (pick one source) | `S20_RestorePickSourceMulti` | ✅ |
| Restore wizard target step | `S23_RestoreTargetStep` renders both states; `InteractionTests.RestorePickTarget_RadioToggle_DrivesTargetOriginalSource` clicks both radios | ✅ |
| Restore wizard summary | `S16_RestoreSummary` | ✅ |
| Restore folder → minimal cover (1 pattern not N files) | `S47_RestoreFolderMinimalCover` projection plus `E35_FolderCollapseEndToEnd` collapsed selection through the real restore engine and byte comparison | ✅ |
| Restore long include list (CreateProcess ceiling) | `E34_RestoreLongIncludeList` | ✅ |
| Restore over slow network (retries) | — | ⛔ deferred: needs network-shaping infra |
| Restore with bandwidth limit applied | — | ⛔ deferred: needs network-shaping infra |
| Cancel a running restore | `S37_RestoreCancel` uses in-memory work; `RestoreSafetyRegressionTests` fake daemon asserts cancellation acknowledgement and owned-file lifetime. A production daemon cancellation remains separate integration proof. | 🟡 |
| Live-tail restore progress | `InteractionLiveTailTests` (interaction shape) | 🟡 |

### Mount/Browse snapshots

| Action | Scenario(s) | Status |
|---|---|---|
| Mount snapshot as readable Windows volumes (WinFSP) | `E04_SnapshotMountWindows` mounts two real volumes, enumerates and compares every file's bytes, unmounts one without breaking the other. Requires WinFSP. | ✅ |
| Mount from the packaged app's card and open Explorer | E04 exercises developer-build coordinators; S49 exercises the card with a fake mount registry. Neither launches the distributed executable. | 🟡 |
| Open Backrest's snapshot UI in the default browser | `S57_SnapshotBrowserLaunch` pointer-clicks the actual Browse button, cold-starts an isolated real daemon, checks its HTTP UI and exact plan URL at an injected opener; opener failure produces the actual notification. Shell launch and web-page interaction remain separate acceptance. | ✅ bounded launch path |

### Maintenance

| Action | Scenario(s) | Status |
|---|---|---|
| Prune after backup (inline) | `E14_PruneAfterBackupRealEngine` | ✅ |
| Standalone prune (cadence) | `RuntimeGuardCoverageTests` verifies actual unregistered weekly trigger/action and real orchestration cancellation/opt-out; `S39` covers fake-engine invocation. OS due-time firing unexercised | ⛔ OS acceptance |
| Check after backup (inline) | `E15_CheckAfterBackupRealEngine` | ✅ |
| Standalone check (cadence) | Actual unregistered monthly trigger/action and real orchestration cancellation/opt-out; `S39` covers fake-engine invocation. OS due-time firing unexercised | ⛔ OS acceptance |
| Test-restore drill | `E16_TestRestoreVerifyDrill` | ✅ |
| Drill skips mid-flight-edited files | E16 covers an ordinary real drill, not a concurrent source edit. `BackupVerifierMidFlightDirtyTests` covers record state and retry replacement only. | 🟡 |
| Maintenance policy cards rendered | `S26_MaintenancePolicyCards` | ✅ |

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
| Apply recommended block | `RecommendedFiltersTests` plus `S56_EditorModeAndApply`: actual Apply pointer clicks preserve custom rules above generated rules and are idempotent across Easy/Advanced mode switches | ✅ |
| Custom-block markers placed above generated | `S30_CustomExclusionMarkerPlacement` asserts merged strings, legacy-rule preservation and idempotence; no rendered editor in S30 | ✅ |
| Filter simulation pane (Simulate button) | `S40_FilterSimulationPane` | ✅ |

### Settings

| Action | Scenario(s) | Status |
|---|---|---|
| Switch accent / theme | `S05_LightDarkThemeShots`, `S12_SettingsAndAccents` | ✅ |
| Configure Healthchecks.io | `CloudNotificationRepairTests` fake HTTP exercises custom PingUrl, terminal pings and retries; live service remains | 🟡 |
| Configure Resend / Mailgun email | `CloudNotificationRepairTests` fake HTTP exercises API requests, retry and visible nonfatal failure; live delivery remains | 🟡 |
| Toggle notification gating per event | `S45_SettingsTogglePersistence` | ✅ |
| Restore-retry cascade settings | `S45` | ✅ |
| Section-header hover wash (no Fluent band leak) | `S55_SettingsHoverAnimation` sends real headless pointer movement with transitions enabled in light/dark, samples intermediate alpha/RGB frames, rejects a white hue, checks left extension/corners and return to transparent. Native display-scale rendering remains acceptance. | ✅ sampled animation |

### Monitoring & notifications

| Action | Scenario(s) | Status |
|---|---|---|
| Toast on Success / Skipped / Failure | `S50_ExactFailureNotification` renders the production failure and skipped-warning popups and clicks Open run details. Run history is prepared; native Windows placement and focus remain untested. `S48_NotificationsWiring` only checks no-throw dispatch. | 🟡 |
| Healthchecks.io ping on each run terminal | `CloudNotificationRepairTests` fake HTTP proves independence from daily human-alert cap and email delay; live endpoint remains | 🟡 |
| Email on Failure | `CloudNotificationRepairTests` fake HTTP proves bounded retries and visible errors; actual inbox delivery remains | 🟡 |
| Sound alert on Failure | `S48` returns without a desktop sound assertion; audible output remains | 🟡 |
| Failure alert opens the exact failed run | `S50_ExactFailureNotification` pointer-clicks the real popup button and checks the earlier failed row even with a newer success; a missing run ID must not select an unrelated run | ✅ headless interaction |
| Close-to-tray preference takes effect immediately | `S60_ReviewFeedback` starts with the tray hidden, clicks the actual checkbox, immediately closes, and reopens the same window | ✅ headless interaction |
| Force quit remains available while ordinary Quit waits | `S60_ReviewFeedback` keeps an operation lease open, starts ordinary Quit, opens Force quit, then cancels it. Native menu interaction and actual forced process exit remain untested. | 🟡 |
| Restore list opens one result without a duplicate card | `S60_ReviewFeedback` clicks View results and All restores on the real views using prepared session history | ✅ headless interaction |
| External scheduled results appear when returning to the app | `S53_ExternalScheduledRefresh` uses separate config/history writers and real navigation/activation; no periodic watcher | ✅ |
| Health banner state matrix | `S13_HealthBannerStateMatrix` | ✅ |

### Edge cases (the boundary set)

| Action | Scenario(s) | Status |
|---|---|---|
| Unicode names (emoji, RTL, CJK, NFD) | `E11_EdgeCaseFixture` + peculiarity set in every E01 size | ✅ |
| Brackets in filenames (`[draft]`) | E01 matrix plus `RestoreSafetyRegressionTests`: private staging, literal requested-output promotion, unrelated target-file preservation; no target-wide deletion sweep | ✅ |
| Zero-byte / leading/trailing space / dotfiles | peculiarity set | ✅ |
| Compound extensions, long names | peculiarity set, `S18_LongNamesOverflow` | ✅ |
| Future mtimes | peculiarity set | ✅ |
| NTFS-specific shapes (ADS, hidden, system, junction) | E11 compares regular-file contents and reports the unsupported junction. Regular-file hashing does not prove ADS/ACL/attribute fidelity. | 🟡 |
| Locked file during backup (FileShare.None) | Peculiarity generator supports an opt-in sentinel; its presence alone does not prove an elevated locked-file backup | 🟡 |
| VSS shadow-copy path | Policy/routing/helper tests cover contracts; actual Windows consent, elevated process and locked-file content remain | ⛔ |

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

Guard, scheduler-definition and parser proof: [44 passing cases plus 13 scheduled-action regressions](../audit-dashboard/evidence/infrastructure-tests/runtime-coverage-report.html). Battery/network availability and maintenance due-time execution are Windows Task Scheduler policies; manual orchestration does not have a separate battery or due/not-due branch. The report distinguishes injected signals, unregistered OS definitions, and actual packaged processes from real machine-state acceptance.

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

The general window screenshot generators advance real render frames for a short
settling interval before capture, so initial caret rotations and theme brushes
have finished. Transitions stay enabled. `S55_SettingsHoverAnimation` separately
captures and checks intermediate hover frames rather than using this settling
step. `S54_EditorialMinimumWidth` checks both themes at 1180 × 720, including the
retained Restores navigation row, all action-label bounds and viewport edges,
and a deliberately narrow card that must wrap its actions onto multiple rows.

### Laptop working areas

MainWindow starts at 1180 × 760 logical pixels when space permits, with a 920 × 540 minimum. Main and resizable editor windows fit to the current monitor's working area after Windows display scaling, taskbar space, and native frame are accounted for. If a monitor is smaller than a requested minimum, the window's minimum is lowered to keep its controls on screen. This is a layout policy, not a claim about laptop market share.

S58_LaptopWorkingAreas renders main pages at 920 × 540, 1076 × 558 (1366 × 768 at 125% with a taskbar), and 1264 × 672 (1920 × 1080 at 150% with a taskbar). It captures the initial backup overview and the scrolled actions, checks matching page gutters and non-overlapping controls, and verifies four resizable dialogs with a 519-pixel client height. WindowPlacementTests separately checks scaling, native frame allowance, off-screen positions, negative-origin monitors and a screen smaller than the normal minimum. Its window-event tests preserve configured startup dimensions, fit moves between simulated monitors at the same scaling, leave ordinary dragging alone, and refit after restoring a maximized window. Screenshots show logical working areas; physical multi-monitor transitions remain an OS acceptance check.

S59_DropdownAffordance checks the painted dropdown arrow in both themes, opens the actual popup with a pointer click, selects choices, checks persisted settings, and exercises keyboard selection, Escape and disabled controls. S11_LogsViewPopulated checks live and saved output separately, including the first-ever run of a selected backup and the return to the empty state when an unfinished activity is removed. Screenshot comparisons serialize their file access so a directory scan cannot write the same diff image as a fresh capture.
