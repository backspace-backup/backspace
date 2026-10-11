# ROADMAP.md — portable mirror of the Review desk

The [local Review desk](http://127.0.0.1:4317/) is the unified work tracker in this checkout. Use its [roadmap board](http://127.0.0.1:4317/roadmap) for future ideas. This file preserves the current open work for repository readers; refresh it from the desk when reviewing comments. The latest saved user decision takes precedence.

The dashboard remains Git-excluded at the user’s request. Copy it separately when moving the checkout. [Working conventions](../AGENTS.md#the-roadmap-workflow-source-of-truth) explain the API, proof and validation rules.

[Full migration report](../audit-dashboard/evidence/agent-work-20261010/roadmap-reconciliation.html) maps all 44 earlier active entries and the complete backlog. [Pre-migration Markdown](../audit-dashboard/evidence/agent-work-20261010/roadmap-before-unification.md) preserves the earlier wording. No user decision or discussion was discarded.

## Actively working

**Checked 10 October 2026:** No implementation or reply is waiting for the agent in the current queue. Parked and deferred plans are listed separately below.

**I need from you:** Cloud account consent and official OneDrive/Google registrations (B001–B003, B034); a Healthchecks check URL or chosen email provider/recipient (B007/B008); native Windows acceptance including elevation, virtual drive, tray/display behavior and updater restart (B004/B023/B024/B032/U001); the actual reboot check (B040); and the restore architecture decision (R007). The desk has the instructions and proof. Other open items await your review of completed work. B035 specifically waits for implementation approval.

**Older review items:** Some requests originated in June and July and still await explicit validation. Their evidence remains available; they have not been silently accepted or removed. N002’s redesign approval is already recorded in its discussion; no second design approval is needed.

### P0: E2E revamp pass — all actionable coverage-matrix rows covered

- **Desk record:** [R006](http://127.0.0.1:4317/#R006)
- **Raised:** 2026-06-12
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** The test inventory now distinguishes clicking the interface from calling internal code. It also identifies which checks use the real backup engine. New checks cover editor interactions, hover animations, Browse, and the packaged app’s backup, maintenance, migration and scheduled-backup commands.
- **Proof — open and look:**
  - [Scheduled-command and input-validation tests](../audit-dashboard/evidence/infrastructure-tests/runtime-coverage-report.html) — All 57 checks passed: 44 covered startup and argument handling, and 13 covered the command generated for scheduled runs.
- **Limitations / adversarial review:** The 10 October broad suite passed 1,081 checks with two credential-dependent skips. Separate real local-engine and 20,000-file / 2 GiB results remain attached. This round adds actual restore animation/card checks and copied-exclusion regression checks. Overlapping batches must not be added together. Real battery, sleep and network changes, reboot, administrator consent and cloud accounts remain explicit acceptance checks.
- **I need from you:** Review the coverage report and its remaining gaps. The checks still needing a real account, a reboot or a Windows consent prompt are listed separately.

### P0: Investigate "why do we deviate from Backrest's native restore RPC?"

- **Desk record:** [R007](http://127.0.0.1:4317/#R007)
- **Raised:** 2026-06-12 (user feedback on the folder-collapse review)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** One selected file or folder uses Backrest’s dedicated Restore API when possible. Several selections, a per-backup speed limit, or significant whitespace use one bundled restic process managed by Backspace. Both paths show progress, stage output safely, and preserve unrelated destination files. Direct restores appear in Backspace but do not appear in Backrest’s separate operation history.
- **Proof — open and look:**
  - 🖼️ [Real selected-file restore in progress](../audit-dashboard/evidence/agent-work-20261010/streaming-progress.png) — An intermediate percentage while two selected files restore with the production speed limit.
  - [Restore architecture: working behavior and its tradeoff](../audit-dashboard/evidence/agent-work-20261010/streaming-restore.html) — One selected file or folder uses Backrest’s dedicated Restore API when possible. Several selections, a per-backup speed limit, or significant whitespace use one bundled restic process managed by Backspace. Both paths show progress, stage output safely, and preserve unrelated destination files. Direct restores appear in Backspace but do not appear in Backrest’s separate operation history.
- **Limitations / adversarial review:** Real bundled Backrest and restic used disposable encrypted local storage. The Stop restore check clicks the real control in headless Avalonia. Live cloud restores, a multi-day run, and Windows refusing to terminate a process remain unverified. The separate buffer tests use synthetic messages; they are not a long-running restore.
- **I need from you:** Approve the tested split between Backrest’s Restore API and a direct restic process for requests that API cannot express. My recommendation is to keep it: it provides live progress and one operation for large selections. The cost is a small process runner to maintain and no Backrest web-history entry for those direct restores.

### P0: Restore enumerates every leaf when a folder is selected

- **Desk record:** [R008](http://127.0.0.1:4317/#R008)
- **Raised:** 2026-06-11
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** A new real-engine test restored 2,397 files from Photos [2026] using one folder pattern. Every file matched its original contents. A similarly named folder was excluded and an existing destination file was preserved. A second restore selected two files and recovered exactly those two.
- **Proof — open and look:**
  - [2,397 bracket-name files: actual restore results](../audit-dashboard/evidence/feedback-20261010/folder-restore.html) — A new real-engine test restored 2,397 files from Photos [2026] using one folder pattern. Every file matched its original contents. A similarly named folder was excluded and an existing destination file was preserved. A second restore selected two files and recovered exactly those two.
- **Limitations / adversarial review:** Real bundled Backrest and restic, encrypted local repository and actual disk output. The test exercises the production selection and restore services; it does not click the restore wizard’s folder checkbox.
- **I need from you:** Open the new folder-restore report. The result to review is simple: 2,397 requested files recovered correctly, zero unwanted files, and the existing destination file unchanged. The report also explains why the individual filename safety list is still retained.

### P1: Adding a cloud destination erases other destinations from shared rclone.conf

- **Desk record:** [A001](http://127.0.0.1:4317/#A001)
- **Raised:** 2026-10-02
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** Each cloud destination now gets its own rclone settings file. Adding or updating one destination therefore leaves the others intact. When rclone refreshes a sign-in token, Backspace saves the newer token in its encrypted credential store. If you sign in again, those new credentials take priority over an older file.
- **Proof — open and look:**
  - [Cloud settings, notifications and skipped-backup tests](../audit-dashboard/evidence/cloud-repairs/report.html) — The report explains the cloud-connection and notification changes, shows their local tests, and identifies the checks that still need real accounts or devices.
- **Limitations / adversarial review:** These tests use files and simulated account responses. They do not include a real cloud sign-in, token expiry or restart. Simultaneous operations on the same destination still rely on rclone to coordinate its token updates and need a real-account check.
- **I need from you:** Open the cloud-settings report to see the two-destination and refreshed-token checks. Real account sign-in and backup tests are tracked in B001, B002 and B003.

### P1: OneDrive remote is unusable even after supplying an OAuth app ID

- **Desk record:** [A002](http://127.0.0.1:4317/#A002)
- **Raised:** 2026-10-02
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** After OneDrive sign-in, Backspace now looks up the account’s default drive and its type before showing Connected. It also fills in missing drive details for older configurations. Tests click Connect and cover both a successful response and refused permission.
- **Proof — open and look:**
  - 🖼️ [OneDrive after a simulated successful sign-in](../audit-dashboard/evidence/cloud-repairs/onedrive-connected.png) — Connected appears only after the editor receives the sign-in result and discovers the drive. Microsoft’s responses were simulated for this screenshot.
  - [Recorded test results and remaining gaps](../audit-dashboard/evidence/repair-verification.html) — Read the pass and fail results, open the original test records if needed, and see what each run did and did not test.
- **Limitations / adversarial review:** The current feature uses the account’s default personal or business drive. It does not offer a SharePoint-site or multiple-drive selector. Tests simulate Microsoft’s responses; real consent and account permissions still need checking. An expired older connection may need another sign-in.
- **I need from you:** Provide the app’s Microsoft registration ID and a disposable Microsoft account for a real sign-in, token refresh, backup and restore check.

### P1: Restore cancellation does not cancel the queued daemon operation

- **Desk record:** [A010](http://127.0.0.1:4317/#A010)
- **Raised:** 2026-10-02
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** Both production restore paths have real cancellation coverage. Native Backrest cancellation and the owned restic process stop work without promoting partial output or replacing existing destination files. The direct-process test clicks the actual Stop restore button.
- **Proof — open and look:**
  - [Actual restore UI and cancellation checks](../audit-dashboard/evidence/agent-work-20261010/restores-final-ui.html) — Measured running/stopped animation, exact-card pointer navigation and preserved destination files.
- **Limitations / adversarial review:** Actual local backup engines and production Avalonia views, rendered headlessly. These results do not prove native Windows tray/display behavior, a live cloud restore or Windows refusing to terminate a process. The separate unconfirmed-outcome safety tests use injected failures.
- **I need from you:** Review the real cancellation report. The existing-file and temporary-directory checks are included; live cloud acceptance remains in B034.

### P1: Every restore has a hard five-minute wait limit

- **Desk record:** [A011](http://127.0.0.1:4317/#A011)
- **Raised:** 2026-10-02
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** Restores now report live progress for several selected files and for backups with a speed limit, as well as single-file and single-folder restores. Progress messages are discarded after display, so they do not fill an ever-growing log. The completed bar shows 100% even when unwanted filename matches were downloaded temporarily and then discarded.
- **Proof — open and look:**
  - 🖼️ [Real selected-file restore in progress](../audit-dashboard/evidence/agent-work-20261010/streaming-progress.png) — An intermediate percentage while two selected files restore with the production speed limit.
  - [Live restore progress, cancellation and damaged-backup results](../audit-dashboard/evidence/agent-work-20261010/streaming-restore.html) — Restores now report live progress for several selected files and for backups with a speed limit, as well as single-file and single-folder restores. Progress messages are discarded after display, so they do not fill an ever-growing log. The completed bar shows 100% even when unwanted filename matches were downloaded temporarily and then discarded.
- **Limitations / adversarial review:** Real bundled Backrest and restic used disposable encrypted local storage. The Stop restore check clicks the real control in headless Avalonia. Live cloud restores, a multi-day run, and Windows refusing to terminate a process remain unverified. The separate buffer tests use synthetic messages; they are not a long-running restore.
- **I need from you:** Review the two screenshots and the test results. The live percentage is implemented and verified with real local backup engines. Live cloud acceptance remains under B034; no multi-day restore was run.

### P1: Explain unsupported Dropbox placeholders without claiming they were backed up

- **Desk record:** [A028](http://127.0.0.1:4317/#A028)
- **Raised:** 2026-10-05
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** The tested Dropbox placeholder stayed online-only, but its contents were absent from the backup. The app now reports the unsupported file and warning honestly. The separate feature for backing up cloud contents without changing local availability is parked under B035 until your go-ahead.
- **Proof — open and look:**
  - [10 October: answer and supporting detail](../audit-dashboard/evidence/feedback-20261010/cloud-source.html) — The tested Dropbox placeholder stayed online-only, but its contents were absent from the backup. The app now reports the unsupported file and warning honestly. The separate feature for backing up cloud contents without changing local availability is parked under B035 until your go-ahead.
- **Limitations / adversarial review:** The earlier placeholder test did not back up its contents. The separate online-only backup feature is planned in B035 and requires your go-ahead.
- **I need from you:** Review the warning fix here. The requested online-only backup feature is tracked separately under B035, including separate temporary downloads, metadata checks, a space limit and ignore-by-default behavior.

### P1: Loading Settings must not rewrite saved preferences

- **Desk record:** [A031](http://127.0.0.1:4317/#A031)
- **Raised:** 2026-10-10
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** Opening or refreshing Settings now only reads saved preferences. It no longer writes them back while loading. Deliberate changes still save, and a queued refresh is ignored after the screen closes.
- **Proof — open and look:**
  - [Settings: unwanted writes reproduced and fixed](../audit-dashboard/evidence/agent-work-20261010/settings-refresh.html) — Four unwanted writes before the fix; zero on opening after it. Intentional edits still save.
- **Limitations / adversarial review:** The regression test uses the real settings and configuration components through their bound properties. It does not click native controls. Existing Settings interaction scenarios also pass in the broader run.
- **I need from you:** Review the before-and-after report. The same test first failed with four unwanted writes and now passes, including intentional edits and a refresh queued before closing.

### P1: Keep every custom rule when exclusion blocks have been copied

- **Desk record:** [A032](http://127.0.0.1:4317/#A032)
- **Raised:** 2026-10-10 (mirror reconciled)
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** Reapplying recommendations used to process only the first copied block. It now preserves all custom rules in order and replaces all complete generated blocks. Applying twice leaves the result unchanged.
- **Proof — open and look:**
  - 🖼️ [Both custom rules preserved](../audit-dashboard/evidence/agent-work-20261010/copied-exclusions.png) — Both copied custom rules appear in one block above the generated recommendations.
  - [Exclusion repair and negative tests](../audit-dashboard/evidence/agent-work-20261010/copied-exclusions.html) — What was wrong, what the actual clicks verified, and what malformed input preserves.
- **Limitations / adversarial review:** The real editor’s Apply, Cancel and Save controls and a fresh configuration load were tested. Malformed marker and comment-preservation cases also passed. No backup engine operation is needed to establish this text-editing behavior.
- **I need from you:** Review the screenshot: both *.private and *.personal should appear in one custom block above the recommendations.

### P1: VSS and elevation on Windows

- **Desk record:** [B004](http://127.0.0.1:4317/#B004)
- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** Windows backup operations now request the elevated helper when the VSS policy requires it. VSS is the Windows snapshot feature used to read files that may be in use. Automated tests cover choosing that route and keeping the repository protected while the helper works.
- **Proof — open and look:**
  - [Background-process and settings safety checks](../audit-dashboard/evidence/infrastructure-tests/final-report.html) — All 50 focused checks passed. An earlier broad run recorded 933 passes, no failures and 2 skips that required credentials.
- **Limitations / adversarial review:** The real administrator-consent prompt has not been accepted during this audit. We have not yet proved that a locked file is included in a real elevated snapshot or that cancelling that elevated run behaves correctly.
- **I need from you:** Choose a disposable locked-file backup for a hands-on test of the Windows administrator prompt, VSS and cancellation.

### P1: Test restore against the original bytes

- **Desk record:** [B006](http://127.0.0.1:4317/#B006)
- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** The Test restore button restores every snapshot file from each source at each destination into temporary batches, then compares contents with local originals when they are still unchanged. The actual button check restored all 31 files from two sources, including an empty file. A second click detected a deliberately changed file of the same size and date and reported failure. The outdated “10 random files” tooltip was corrected; hovering over the result now shows the file counts or failure summary.
- **Proof — open and look:**
  - 🖼️ [Test restore button: all 31 files match](../audit-dashboard/evidence/issue-context-20261010/test-restore-success.png) — Two source folders, an empty file, real Backrest/restic. The result tooltip shows 31 restored and 31 byte-identical.
  - [What the Test restore button checks](../audit-dashboard/evidence/issue-context-20261010/test-restore.html) — Actual feature flow, test results, temporary space, changed originals and limits.
- **Limitations / adversarial review:** Actual button clicks in headless Avalonia with real Backrest/restic and one encrypted local destination. All 17 focused tests passed; 16 use controlled inputs or the in-memory engine. This does not prove live cloud behavior or Windows metadata recovery. Changed or missing originals cannot be byte-compared and are reported separately.
- **I need from you:** Review the success and deliberate-mismatch screenshots for the Test restore button. The explanation of your earlier comment and the recovered issue wording are in the discussion below.

### P1: Healthchecks start, failure and completion pings

- **Desk record:** [B007](http://127.0.0.1:4317/#B007)
- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** Backrest provides native Healthchecks hooks. Backspace currently reads Backrest operation results but sends its own Healthchecks signals for the whole Backspace backup, which can contain several sources, destinations and retries.
- **Proof — open and look:**
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
- **Limitations / adversarial review:** The current app does not configure Backrest Healthchecks hooks. Local tests exercise request contents and timing using a test server; they do not prove delivery to your service.
- **I need from you:** Review the explanation of the remaining wrapper. Actual delivery to your Healthchecks service still needs a real check URL.

### P3: "Restores" multi-restore cards

- **Desk record:** [B018](http://127.0.0.1:4317/#B018)
- **Raised:** 2026-06-12
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** Two actual restore operations produce two separate cards. Opening the cancelled restore shows only that result; returning to All restores brings back both cards.
- **Proof — open and look:**
  - 🖼️ [The clicked cancelled result](../audit-dashboard/evidence/agent-work-20261010/real-restore-selected.png) — Only the selected cancelled result is open; All restores returns to the two-run list.
  - [Actual restore UI and cancellation checks](../audit-dashboard/evidence/agent-work-20261010/restores-final-ui.html) — Measured running/stopped animation, exact-card pointer navigation and preserved destination files.
- **Limitations / adversarial review:** Actual local backup engines and production Avalonia views, rendered headlessly. These results do not prove native Windows tray/display behavior, a live cloud restore or Windows refusing to terminate a process. The separate unconfirmed-outcome safety tests use injected failures.
- **I need from you:** Compare the two-card screenshot with the selected cancelled result. Each restore should appear once in the appropriate view.

### P1: Return from a restore card to that exact restore

- **Desk record:** [B019](http://127.0.0.1:4317/#B019)
- **Raised:** 2026-10-10 (mirror reconciled)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** A pointer click on the second actual restore opens that exact cancelled session. The successful first session is not selected or duplicated. The All restores button returns to both records.
- **Proof — open and look:**
  - 🖼️ [The clicked cancelled result](../audit-dashboard/evidence/agent-work-20261010/real-restore-selected.png) — Only the selected cancelled result is open; All restores returns to the two-run list.
  - [Actual restore UI and cancellation checks](../audit-dashboard/evidence/agent-work-20261010/restores-final-ui.html) — Measured running/stopped animation, exact-card pointer navigation and preserved destination files.
- **Limitations / adversarial review:** Actual local backup engines and production Avalonia views, rendered headlessly. These results do not prove native Windows tray/display behavior, a live cloud restore or Windows refusing to terminate a process. The separate unconfirmed-outcome safety tests use injected failures.
- **I need from you:** Compare the list and selected-result screenshots to review the navigation.

### P1: Explain quitting during active operations

- **Desk record:** [B023](http://127.0.0.1:4317/#B023)
- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** The tray now checks actual Windows scheduled tasks and shows Schedules ready, paused, off, needing attention or not verified. It refreshes after task and configuration changes, when you open the app or tray menu, and once when a snooze expires. The menu shows when tasks were checked. A separate blue badge indicates work in progress. The existing close-to-tray, Quit and Force quit controls remain available.
- **Proof — open and look:**
  - 🖼️ [The actual tray badge artwork](../audit-dashboard/evidence/remaining-work-20261009/tray-status-icons.png) — Five production badges and their menu labels, at small size and enlarged. This gallery is headless, not the Windows notification area.
  - [Tray checks and their limits](../audit-dashboard/evidence/remaining-work-20261009/report.html) — Read the passing results, deliberately altered task definitions, and native checks still outstanding.
- **Limitations / adversarial review:** Full local suite: 1,023 passed, two cloud credential skips, no failures. Tests use real Windows task-definition objects, production icon rendering and headless Avalonia controls. They do not prove native notification-area behavior, a real reboot or forced exit with a live engine.
- **I need from you:** The implementation and automated checks are complete. A native desktop check still needs to confirm the actual Windows tray menu and Force quit with an isolated running backup. Native computer control is unavailable in this session. A ready schedule does not guarantee that its drive, network or cloud sign-in will be available at run time.

### P1: Tolerable destination outages are skipped

- **Desk record:** [B025](http://127.0.0.1:4317/#B025)
- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** Expected availability problems remain Skipped. When the grace period expires, the app sends a warning that says the backup was skipped and explains why. Other errors remain failures; some are retried first.
- **Proof — open and look:**
  - 🖼️ [A skipped backup produces a warning](../audit-dashboard/evidence/review-feedback/03-skipped-warning-is-not-a-failure.png) — The actual alert says skipped and names the disconnected drive. It does not label this as a failed backup.
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
- **Limitations / adversarial review:** The popup is the actual production view, but the outage is prepared test data. Router, VPN, NAS and physical-drive disconnections remain separate live checks.
- **I need from you:** The classification and interface checks pass with simulated availability and errors. A real provider-outage check still needs the disposable account and consent requested in B034; that is why this remains Still needs testing. Post the provider you want to test in B034 so these related checks can be done together.

### P1: Unexpected destination errors remain failures

- **Desk record:** [B026](http://127.0.0.1:4317/#B026)
- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** The offline handling does not hide other errors. Permission problems and unexpected missing-path errors keep their failure result instead of being treated as a harmless unavailable destination.
- **Proof — open and look:**
  - 🖼️ [Set a quiet period before going away](../audit-dashboard/evidence/cloud-repairs/easy-grace-before-holiday.png) — A healthy backup already shows the 14-day selector and Reset. The explanation says later skipped backups produce warnings, not failures.
  - [Cloud settings, notifications and skipped-backup tests](../audit-dashboard/evidence/cloud-repairs/report.html) — The report explains the cloud-connection and notification changes, shows their local tests, and identifies the checks that still need real accounts or devices.
- **Limitations / adversarial review:** The tests use controlled error responses. Real network hardware and provider-specific failure modes still need their own checks.
- **I need from you:** The classification and interface checks pass with simulated availability and errors. A real provider-outage check still needs the disposable account and consent requested in B034; that is why this remains Still needs testing. Post the provider you want to test in B034 so these related checks can be done together.

### P1: After grace, skipped backups warn without failing

- **Desk record:** [B029](http://127.0.0.1:4317/#B029)
- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** When the grace period expires, repeated skipped backups produce a warning. The stored run still says Skipped, not Failed. Reset quiets subsequent skip notifications for a new grace period.
- **Proof — open and look:**
  - 🖼️ [Set a quiet period before going away](../audit-dashboard/evidence/cloud-repairs/easy-grace-before-holiday.png) — A healthy backup already shows the 14-day selector and Reset. The explanation says later skipped backups produce warnings, not failures.
  - [Cloud settings, notifications and skipped-backup tests](../audit-dashboard/evidence/cloud-repairs/report.html) — The report explains the cloud-connection and notification changes, shows their local tests, and identifies the checks that still need real accounts or devices.
- **Limitations / adversarial review:** Notification timing is tested with controlled dates and error responses. No live monitoring service or email provider was contacted.
- **I need from you:** The classification and interface checks pass with simulated availability and errors. A real provider-outage check still needs the disposable account and consent requested in B034; that is why this remains Still needs testing. Post the provider you want to test in B034 so these related checks can be done together.

### P1: Keep relocation separate from offline handling

- **Desk record:** [B030](http://127.0.0.1:4317/#B030)
- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** Locate moved repository is available for an existing local, external-drive or network destination regardless of the last backup result. Its optional suggestion in a skipped-run message requires at least one earlier success. The chosen folder is checked with real Restic before a path change is offered.
- **Proof — open and look:**
  - 🖼️ [Confirm a repository that Restic actually opened](../audit-dashboard/evidence/review-feedback/03-repository-confirmation.png) — The confirmation says the saved password works, and explains why that alone does not identify the original repository.
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
- **Limitations / adversarial review:** The test supplies the folder path that a picker would return and clicks the production confirmation buttons. It uses the real engine, but it does not automate the Windows folder picker. Password access alone cannot identify the original repository.
- **I need from you:** Open the confirmation and wrong-password screenshots. The real-engine test moves a repository, reuses it, keeps the earlier snapshot and restores the new backup correctly. The native Windows folder picker remains a separate check.

### P1: Clarify when Expected Drive Letter is useful

- **Desk record:** [B032](http://127.0.0.1:4317/#B032)
- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** This remains Still needs testing. A prepared Windows check creates its own small virtual disk, gives it a unique label, checks it through the production drive detector, changes its letter and checks again.
- **Proof — open and look:**
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
- **Limitations / adversarial review:** The script checks the identity of its newly created virtual disk before formatting it and never selects an existing physical disk. The current agent session is not elevated, so no mounted-disk result is claimed.
- **I need from you:** Run the prepared virtual-drive check from an administrator PowerShell session, then ask me to inspect its result. It has not yet run with the required Windows privileges.

### P1: Failure notification layout and link to logs

- **Desk record:** [B036](http://127.0.0.1:4317/#B036)
- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** The test now renders the real failure popup and pointer-clicks its Open run details button. It verifies that the earlier failed run is selected even when a newer successful run exists, and that a missing run ID does not silently select an unrelated run.
- **Proof — open and look:**
  - 🖼️ [Failure notification before clicking](../audit-dashboard/evidence/review-feedback/01-failure-popup-before-click.png) — The actual alert has a readable title, the error, and an Open run details button.
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
- **Limitations / adversarial review:** The alert handling and popup are production code; history is prepared to create the exact earlier-failure/newer-success case. This is a headless interface test, not a screenshot of a native Windows notification arriving during a live failure.
- **I need from you:** Open the before-click popup and after-click log screenshots. Windows desktop placement and focus still need a native check.

### P1: Require current screenshots and documentation before a GitHub release

- **Desk record:** [N011](http://127.0.0.1:4317/#N011)
- **Raised:** 2026-10-05
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** Release approval is a human judgment supported by automated evidence. The workflow produces tests, current screenshots and the exact candidate, then waits for a required reviewer. The release guide and GitHub summary now explicitly ask the reviewer to compare README, guides, website images and captions with that candidate and record the reviewed revision.
- **Proof — open and look:**
  - 🖼️ [GitHub waiting for human approval](../audit-dashboard/evidence/remaining-work-20261009/release-approval-waiting.png) — Windows checks passed; the publishing job has not started.
  - [10 October: answer and supporting detail](../audit-dashboard/evidence/feedback-20261010/release-review.html) — Release approval is a human judgment supported by automated evidence. The workflow produces tests, current screenshots and the exact candidate, then waits for a required reviewer. The release guide and GitHub summary now explicitly ask the reviewer to compare README, guides, website images and captions with that candidate and record the reviewed revision.
- **Limitations / adversarial review:** The approval gate was exercised in the earlier GitHub rehearsal. The new checklist wording has been checked locally, not by publishing another release. Repository administrators can still change permissions or publish through other routes.
- **I need from you:** Review the clarified process. The agent prepares and corrects the documentation; the human makes the final accuracy judgment before publishing. The existing gate rehearsal is evidence that publishing waits, not proof that a person read every document.

### P1: Stop engine startup if local authentication cannot be enabled

- **Desk record:** [N017](http://127.0.0.1:4317/#N017)
- **Raised:** 2026-10-10
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** The document review uncovered two startup defects. Authentication setup errors could be ignored, and an already configured engine could time out because the readiness check expected anonymous access. Startup now requires a successful login, treats a sign-in challenge as readiness, and only then makes the engine available to other operations.
- **Proof — open and look:**
  - [10 October: answer and supporting detail](../audit-dashboard/evidence/feedback-20261010/authentication.html) — The document review uncovered two startup defects. Authentication setup errors could be ignored, and an already configured engine could time out because the readiness check expected anonymous access. Startup now requires a successful login, treats a sign-in challenge as readiness, and only then makes the engine available to other operations.
- **Limitations / adversarial review:** The real test uses disposable local engine state. Controlled HTTP failures separately cover the first-time hash, configuration and login steps. It does not claim that the brief initial authentication setup window has been eliminated or that every local security boundary has been audited.
- **I need from you:** Open the authentication report. It shows the real daemon rejecting anonymous access, shutting down after a deliberately wrong saved password, preserving its configuration, and starting successfully after the test password is corrected.

### P1: Restore target radios ping-pong forever (test-suite hang)

- **Desk record:** [R001](http://127.0.0.1:4317/#R001)
- **Raised:** 2026-07-04 (found by us: the visual suite sat 40+ minutes
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** The restore destination options now change cleanly between the original source and another folder. The interaction test clicks both options and confirms that the destination changes each time without the former layout loop.
- **Proof — open and look:**
  - 🖼️ [Restore into a different folder](../audit-dashboard/evidence/carryovers/restore-target-other-folder.png) — The lower option is selected, the destination field and Browse button are visible, and the original-source overwrite warning remains above.
  - [Results for the earlier roadmap items](../audit-dashboard/evidence/carryovers/report.html) — Read the checks for this item, see which ones use the real interface or engine, and review the remaining gaps.
- **Limitations / adversarial review:** The pointer-click test runs in Avalonia’s automated UI host rather than a separately packaged app process.
- **I need from you:** Open the two destination states and confirm that the choices and warning are clear.

### P1: Add graceful Windows updates from GitHub releases

- **Desk record:** [U001](http://127.0.0.1:4317/#U001)
- **Raised:** 2026-10-09
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** Windows Settings now offers periodic update checks, release notes, skipping a version, and an explicit download-and-restart action. Backspace verifies the full release bundle, waits for active work, preserves settings, and retains a recovery copy.
- **Proof — open and look:**
  - 🖼️ [App updates in Settings](../audit-dashboard/evidence/remaining-work-20261009/update-light.png) — Check frequency, release notes, and explicit restart or skip choices; the displayed release is simulated.
  - [Windows updates: screenshots, test results and remaining checks](../audit-dashboard/evidence/remaining-work-20261009/windows-updates.html) — See both Settings themes, what the automated tests really exercised, and the native acceptance steps.
- **Limitations / adversarial review:** 34 focused checks passed, plus a test using the real portable Windows bundle and native version command. No public release download, full GUI restart, SmartScreen prompt, or physically full disk was tested.
- **I need from you:** Follow the pending native Windows acceptance instructions once approved packaged versions are available. Automated checks and the non-publishing GitHub rehearsal are complete; desktop restart, recovery and security prompts are not yet verified.

### P2: Custom Healthchecks management server does not control the ping host

- **Desk record:** [A003](http://127.0.0.1:4317/#A003)
- **Raised:** 2026-10-02
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** Backspace now saves and uses the ping address returned by the Healthchecks server. A check created on a self-hosted server therefore sends its backup signals to that server, rather than always sending them to the public service.
- **Proof — open and look:**
  - [Cloud settings, notifications and skipped-backup tests](../audit-dashboard/evidence/cloud-repairs/report.html) — The report explains the cloud-connection and notification changes, shows their local tests, and identifies the checks that still need real accounts or devices.
- **Limitations / adversarial review:** No live server was contacted. An older configuration containing only a check ID still uses hc-ping.com until the check is verified again. A failed signal is logged without changing the backup result.
- **I need from you:** Review the custom-server checks in the cloud report. The service’s returned ping address should be used for start, failure and completion signals.

### P2: Dropbox with our own app ID

- **Desk record:** [B001](http://127.0.0.1:4317/#B001)
- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** The cloud connection code now keeps each destination’s settings separate and saves refreshed sign-in tokens securely. The local tests cover those behaviors. A real Dropbox account is still needed to finish checking the complete sign-in, backup and restore journey.
- **Proof — open and look:**
  - [Cloud settings, notifications and skipped-backup tests](../audit-dashboard/evidence/cloud-repairs/report.html) — The report explains the cloud-connection and notification changes, shows their local tests, and identifies the checks that still need real accounts or devices.
- **Limitations / adversarial review:** No real Dropbox sign-in or cloud backup was performed. File-based token tests cannot prove that Dropbox accepts the registration or refreshes the connection in practice.
- **I need from you:** Authorize a disposable Dropbox account so we can test sign-in, token refresh after a restart, backup and restore, then compare the restored files with the originals.

### P3: OneDrive + Google Drive OAuth client_ids

- **Desk record:** [B002](http://127.0.0.1:4317/#B002)
- **Raised:** 2026-06-12 (audit of BACKSPACE_PORT_DESIGN.md § 6.2)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** The editor now discovers the signed-in account’s default OneDrive before showing Connected. Tests click Connect and simulate both successful permission and refused permission. The official Microsoft app registration and a real account check remain outstanding.
- **Proof — open and look:**
  - 🖼️ [OneDrive after a simulated successful sign-in](../audit-dashboard/evidence/cloud-repairs/onedrive-connected.png) — Connected appears only after the editor receives the sign-in result and discovers the drive. Microsoft’s responses were simulated for this screenshot.
  - [Cloud settings, notifications and skipped-backup tests](../audit-dashboard/evidence/cloud-repairs/report.html) — The report explains the cloud-connection and notification changes, shows their local tests, and identifies the checks that still need real accounts or devices.
- **Limitations / adversarial review:** The current scope is the default personal or business drive, without a SharePoint-site or multiple-drive selector. Microsoft’s real consent screen and account permissions have not been tested.
- **I need from you:** Provide the Microsoft app registration ID and authorize a disposable account for sign-in, refresh, backup and restore testing.

### P2: Connect Google Drive using Backspace’s own app registration

- **Desk record:** [B003](http://127.0.0.1:4317/#B003)
- **Raised:** 2026-10-10 (mirror reconciled)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** Google Drive settings now match the requested permission: access to files created or opened through this app. The destination editor explains that restriction. The official Google app registration and a real account check are still needed.
- **Proof — open and look:**
  - [Cloud settings, notifications and skipped-backup tests](../audit-dashboard/evidence/cloud-repairs/report.html) — The report explains the cloud-connection and notification changes, shows their local tests, and identifies the checks that still need real accounts or devices.
- **Limitations / adversarial review:** No real Google account was used. A repository created through another app registration may be inaccessible with this restricted permission.
- **I need from you:** Provide the Google app registration ID and authorize a disposable account for sign-in, token refresh, backup and restore testing.

### P2: Email delivery integration

- **Desk record:** [B008](http://127.0.0.1:4317/#B008)
- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** Backrest supports email delivery through Shoutrrr. Backspace currently sends through its own Resend or Mailgun integration after combining the results of all parts of a backup.
- **Proof — open and look:**
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
- **Limitations / adversarial review:** No email was sent. Local request tests do not prove delivery or inbox appearance. The app has not been migrated to Backrest or Shoutrrr delivery.
- **I need from you:** Review the distinction between a Backrest operation alert and a complete Backspace backup summary. A real delivery test needs a configured provider and your chosen test recipient.

### P2: Useful logs for every operation

- **Desk record:** [B009](http://127.0.0.1:4317/#B009)
- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** Backspace now copies the engine’s original operation diagnostics into its saved run log. In the actual Dropbox test, the log now names the omitted file and includes Restic’s unsupported-file error instead of only saying that some files could not be read.
- **Proof — open and look:**
  - 🖼️ [Returning to Backups shows a background failure](../audit-dashboard/evidence/cloud-repairs/external-scheduled-backup-card.png) — The card and health banner show a failure written by a separate settings/history writer while the app was already open.
  - [Actual Dropbox test: no download and no backed-up contents](../audit-dashboard/evidence/cloud-placeholder-20261005/report.html) — See the file measurements, snapshot checks and original engine error. The supplied file remained online-only.
- **Limitations / adversarial review:** The Dropbox backup is a real engine run. Additional protocol tests cover fragmented log text and failure to fetch diagnostics. This does not prove every cloud operation and provider.
- **I need from you:** Open the Dropbox report and saved log. Wider checks against real cloud accounts remain outstanding.

### P2: Review the referenced Word document

- **Desk record:** [B010](http://127.0.0.1:4317/#B010)
- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** I reviewed the supplied Backspace TODO.docx in full. Backspace now implements its Option 2: an Avalonia desktop app with a bundled Backrest daemon driving restic. The document is an earlier architecture discussion, not a list of entirely missing features. The attached comparison distinguishes implemented behavior, deliberate differences, remaining work and claims in the document that should not guide new work.
- **Proof — open and look:**
  - [10 October: answer and supporting detail](../audit-dashboard/evidence/feedback-20261010/word-comparison.html) — I reviewed the supplied Backspace TODO.docx in full. Backspace now implements its Option 2: an Avalonia desktop app with a bundled Backrest daemon driving restic. The document is an earlier architecture discussion, not a list of entirely missing features. The attached comparison distinguishes implemented behavior, deliberate differences, remaining work and claims in the document that should not guide new work.
- **Limitations / adversarial review:** The comparison combines code inspection and existing test evidence; it is not fresh live-cloud, reboot, VSS or mounting validation. The original document is preserved and its extracted text is included unchanged in the report.
- **I need from you:** Review the Word-document comparison and the current restore architecture in R007. The online-only feature is now parked in B035 at your request. Native/account acceptance remains explicitly listed; no second engine rewrite is proposed.

### P2: Animate the sidebar icon while a restore is running

- **Desk record:** [B017](http://127.0.0.1:4317/#B017)
- **Raised:** 2026-10-10 (mirror reconciled)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** The actual sidebar arrow was sampled during a real, speed-limited local restore. It moved while the restore ran and stopped after the successful and cancelled operations finished.
- **Proof — open and look:**
  - 🖼️ [Two restore cards keep separate progress](../audit-dashboard/evidence/restore-repairs/screenshots/sessions-independent-progress.png) — BackupA shows 12 of 50 files, while BackupB shows 60 of 80. Each card follows its own session updates.
  - [Actual restore UI and cancellation checks](../audit-dashboard/evidence/agent-work-20261010/restores-final-ui.html) — Measured running/stopped animation, exact-card pointer navigation and preserved destination files.
- **Limitations / adversarial review:** Actual local backup engines and production Avalonia views, rendered headlessly. These results do not prove native Windows tray/display behavior, a live cloud restore or Windows refusing to terminate a process. The separate unconfirmed-outcome safety tests use injected failures.
- **I need from you:** Open the animation and restore report. The motion is now measured automatically; you no longer need to establish it by watching the preview.

### P2: Measure idle tray CPU use

- **Desk record:** [B024](http://127.0.0.1:4317/#B024)
- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** Backspace now uses Avalonia 12.1.3, whose rendering loop sleeps when idle. Matched native measurements show less CPU use than the old framework. A separate low-overhead, one-minute sample measured about 0.0036% of this machine’s total CPU with the window hidden; Backrest used no measurable CPU in that interval.
- **Proof — open and look:**
  - [Measured idle CPU before and after the framework update](../audit-dashboard/evidence/agent-work-20261010/idle-cpu-12.html) — Backspace now uses Avalonia 12.1.3, whose rendering loop sleeps when idle. Matched native measurements show less CPU use than the old framework. A separate low-overhead, one-minute sample measured about 0.0036% of this machine’s total CPU with the window hidden; Backrest used no measurable CPU in that interval.
- **Limitations / adversarial review:** Production window and styles in a native Windows host, real owned Backrest, empty disposable configuration. No tray icon, scheduled task registration, update checks or active backups in the CPU probe. Detailed and low-overhead samplers are identified separately. An artificial 125% bitmap capture showed displaced content; actual scaled-display acceptance is still needed. Windows screenshot inspection timed out awaiting app approval.
- **I need from you:** Review the measurements, then check the actual app at your Windows display scale and leave it idle in the tray while watching Task Manager. Confirm that the layout remains correct and CPU settles near zero. The isolated native-window reduction is measured, but the full tray and scaled-display check remains pending. B040 retains the reboot test.

### P2: Interactive OAuth test switch and reusable tokens

- **Desk record:** [B034](http://127.0.0.1:4317/#B034)
- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** The interactive sign-in switch and saved-token reuse exist, but successful authentication against your actual provider accounts is still unproven. This issue remains Still needs testing.
- **Proof — open and look:**
  - 🖼️ [OneDrive after a simulated successful sign-in](../audit-dashboard/evidence/cloud-repairs/onedrive-connected.png) — Connected appears only after the editor receives the sign-in result and discovers the drive. Microsoft’s responses were simulated for this screenshot.
  - [The pending-test instructions are visible in the issue](../audit-dashboard/evidence/remaining-work-20261009/report.html) — The highlighted panel contains the steps and what to report back. Both tests remain explicitly unverified.
- **Limitations / adversarial review:** OneDrive and Google Drive need valid app-registration details. Mock authorization responses cannot prove provider consent, refreshed tokens or real file delivery.
- **I need from you:** Pending test from you: Connect a real cloud account, then back up and restore test files. Follow the numbered instructions above and post the result here.

### P2: Prove the schedule fires after a real reboot

- **Desk record:** [B040](http://127.0.0.1:4317/#B040)
- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** This is now explicitly Still needs testing. A prepared script creates an isolated local backup and a Windows scheduled task using the production task definition. After a real reboot, it checks when the task ran and compares a file recovered from the resulting snapshot.
- **Proof — open and look:**
  - [The pending-test instructions are visible in the issue](../audit-dashboard/evidence/remaining-work-20261009/report.html) — The highlighted panel contains the steps and what to report back. Both tests remain explicitly unverified.
- **Limitations / adversarial review:** Only task-definition generation has been checked so far. No real reboot result is claimed. The planned test covers a local folder, not cloud authentication, network shares or other users’ private files.
- **I need from you:** Pending test from you: Run one scheduled backup across a real Windows restart. Follow the numbered instructions above and post the result here.

### P2: Restore the first snapshot to a different folder

- **Desk record:** [B041](http://127.0.0.1:4317/#B041)
- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** A real local-engine test restores an earlier snapshot into a new folder and compares every ordinary file with the saved originals. It includes empty files and checks that the restored data is the older version rather than the changed current source.
- **Proof — open and look:**
  - [Recorded test results and remaining gaps](../audit-dashboard/evidence/repair-verification.html) — Read the pass and fail results, open the original test records if needed, and see what each run did and did not test.
- **Limitations / adversarial review:** This uses disposable local data. It does not cover real cloud accounts or unsupported filesystem metadata.
- **I need from you:** Open the restore verification report and review the older-version file comparisons.

### P2: Compare restored files with the original hashes

- **Desk record:** [B042](http://127.0.0.1:4317/#B042)
- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** The large restore recovered 20,000 files totaling 2 GiB. Every restored file’s full SHA-256 hash matched its original, with no failures. Smaller tests also check that the comparison catches changed content and the wrong backup version.
- **Proof — open and look:**
  - 🖼️ [The app after the 20,000-file restore](../audit-dashboard/evidence/huge-scale/E34-restore-long-include-list/screenshots/03-after-restore.png) — Backspace remains open after the real restore. Use the report for the file counts and content comparisons; the card alone cannot prove file integrity.
  - [20,000 restored files, each compared with its original](../audit-dashboard/evidence/huge-scale/report.html) — See the source and restored file counts, the 2 GiB total, the full SHA-256 comparison result, and the limits on metadata preservation.
- **Limitations / adversarial review:** The data is disposable and local. Matching file contents does not establish that all permissions, special metadata or cloud-provider behavior are supported.
- **I need from you:** Open the large-data report to see the source and restored counts and the full-file comparison result.

### P2: Review the redesign, with an easy way to revert it

- **Desk record:** [N002](http://127.0.0.1:4317/#N002)
- **Raised:** 2026-10-10 (mirror reconciled)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** You approved the redesign on 10 October 2026. The appearance approval is recorded; it is no longer a pending decision. Functional and native Windows checks remain tracked separately.
- **Proof — open and look:**
  - 🖼️ [The revised Backups page](../audit-dashboard/evidence/revision-after/full/02-backups-overview.png) — Manrope headings, aligned content and a clear navigation selection.
  - [10 October: answer and supporting detail](../audit-dashboard/evidence/feedback-20261010/report.html) — You approved the redesign on 10 October 2026. The appearance approval is recorded; it is no longer a pending decision. Functional and native Windows checks remain tracked separately.
- **Limitations / adversarial review:** The gallery uses the same-sized windows for each comparison. Small-laptop screenshots are shown separately. Tests use disposable local data; your aesthetic approval is still needed, and no completion box is checked on your behalf.
- **I need from you:** No further design approval is needed. Your approval is preserved in the discussion and the corresponding active roadmap entry is removed.

### P2: Remove the backup setup time estimate

- **Desk record:** [N018](http://127.0.0.1:4317/#N018)
- **Raised:** 2026-10-10
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** The setup invitation now says “Set up a backup”, without a time estimate.
- **Proof — open and look:**
  - 🖼️ [Set up a backup, without an estimate](../audit-dashboard/evidence/agent-work-20261010/setup-without-time-estimate.png) — The setup button has no duration claim.
- **Limitations / adversarial review:** Actual empty-state UI rendered in headless Avalonia. This change only removes wording.
- **I need from you:** Review the screenshot and confirm the wording.

### P2: Add a roadmap board to the Review desk

- **Desk record:** [N019](http://127.0.0.1:4317/#N019)
- **Raised:** 2026-10-10
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** The roadmap board is ready. Next, Soon, Later and Parked use the same records as active issues. Open a card to discuss it or change its stage; Start work gives the agent your go-ahead. Column headings fold and expand the board.
- **Proof — open and look:**
  - 🖼️ [Roadmap board](../audit-dashboard/evidence/agent-work-20261010/roadmap-board.png) — Planned improvements and the parked cloud feature have their own columns; active review remains available.
  - [Board checks and usage](../audit-dashboard/evidence/agent-work-20261010/roadmap-review.html) — The roadmap board is ready. Next, Soon, Later and Parked use the same records as active issues. Open a card to discuss it or change its stage; Start work gives the agent your go-ahead. Column headings fold and expand the board.
- **Limitations / adversarial review:** The real browser checks used disposable records to create an idea, preserve exact text, post a comment, activate it, park it again and reload. Server tests also restart the process and reject stale edits. Layout was checked at 1366, 1024 and 390 pixels without page overflow. No real planned feature was activated during testing.
- **I need from you:** Open the Roadmap page and review the board. Future ideas stay out of the active-issue queue until you start them.

### P2: Reconcile the Markdown roadmap with the Review desk

- **Desk record:** [N020](http://127.0.0.1:4317/#N020)
- **Raised:** 2026-10-10
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** All 44 earlier active Markdown entries and 21 backlog/history groups are mapped in the reconciliation report. Existing work keeps its IDs; deferred improvements now have roadmap cards. The audit found and fixed the copied-exclusion defect rather than hiding it among future ideas.
- **Proof — open and look:**
  - 🖼️ [Roadmap board](../audit-dashboard/evidence/agent-work-20261010/roadmap-board.png) — Planned improvements and the parked cloud feature have their own columns; active review remains available.
  - [Every old roadmap entry mapped](../audit-dashboard/evidence/agent-work-20261010/roadmap-reconciliation.html) — All 44 earlier active Markdown entries and 21 backlog/history groups are mapped in the reconciliation report. Existing work keeps its IDs; deferred improvements now have roadmap cards. The audit found and fixed the copied-exclusion defect rather than hiding it among future ideas.
- **Limitations / adversarial review:** The 110 pre-existing records retain their exact extracts, earlier comments, completion choices and discard decisions. Changed explanations retain their earlier wording in the thread. The old Markdown is preserved as a dated artifact. The dashboard and its JSON remain uncommitted, as requested.
- **I need from you:** Open the reconciliation table if you want to trace an earlier entry. B035 is parked until your go-ahead. The historical port file stays until the registration gaps are completed or explicitly deferred.

### P2: E2E coverage audit — user-action ↔ scenario matrix

- **Desk record:** [R015](http://127.0.0.1:4317/#R015)
- **Raised:** 2026-06-11
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** The coverage table now says whether a test clicks the real interface, calls internal logic, uses a simulated engine or runs a real local backup. Several actions now have interactive tests where the earlier table implied more coverage than existed.
- **Proof — open and look:**
  - [Results for the earlier roadmap items](../audit-dashboard/evidence/carryovers/report.html) — Read the checks for this item, see which ones use the real interface or engine, and review the remaining gaps.
- **Limitations / adversarial review:** This item is about making the coverage report accurate. Filling the remaining test gaps is tracked in R006. The latest actual test results determine pass or fail, not the color of an inventory row.
- **I need from you:** Review the table’s remaining partial and environment-dependent rows and choose any gaps you want prioritized next.

### P3: Delete BACKSPACE_PORT_DESIGN.md once port-plan gaps close

- **Desk record:** [R017](http://127.0.0.1:4317/#R017)
- **Raised:** 2026-06-12 (user request)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** The historical port document is stale, but your condition for deleting it is not yet met: official OneDrive and Google registrations still remain. Its useful requirements and their current records are now mapped in the unified roadmap. No implementation depends on this old document.
- **Proof — open and look:**
  - [10 October: answer and supporting detail](../audit-dashboard/evidence/feedback-20261010/port-plan.html) — The old port document still names unfinished work: official OneDrive/Google app registrations and native acceptance of Windows mounting. Most other items are implemented, including process ownership, Test restore and Windows update support. It also contains obsolete statements, so it should be treated only as historical context.
- **Limitations / adversarial review:** The old file is preserved. It is historical context, not a current status list. This item has no independent coding work waiting for the agent.
- **I need from you:** Complete or explicitly defer the account-registration decisions in B002 and B003. Existing Windows acceptance checks remain in their own issues. Once the port gaps are completed or deferred, your earlier permission already authorizes deleting the historical file; no new deletion approval is needed.

### P3: Auto-update channel — light variant

- **Desk record:** [R018](http://127.0.0.1:4317/#R018)
- **Raised:** 2026-06-12 (audit of BACKSPACE_PORT_DESIGN.md § 13)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** Your later Windows update request is now implemented under U001: periodic checks, release notes, skipping a version and choosing when to install the verified bundle. Other platforms keep the earlier notification and release-page link.
- **Proof — open and look:**
  - [Windows updates: screenshots, test results and remaining checks](../audit-dashboard/evidence/remaining-work-20261009/windows-updates.html) — See both Settings themes, what the automated tests really exercised, and the native acceptance steps.
- **Limitations / adversarial review:** 34 focused checks passed, plus a test using the real portable Windows bundle and native version command. No public release download, full GUI restart, SmartScreen prompt, or physically full disk was tested.
- **I need from you:** Review U001 and complete its pending native Windows acceptance checks. This original request remains open until you accept it.

## Planned work — not authorized for implementation

These records are the former deferred backlog, plus B035 at the user’s explicit direction. Moving them to an active issue requires a go-ahead; no new issue or copied discussion is needed.

| Record | Stage | Planned work | Before starting |
|---|---|---|---|
| [B035](http://127.0.0.1:4317/roadmap#B035) | Parked | Back up online-only files without making the originals local | Give the go-ahead when you want implementation to begin. The saved plan covers Dropbox source access, persistent revision tracking, original file dates, a hard temporary-space limit, complete snapshots, cancellation, and the default-on option to ignore online-only files. Later live testing will need your consent to a source account. |
| [P001](http://127.0.0.1:4317/roadmap#P001) | Later | Add a live S3 backup-and-restore test | Choose an S3-compatible service and provide a disposable test bucket before starting. |
| [P002](http://127.0.0.1:4317/roadmap#P002) | Later | Expand dark-theme screenshot coverage | Choose this for a future visual-coverage round. Current release screenshots must still pass the existing release review. |
| [P003](http://127.0.0.1:4317/roadmap#P003) | Later | Suggest exclusions for folders that change often | Decide whether to prioritize this feature before implementation. |
| [P004](http://127.0.0.1:4317/roadmap#P004) | Later | Unlock portable secrets with a master passphrase | Approve the security and recovery design before implementation. Losing the passphrase would make those saved secrets inaccessible. |
| [P005](http://127.0.0.1:4317/roadmap#P005) | Later | Copy a backup to another destination | Choose this for implementation when moving or duplicating repositories becomes a priority. |
| [P006](http://127.0.0.1:4317/roadmap#P006) | Later | Add Azure Blob as a backup destination | Approve this destination and choose a disposable service/account before implementation. |
| [P007](http://127.0.0.1:4317/roadmap#P007) | Later | Add Backblaze B2 native as a backup destination | Approve this destination and choose a disposable service/account before implementation. |
| [P008](http://127.0.0.1:4317/roadmap#P008) | Later | Add Storj as a backup destination | Approve this destination and choose a disposable service/account before implementation. |
| [P009](http://127.0.0.1:4317/roadmap#P009) | Later | Add WebDAV as a backup destination | Approve this destination and choose a disposable service/account before implementation. |
| [P010](http://127.0.0.1:4317/roadmap#P010) | Later | Add SFTP as a backup destination | Approve this destination and choose a disposable service/account before implementation. |
| [P011](http://127.0.0.1:4317/roadmap#P011) | Later | Translate Backspace into other languages | Choose the first languages before implementation. |
| [P012](http://127.0.0.1:4317/roadmap#P012) | Later | Revisit literal-path restores after an engine upgrade | When choosing a future restic upgrade, check whether literal matching has shipped and rerun bracket-name, unwanted-sibling and existing-file preservation tests before removing the workaround. |

## Known restore limitations

Bracket-name matching can fetch unwanted siblings into the operation’s private staging directory. Only requested files are promoted, so unrelated target files are preserved. Failed or cancelled output is not promoted. The current implementation uses Backrest’s native Restore API where it fits, and one owned bundled restic process for scattered selections or per-backup bandwidth limits. Direct restores appear in Backspace but not Backrest’s separate operation history.

P012 preserves the future literal-path matching investigation and its dated upstream references. Do not remove staging or the exact selection checks based on an upstream announcement alone. Validate the bundled engine and negative cases first. Windows metadata, ACLs, sparse allocation and junctions retain the documented limitations in [GOTCHAS.md](GOTCHAS.md).

## Reconciled historical claims

The proposed huge-test toggle is implemented and accepted in B021. Log retention already removes only expired owned logs; its file-backed regression passed again. Earlier hover, grace-control and restore-navigation gaps have later UI evidence. The repeated-custom-block finding remained real and was fixed in A032 during this reconciliation. The complete old audit remains in the migration report, rather than presenting June’s statements as current facts.
