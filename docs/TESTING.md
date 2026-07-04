# TESTING.md — test suite & scenario harness

How to run the tests, what each tier covers, and the standing
instructions for any agent driving the visual scenario suite.

## Commands

```bash
# Full unit + visual suite (no live cloud)
dotnet test tests/Backspace.Tests \
  --filter "FullyQualifiedName!~Cloud&FullyQualifiedName!~E2E"

# Local restic round-trip tests (boots the Backrest sidecar + runs real
# restic against a temp local repo)
dotnet test tests/Backspace.Tests --filter "FullyQualifiedName~E2E"

# Cloud tests (interactive — requires live OAuth tokens via secrets file
# or env var, gated by BACKSPACE_TEST_INTERACTIVE=1)
dotnet test tests/Backspace.Tests --filter "FullyQualifiedName~Cloud"
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
   `<repo>/tests/Backspace.Tests/artifacts/scenarios/<name>/sources/...`
   via `SyntheticFileTree.Generate(...)`. Keep totals small (≤ a few MB
   per scenario) so the suite stays fast.
3. **Drive each scenario** through scripted UI interactions — show
   MainWindow, click buttons, fill text, navigate the wizard, run the
   backup, restore, verify. Each step renders a PNG under
   `artifacts/scenarios/<name>/<NN>-<step>.png`.
4. **Auto-skip secret-gated scenarios** when a required token is
   missing. `SecretsLoader` looks under
   `tests/Backspace.Tests/secrets/` (gitignored) and returns null +
   records the reason; the runner marks the scenario `Skipped(reason)`
   instead of failing.
5. **Write a markdown summary** at `artifacts/scenarios/SUMMARY.md`
   after each suite run, listing every scenario's status, screenshot
   index, and any anomalies the auditor caught.
6. **Iterate.** The agent runs the suite, opens the summary + select
   PNGs, files findings, fixes them in the source, re-runs.

Why: human stays in the loop only for the bits requiring real account
access (Dropbox OAuth round-trips against live API, OneDrive / GDrive
once they ship). Everything else is mechanically verifiable.

**Adding a scenario.** Subclass `ScenarioBase`, override `RunAsync`,
script the user flow with `ctx.Driver` helpers, and call
`ctx.Snapshot("name")` at every interesting state transition. Mark the
class with `[Fact]` so xUnit runs it. Gate secret-dependent flows on
`if (!ctx.Secrets.TryGetDropboxToken(out var token)) { ctx.Skip("…"); return; }`.

## Coverage matrix — every user-driven action vs. its scenario

Built as a deliverable for ROADMAP item 6b (2026-06-12). The goal: no
user-reachable action gets shipped without an E2E or scenario covering
it. Two production bugs (Mount fails on dist, restore crash on long
include list) shipped under green scenario suites this month; the gaps
column below is where the same class of false-green could still hide.

**Status key:**
- ✅  Covered by a scenario or unit test
- 🟡  Partial — covered for one mode/path but not all
- ❌  GAP — no scenario; if it broke we'd hear from a user first

### Onboarding & first-run

| Action | Scenario(s) | Status |
|---|---|---|
| Open onboarding from fresh install | `S01_OnboardingFirstTime` | ✅ |
| Walk all 4 wizard steps | `S01`, `S21_OnboardingScheduleStep` | ✅ |
| Pick source folder via tree picker | `S24_SourceTreePicker` | ✅ |
| Pick destination (local) | `S01`, `E05_EasyModeWizard` | ✅ |
| Pick destination (Dropbox / OneDrive / GDrive) | `E07_DropboxRoundTrip`, `E08`, `E09` | ✅ |
| Pick destination (S3 / Wasabi / R2) | — | ⛔ deferred: needs real bucket creds, same shape as E07 |
| Set schedule | `S21` | ✅ |
| Apply recommended exclusions in onboarding | `S42_EasyAdvancedSwitchAndRecommended` | ✅ |
| Switch Easy → Advanced mid-wizard | `S42` | ✅ |

### Backup creation/edit (Easy + Advanced)

| Action | Scenario(s) | Status |
|---|---|---|
| Create new backup (Easy) | `E05_EasyModeWizard` | ✅ |
| Create new backup (Advanced) | `E06_AdvancedModeEditor` | ✅ |
| Edit existing backup (Easy) | `S07_BackupEditorEdit` (Easy variant) | 🟡 |
| Edit existing backup (Advanced) | `S07_BackupEditorEdit` (Advanced variant) | 🟡 |
| Edit sources, ensure persistence | `E23_EditBackupSourcesPersists` | ✅ |
| Edit filter text inline | `E30_CustomFilter` | ✅ |
| Apply recommended exclusions (full effect on file count) | `E31_FilterExcludesEverything` | ✅ |
| Estimated-savings indicator label states | `S34_SavingsLabelStates` | ✅ |
| Savings message wraps beside Apply button (no crop) | `S50_RecommendedRowTextWraps` | ✅ |
| Skipped-run alerts block visibility | `S28_GraceBlockVisibility` | ✅ |
| Reset grace period (Easy + Advanced) | `S28_GraceBlockVisibility` (Half 4) | ✅ |
| Delete backup | `S41_DestinationAndBackupCrud` | ✅ |

### Backup execution

| Action | Scenario(s) | Status |
|---|---|---|
| Run backup manually | `E01_LocalRoundTrip` (Smoke/Many/Large), `S22_BackupCardRunningState` | ✅ |
| Run backup at scale (≥ 1500 files) | `E34_RestoreLongIncludeList`, `BACKSPACE_E2E_SCALE=huge` lift | ✅ |
| Stop mid-run | `E02_StopAndResume` | ✅ |
| Resume after stop | `E02` | ✅ |
| Two concurrent backups | `E03_TwoConcurrentBackups` | ✅ |
| Two concurrent multi-cloud backups | `E10_TwoConcurrentMultiCloud` | ✅ |
| Queue a 2nd run while one's running | `E21_QueueWhileRunning` | ✅ |
| Skip-on-battery → run aborts cleanly | `S36_BatteryNetworkPreconditions` | ✅ |
| Stop-on-battery → run aborts cleanly | `S36` | ✅ |
| Require-network → run skips when offline | `S36` | ✅ |
| Metered-network watchdog kills mid-run | `S36` | ✅ |
| Catch-up missed runs after sleep | `S36` | ✅ |
| Headless `--run <name>` | `S35_HeadlessRunCli` | ✅ |
| Headless `--run-all` | `S35` | ✅ |
| Schedule fires at the configured time (Task Scheduler) | `TaskSchedulerTests` (unit) | ⛔ deferred: COM-interop with Windows Task Scheduler, unit-covered |

### Restore

| Action | Scenario(s) | Status |
|---|---|---|
| Restore everything | `E01` (round-trip), `S04_RestoreWizardShots` | ✅ |
| Restore from older revision | `E22_RestoreFromOlderRevision`, `S14_RestoreSingleSourceRevisionPicker` | ✅ |
| Restore cherry-picked files | `E17_RestoreCherryPickSubset`, `S15_RestoreFilesStep` | ✅ |
| Restore over original source (overwrite) | `E18_RestoreToOriginalSource` | ✅ |
| Multi-source restore (pick one source) | `S20_RestorePickSourceMulti` | ✅ |
| Restore wizard target step | `S23_RestoreTargetStep` | ✅ |
| Restore wizard summary | `S16_RestoreSummary` | ✅ |
| Restore folder → minimal cover (1 pattern not N files) | `S47_RestoreFolderMinimalCover` | ✅ |
| Restore long include list (CreateProcess ceiling) | `E34_RestoreLongIncludeList` | ✅ |
| Restore over slow network (retries) | — | ⛔ deferred: needs network-shaping infra |
| Restore with bandwidth limit applied | — | ⛔ deferred: needs network-shaping infra |
| Cancel a running restore | `S37_RestoreCancel` | ✅ |
| Live-tail restore progress | `InteractionLiveTailTests` (interaction shape) | 🟡 |

### Mount/Browse snapshots

| Action | Scenario(s) | Status |
|---|---|---|
| Mount snapshot in Explorer (WinFSP) | `E04_SnapshotMountWindows` | ✅ |
| Browse snapshot in-app | `S38_BrowseSnapshotInApp` | ✅ |

### Maintenance

| Action | Scenario(s) | Status |
|---|---|---|
| Prune after backup (inline) | `E14_PruneAfterBackupRealEngine` | ✅ |
| Standalone prune (cadence) | `S39_StandalonePruneAndCheck` | ✅ |
| Check after backup (inline) | `E15_CheckAfterBackupRealEngine` | ✅ |
| Standalone check (cadence) | `S39` | ✅ |
| Test-restore drill | `E16_TestRestoreVerifyDrill` | ✅ |
| Drill skips mid-flight-edited files | `E16` (covered in the drill's RecentEditPolicy path) | ✅ |
| Maintenance policy cards rendered | `S26_MaintenancePolicyCards` | ✅ |

### Destinations

| Action | Scenario(s) | Status |
|---|---|---|
| Add destination (each kind) | `S08_DestinationEditorVariants` | ✅ |
| Edit destination | `S41_DestinationAndBackupCrud` | ✅ |
| Delete destination | `S41` | ✅ |
| Change destination path mid-life | `E33_DestinationPathRenameMidLife` | ✅ |
| Change storage password | `E19_ChangeStoragePassword`, `S09_PasswordDialogs` | ✅ |
| Erase a destination's data | `E20_EraseStorageWipesDataPreservesForeign`, `S10_DestructiveConfirmDialog` | ✅ |
| Destination pill click → edit dialog | `InteractionDestinationClickTests` | ✅ |
| External-drive volume-name matching | — | ⛔ deferred: requires removable media simulation |
| Multi-destination card | `S17_MultiDestinationCard` | ✅ |
| Destinations view busy state | `S25_DestinationsViewBusy` | ✅ |

### Filters

| Action | Scenario(s) | Status |
|---|---|---|
| Hand-edit filter text | `E30_CustomFilter` | ✅ |
| Filter excludes everything (edge) | `E31_FilterExcludesEverything` | ✅ |
| Apply recommended block | `RecommendedFiltersTests` (unit), `S42_EasyAdvancedSwitchAndRecommended` | ✅ |
| Custom-block markers placed above generated | `S30_CustomExclusionMarkerPlacement` | ✅ |
| Filter simulation pane (Simulate button) | `S40_FilterSimulationPane` | ✅ |

### Settings

| Action | Scenario(s) | Status |
|---|---|---|
| Switch accent / theme | `S05_LightDarkThemeShots`, `S12_SettingsAndAccents` | ✅ |
| Configure Healthchecks.io | — | ⛔ deferred: interactive HTTP, secret-gated |
| Configure Resend / Mailgun email | — | ⛔ deferred: interactive SMTP, secret-gated |
| Toggle notification gating per event | `S45_SettingsTogglePersistence` | ✅ |
| Restore-retry cascade settings | `S45` | ✅ |
| Section-header hover wash (no Fluent band leak) | `S49_SettingsExpanderHover` | ✅ |

### Monitoring & notifications

| Action | Scenario(s) | Status |
|---|---|---|
| Toast on Success / Skipped / Failure | `S48_NotificationsWiring` | ✅ |
| Healthchecks.io ping on each run terminal | — | ⛔ deferred: interactive HTTP, secret-gated |
| Email on Failure | — | ⛔ deferred: interactive SMTP, secret-gated |
| Sound alert on Failure | `S48` | ✅ |
| Health banner state matrix | `S13_HealthBannerStateMatrix` | ✅ |

### Edge cases (the boundary set)

| Action | Scenario(s) | Status |
|---|---|---|
| Unicode names (emoji, RTL, CJK, NFD) | `E11_EdgeCaseFixture` + peculiarity set in every E01 size | ✅ |
| Brackets in filenames (`[draft]`) | E01 matrix + `?`-substitution sweep | ✅ |
| Zero-byte / leading/trailing space / dotfiles | peculiarity set | ✅ |
| Compound extensions, long names | peculiarity set, `S18_LongNamesOverflow` | ✅ |
| Future mtimes | peculiarity set | ✅ |
| NTFS-specific shapes (ADS, hidden, system) | peculiarity set | ✅ |
| Locked file during backup (FileShare.None) | peculiarity-set sentinel; opt-in by scenarios | ✅ |
| VSS shadow-copy path | — | ⛔ deferred: Windows VSS infra, requires elevated helper |

### Headless / CLI

| Action | Scenario(s) | Status |
|---|---|---|
| `--run <backup-name>` | `S35_HeadlessRunCli` | ✅ |
| `--run-all` | `S35` | ✅ |
| `--migrate <duplicacy-path>` | — | ⛔ deferred: needs sample Duplicacy config |
| `--version`, `--help` | `S35` | ✅ |

### Status legend

- ✅ — scenario or unit test covers the action.
- 🟡 — partial coverage (one mode/path, or unit-only on a path the
  user reaches through UI). Worth a follow-up scenario when the
  surrounding code is next touched.
- ⛔ — intentionally deferred with a stated reason (interactive
  infrastructure, secret-gated, or requires real removable / VSS /
  network shaping infrastructure). Not a coverage gap that can be
  closed in-process; routes through the secret-loader skip path
  when relevant.
- ❌ — uncovered gap that SHOULD be closeable in-process. Empty in
  the current matrix — every actionable gap has been filled by an
  Sxx scenario in this revamp pass.

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
5. After any fix, re-run and confirm the affected rows flipped to
   `✅ PASS`.

A single fix-and-rerun pulse, not an unbounded loop. Stop and report if
real issues remain after two passes.
