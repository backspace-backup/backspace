# ROADMAP.md — work tracker

This file is the **source of truth** for in-flight work. Pair with
[GOTCHAS.md](GOTCHAS.md) for the upstream-driven limitations the code
already works around, and [AGENTS.md § The ROADMAP workflow](../AGENTS.md#the-roadmap-workflow-source-of-truth)
for the workflow rules an agent must follow before touching this file.

## Actively working

**Review queue — 10 October 2026:** Your latest comments have replies and new evidence in [the review desk](http://127.0.0.1:4317/). [Review summary](../audit-dashboard/evidence/feedback-20261010/report.html). The accepted entries have been removed from this active list, with the original records preserved in the desk and reconciliation report. Your redesign approval is recorded. The Word document has been reviewed.

**I need from you:** Review the new answers where the next turn is yours. Live cloud checks need account consent and provider configuration; email and Healthchecks checks need a test destination. The prepared Windows checks need an administrator session and B040 needs a real reboot. B024 now has measured CPU improvements and asks for the remaining native tray/display-scale check. R007 asks for approval of the tested restore architecture; A011 now has real progress and safety results to review. No input is needed for the agent’s remaining cloud-source implementation work. The historical port plan remains until the condition on your deletion permission is met.

**Older items need attention:** Any remaining June/July entry awaiting validation is older than a week; accept its evidence or describe the remaining problem. These entries have not been silently removed.

### P0: E2E revamp pass — all actionable coverage-matrix rows covered — Awaiting validation

- **Raised:** 2026-06-12
- **Type:** test coverage
- **Status:** In progress
- **Context:** R006: The coverage audit distinguishes real UI, real local engine, injected signals and OS/account boundaries. Actual editor, animated-hover and Browse checks pass. Published backup, maintenance and migration workflows and the generated scheduled action also pass isolated process and byte-comparison checks.
- **Current result:** The coverage audit distinguishes real UI, real local engine, injected signals and OS/account boundaries. Actual editor, animated-hover and Browse checks pass. Published backup, maintenance and migration workflows and the generated scheduled action also pass isolated process and byte-comparison checks.
- **Remaining:** Review the revised matrix and linked process evidence. Remaining acceptance needs real accounts or machine-state changes; every possible installation and workflow variation has not been exercised.
- **Proof — open and look:**
  - [Scheduler, guard and parser regression results](../audit-dashboard/evidence/infrastructure-tests/runtime-coverage-report.html) — 57 passed, 0 failed, 0 skipped: 44 guard/parser plus13 action regressions
  - [Final packaged app: saved-credential validation](../audit-dashboard/evidence/packaged-vault-final/report.html) — 12 public CLI launches and 145 assertions passed, plus nine loader/argument checks. Each backup, prune and check entry point rejects the same unreadable ciphertext on two launches without changing its bytes or starting an operation. Real current-user DPAPI empty-map and absent-vault negatives proceed to normal missing-backup handling. Synthetic disposable vaults; no personal credential recovery, live cloud account, registered task or reboot is claimed.
- **Limitations / adversarial review:** The final broad suite passed 998 checks with 2 credential skips; 39 local engine cases and the 20,000-file / 2 GiB round trip passed separately. Final packaged backup, maintenance, migration, scheduled-action, credential-preflight and native-loader checks also passed. Overlapping regression batches are not unique test counts. Actual OS battery/sleep/network transitions, reboot, elevated consent and live provider accounts remain environment acceptance.
- **I need from you:** R006: Review the revised matrix and linked process evidence. Remaining acceptance needs real accounts or machine-state changes; every possible installation and workflow variation has not been exercised.

### P0: Investigate "why do we deviate from Backrest's native restore RPC?"

- **Raised:** 2026-06-12 (user feedback on the folder-collapse review)
- **Type:** architecture review
- **Status:** Awaiting validation
- **Context:** R007: Explain why some restore requests cannot use Backrest’s dedicated Restore API, compare the alternatives with real safety checks, and present the resulting architectural tradeoff for approval.
- **Current result:** One selected file or folder uses Backrest’s dedicated Restore API when possible. Several selections, a per-backup speed limit, or significant whitespace use one bundled restic process managed by Backspace. Both paths show progress, stage output safely, and preserve unrelated destination files. Direct restores appear in Backspace but do not appear in Backrest’s separate operation history.
- **Remaining:** Approve the tested split between Backrest’s Restore API and a direct restic process for requests that API cannot express. My recommendation is to keep it: it provides live progress and one operation for large selections. The cost is a small process runner to maintain and no Backrest web-history entry for those direct restores.
- **Proof — open and look:**
  - 🖼️ [Selected files restoring with a speed limit](../audit-dashboard/evidence/agent-work-20261010/streaming-progress.png) — A real intermediate percentage before the two selected files finish.
  - 🖼️ [Completed and cancelled results](../audit-dashboard/evidence/agent-work-20261010/streaming-complete-cancel.png) — The successful restore shows 2 of 2 files and a full bar; the cancelled restore shows zero recovered files.

  - [Restore architecture: working behavior and its tradeoff](../audit-dashboard/evidence/agent-work-20261010/streaming-restore.html) — One selected file or folder uses Backrest’s dedicated Restore API when possible. Several selections, a per-backup speed limit, or significant whitespace use one bundled restic process managed by Backspace. Both paths show progress, stage output safely, and preserve unrelated destination files. Direct restores appear in Backspace but do not appear in Backrest’s separate operation history.

  - [Real native restore results](../audit-dashboard/evidence/agent-work-20261010/native-restore.html) — Backspace now uses Backrest’s dedicated Restore API for one selected file or folder when no per-backup bandwidth limit is set. It reports live progress and still restores into a temporary folder before moving only the requested files into place. Scattered selections and bandwidth-limited restores keep the existing command path.
  - 🖼️ [Actual restore in progress](../audit-dashboard/evidence/agent-work-20261010/01-real-restore-in-progress.png) — The Restores page shows a real intermediate percentage before the operation finishes.

  - [10 October: answer and evidence](../audit-dashboard/evidence/feedback-20261010/restore-architecture.html) — Backspace still uses Backrest to run restores, but supplies a restic command through its general command API. This supports several selected files or folders in one operation and keeps the output in a temporary folder until it is safe to move. Backrest’s dedicated Restore API already supports subfolders and structured progress; our earlier explanation understated what it can do.
  - [R007 · Carryover evidence and test scope](../audit-dashboard/evidence/carryovers/report.html) — The report lists the passing overmatch/partial-error/cancellation negative controls and the unresolved architecture choice.
  - [Detailed restore safety report](../audit-dashboard/evidence/restore-repairs/report.html) — Open restore staging, terminal truth and cancellation evidence with its integration limits.
- **Limitations / adversarial review:** Real bundled Backrest and restic used disposable encrypted local storage. The Stop restore check clicks the real control in headless Avalonia. Live cloud restores, a multi-day run, and Windows refusing to terminate a process remain unverified. The separate buffer tests use synthetic messages; they are not a long-running restore.
- **Follow-up received 2026-10-10:**
  > Please be clearer about the current status of things (what are we missing from backrest, upsides/downsides of current implementation) and what you suggest would be best.

- **I need from you:** Approve the tested split between Backrest’s Restore API and a direct restic process for requests that API cannot express. My recommendation is to keep it: it provides live progress and one operation for large selections. The cost is a small process runner to maintain and no Backrest web-history entry for those direct restores.

### P0: Restore enumerates every leaf when a folder is selected

- **Raised:** 2026-06-11
- **Type:** bug + design
- **Status:** Awaiting validation
- **Context:** R008: Fully selected subtrees collapse to a minimal include cover. S47 checks whole-folder collapse and the negative partial-selection case; E35 sends collapsed patterns through the real restore engine and verifies bytes. E34 separately uses an include file for large selections.
- **Current result:** A new real-engine test restored 2,397 files from Photos [2026] using one folder pattern. Every file matched its original contents. A similarly named folder was excluded and an existing destination file was preserved. A second restore selected two files and recovered exactly those two.
- **Remaining:** Open the new folder-restore report. The result to review is simple: 2,397 requested files recovered correctly, zero unwanted files, and the existing destination file unchanged. The report also explains why the individual filename safety list is still retained.
- **Proof — open and look:**

  - [10 October: answer and evidence](../audit-dashboard/evidence/feedback-20261010/folder-restore.html) — A new real-engine test restored 2,397 files from Photos [2026] using one folder pattern. Every file matched its original contents. A similarly named folder was excluded and an existing destination file was preserved. A second restore selected two files and recovered exactly those two.
  - [R008 · Carryover evidence and test scope](../audit-dashboard/evidence/carryovers/report.html) — S47 proves full folders collapse and partial leaves stay explicit; E35 and E34 passed with real local backup/restore and full regular-file byte verification.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** Real bundled Backrest and restic, encrypted local repository and actual disk output. The test exercises the production selection and restore services; it does not click the restore wizard’s folder checkbox.
- **Follow-up received 2026-10-10:**
  > Are you sure there wasn't a good reason for listing files separately (like the issue with brackets)?
  > 
  > What needs to happen next
  > Open the folder-selection test results and check that they address your example with 2,397 separate file arguments.
  > => This isn't clear, what am I supposed to do? The page http://127.0.0.1:4317/proof/repair-verification.html doesn't show a proof of what you did.

- **I need from you:** Open the new folder-restore report. The result to review is simple: 2,397 requested files recovered correctly, zero unwanted files, and the existing destination file unchanged. The report also explains why the individual filename safety list is still retained.

### P1: Loading Settings must not rewrite saved preferences

- **Raised:** 2026-10-10
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** The framework-upgrade checks exposed a settings refresh that writes values back while loading them. Queued refreshes can overwrite a newer logging choice, and simply opening Settings can change a saved Debug logging level to Verbose.
- **Current result:** Opening and refreshing Settings no longer writes preferences back. Deliberate edits still save; queued refreshes are ignored after disposal.
- **Proof — open and look:** [Settings before-and-after results](../audit-dashboard/evidence/agent-work-20261010/settings-refresh.html) — The test first reproduced four unwanted writes, then passed with zero on opening, correct deliberate saves and safe disposal.
- **Limitations / adversarial review:** The test exercises real view-model/configuration behavior, including negative cases, through bound properties rather than native clicks. Existing Settings interaction scenarios also pass.
- **I need from you:** Review A031 and its before-and-after report.

### P1: Stop engine startup if local authentication cannot be enabled

- **Raised:** 2026-10-10
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** While comparing the supplied Word document with the implementation, the audit found that failed initial authentication could leave the bundled Backrest server running without authentication. A concurrent caller could also see the engine as ready before authentication completed.
- **Remaining:** Open the authentication report. It shows the real daemon rejecting anonymous access, shutting down after a deliberately wrong saved password, preserving its configuration, and starting successfully after the test password is corrected.
- **Current result:** The document review uncovered two startup defects. Authentication setup errors could be ignored, and an already configured engine could time out because the readiness check expected anonymous access. Startup now requires a successful login, treats a sign-in challenge as readiness, and only then makes the engine available to other operations.

- **Limitations / adversarial review:** The real test uses disposable local engine state. Controlled HTTP failures separately cover the first-time hash, configuration and login steps. It does not claim that the brief initial authentication setup window has been eliminated or that every local security boundary has been audited.

- **Proof — open and look:** [Authentication results](../audit-dashboard/evidence/feedback-20261010/authentication.html) — The real engine rejects anonymous access, stops on failed login and restarts after correction.

- **I need from you:** Open the authentication report. It shows the real daemon rejecting anonymous access, shutting down after a deliberately wrong saved password, preserving its configuration, and starting successfully after the test password is corrected.

### P1: Add graceful Windows updates from GitHub releases

- **Raised:** 2026-10-09
- **Type:** feature
- **Status:** In progress
- **Context:** Windows updates should be as easy to use as Perfect-Bluetooth-MIDI-For-Windows. Preserve portable settings and credentials, avoid interrupting backups or restores, and provide recovery if an update fails.
- **Current result:** Settings offers monthly, weekly, daily or manual-only checks, release notes, skipping a version, and an explicit download-and-restart action. The complete Windows bundle is checked before installation. Active work and other Backspace processes prevent replacement. Failed replacements roll back; the previous version and a recovery launcher remain available. Older completed recovery bundles are cleaned up after the next successful replacement.
- **Proof — open and look:**
  - 🖼️ **[Update available in Settings](../audit-dashboard/evidence/remaining-work-20261009/update-light.png)** — Current version, check frequency, release notes and explicit install/skip choices; the displayed release is simulated.
  - 🖼️ **[Skipped update in dark mode](../audit-dashboard/evidence/remaining-work-20261009/update-dark.png)** — Skipping the notification leaves manual installation available.
  - [Windows updater evidence](../audit-dashboard/evidence/remaining-work-20261009/windows-updates.html) — Safety checks and a real portable bundle test, with exact limits and native acceptance steps.
- **Limitations / adversarial review:** Tests reject unsafe paths, altered downloads and staging files, refuse installation while another real process holds a lease, preserve active operations, and roll back failures after each replacement step. The real bundle was installed in a disposable Unicode path, launched with its native version command, and restored byte-for-byte. GitHub HTTP was simulated; full GUI restart, Windows security prompts and a physically full disk remain unverified. A successful process launch alone does not establish a healthy new GUI.
- **Automated result:** Both the local and hosted full suites passed 1,059 tests, with two credential-dependent skips. The hosted candidate also passed its actual Windows bundle installation and rollback test. The release rehearsal stopped at human approval as required; see the release report below.
- **Remaining:** Perform the native acceptance checks listed in the report once approved packaged versions are available. No public release has been created.
- **I need from you:** U001: Use the pending Windows test instructions in the Review desk to check GUI restart, recovery, active scheduled work and Windows prompts. These remain unverified; automated command-line launch is not GUI acceptance.

### P1: Explain unsupported Dropbox placeholders without claiming they were backed up

- **Raised:** 2026-10-05
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** The supplied 124,333,386-byte Dropbox file uses an Offline reparse point that bundled Restic 0.18.1 reports as an unsupported irregular file. Backups with cloud-file exclusion both on and off omitted it and returned Warning. The file remained online-only with zero allocated bytes. Earlier source documentation asserted tested download behavior and a snapshot marker without evidence; correct that claim and expose the limitation plainly.
- **Current result:** The tested Dropbox placeholder stayed online-only, but its contents were absent from the backup. The app now reports the unsupported file and warning honestly. The separate feature for backing up cloud contents without changing local availability remains active under B035.
- **Remaining:** Review the warning fix here. The requested online-only backup feature is tracked separately under B035, including separate temporary downloads, metadata checks, a space limit and ignore-by-default behavior.
- **Proof — open and look:**

  - [10 October: answer and evidence](../audit-dashboard/evidence/feedback-20261010/cloud-source.html) — The tested Dropbox placeholder stayed online-only, but its contents were absent from the backup. The app now reports the unsupported file and warning honestly. The separate feature for backing up cloud contents without changing local availability remains active under B035.
  - [Actual Dropbox test: no download and no backed-up contents](../audit-dashboard/evidence/cloud-placeholder-20261005/report.html) — See the file measurements, snapshot checks and original engine error. The supplied file remained online-only.
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
- **Limitations / adversarial review:** Actual backup support for this online-only file remains unresolved. B035 retains the broader testing work.

- **Follow-up received 2026-10-10:**
  > Make sure this is documented in our roadmap (per the other discussion we had about adding support for such online only files without causing them to be downloaded more than momentarily)

- **I need from you:** Review the warning fix here. The requested online-only backup feature is tracked separately under B035, including separate temporary downloads, metadata checks, a space limit and ignore-by-default behavior.

### P1: Require current screenshots and documentation before a GitHub release

- **Raised:** 2026-10-05
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** The user wants a systematic check of screenshots, README and documentation before each GitHub release, and asks whether AGENTS.md is sufficient. Add a review issue and recommend enforceable release checks with a clear distinction between automated checks and human visual/content review.
- **Current result:** Release approval is a human judgment supported by automated evidence. The workflow produces tests, current screenshots and the exact candidate, then waits for a required reviewer. The release guide and GitHub summary now explicitly ask the reviewer to compare README, guides, website images and captions with that candidate and record the reviewed revision.
- **Remaining:** Review the clarified process. The agent prepares and corrects the documentation; the human makes the final accuracy judgment before publishing. The existing gate rehearsal is evidence that publishing waits, not proof that a person read every document.
- **Proof — open and look:**

  - [10 October: answer and evidence](../audit-dashboard/evidence/feedback-20261010/release-review.html) — Release approval is a human judgment supported by automated evidence. The workflow produces tests, current screenshots and the exact candidate, then waits for a required reviewer. The release guide and GitHub summary now explicitly ask the reviewer to compare README, guides, website images and captions with that candidate and record the reviewed revision.
  - 🖼️ [Successful checks waiting for human approval](../audit-dashboard/evidence/remaining-work-20261009/release-approval-waiting.png) — Green Windows checks lead to a waiting human-review job; publishing has not started.
  - [Release rehearsal results](../audit-dashboard/evidence/remaining-work-20261009/release-gate.html) — Test records, full file comparison, saved settings and the reason the overall run now says cancelled.
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
- **Limitations / adversarial review:** The approval gate was exercised in the earlier GitHub rehearsal. The new checklist wording has been checked locally, not by publishing another release. Repository administrators can still change permissions or publish through other routes.

- **Clean-run findings — 2026-10-09:** Earlier hosted runs exposed missing first-build engine copies, a 24-hour-only assertion, a hosted metered-network signal cancelling unrelated tests, and a version query that left Backrest's Windows tray running. These were repaired before the successful rehearsal. Production network detection and its dedicated cancellation tests remain enabled.

- **Packaging follow-up — 2026-10-09:** The fallback warning executable had the same first-build resource-list problem. Include it after it is generated, fail ordinary builds if generation produces no file, and make the release suite verify the actual embedded executable. Development builds can still explicitly skip it.

- **Engine-version finding — 2026-10-09:** The third hosted run passed the interface suite but failed restore with newly downloaded Backrest 1.14.1. Its request formats differ from the tested 1.13.0 engine. Both downloaders now read explicit version selections: Backrest 1.13.0, Restic 0.18.1 and rclone 1.74.1. Existing local binaries stay untouched. This prevents an unreviewed automatic upgrade; it does not claim compatibility with Backrest 1.14.1 or cryptographic verification of every download.

- **Follow-up — 2026-10-09:** Complete all release-gate implementation and available repository configuration without waiting for the user. Identify a concrete access or approval blocker before leaving work open.
- **Follow-up received 2026-10-10:**
  > I don't think you answered my question: We need to systematically verify screenshots and the docs and readme are still up to date before pushing a new release on github. What's the best way to enforce this? Just updating agents.md accordingly?
  > AFAIK this can't be done with a suite test (no LLM call) as it require semantic understanding.

- **I need from you:** Review the clarified process. The agent prepares and corrects the documentation; the human makes the final accuracy judgment before publishing. The existing gate rehearsal is evidence that publishing waits, not proof that a person read every document.

### P1: Restore target radios ping-pong forever (test-suite hang)

- **Raised:** 2026-07-04 (found by us: the visual suite sat 40+ minutes
- **Type:** bug (latent UI infinite loop)
- **Status:** Awaiting validation
- **Context:** R001: The target choices render separately and the interaction regression clicks both radio buttons, verifies the bound target changes in each direction, and completes without the former layout loop.
- **Current result:** The target choices render separately and the interaction regression clicks both radio buttons, verifies the bound target changes in each direction, and completes without the former layout loop.
- **Remaining:** Inspect the two target states and accept if the interaction is now correct.
- **Proof — open and look:**
  - 🖼️ [Different-folder target selected](../audit-dashboard/evidence/carryovers/restore-target-other-folder.png) — The lower radio is selected; the destination field and Browse button are visible, and the original-source overwrite warning remains above.
  - [R001 · Carryover evidence and test scope](../audit-dashboard/evidence/carryovers/report.html) — The actual radio-click regression and both rendered target states passed in the recorded run.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** S23 renders seeded states; the separate interaction test supplies the actual pointer-click evidence. This is headless Avalonia, not a packaged-process test.
- **I need from you:** R001: Inspect the two target states and accept if the interaction is now correct.

### P1: Adding a cloud destination erases other destinations from shared rclone.conf

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** In progress
- **Context:** A001: Production cloud operations materialize separate files per destination, legacy shared-file writes retain unrelated sections, and newer rclone-refreshed tokens are imported into the encrypted vault. Later interactive sign-in wins over stale disk credentials.
- **Current result:** Production cloud operations materialize separate files per destination, legacy shared-file writes retain unrelated sections, and newer rclone-refreshed tokens are imported into the encrypted vault. Later interactive sign-in wins over stale disk credentials.
- **Remaining:** Review the isolated-destination and concurrent-file evidence. Live account acceptance is tracked separately in B001–B003.
- **Proof — open and look:**
  - [Concurrent destinations and token persistence — passing file-backed tests](../audit-dashboard/evidence/cloud-repairs/report.html) — Two simultaneous writers preserve A and B; a simulated on-disk refresh survives rebuilding, while a new account sign-in defeats the older file.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** No real cloud sign-in, expiry/refresh or restart was performed. Official OneDrive and Google registration IDs are still empty and require the maintainer. Isolation prevents A/B writers overwriting one another; concurrent rclone processes for the same destination still use rclone's own token-write behavior and need live-account coverage.
- **I need from you:** A001: Review the isolated-destination and concurrent-file evidence. Live account acceptance is tracked separately in B001–B003.

### P1: OneDrive remote is unusable even after supplying an OAuth app ID

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Open
- **Context:** A002: The OneDrive editor discovers the signed-in user's actual default drive ID/type before marking connected; engine use discovers missing legacy identity. Runtime uses the documented localhost callback host/path for Microsoft native registration. The real editor command is covered through local callback, PKCE exchange, discovery and consent refusal with simulated provider HTTP responses.
- **Current result:** The OneDrive editor discovers the signed-in user's actual default drive ID/type before marking connected; engine use discovers missing legacy identity. Runtime uses the documented localhost callback host/path for Microsoft native registration. The real editor command is covered through local callback, PKCE exchange, discovery and consent refusal with simulated provider HTTP responses.
- **Remaining:** Supply or register the official OneDrive app ID and authorize a disposable Microsoft account for actual sign-in, drive discovery, refresh and backup/restore acceptance.
- **Proof — open and look:**
  - 🖼️ [OneDrive editor after simulated successful sign-in](../audit-dashboard/evidence/cloud-repairs/onedrive-connected.png) — Connected appears after the editor command, real local callback and fake token/drive responses; this is not a real account proof.
  - 🖼️ [Consent refusal remains visible](../audit-dashboard/evidence/cloud-repairs/onedrive-denied.png) — Denying consent leaves a red refusal message, no stuck busy state, and no token exchange.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** Default personal/business drive only; no SharePoint-site or multi-drive selector. Real browser consent, account permissions and official app registration are unverified. Legacy expired tokens may require signing in again before drive discovery.
- **I need from you:** A002: Supply or register the official OneDrive app ID and authorize a disposable Microsoft account for actual sign-in, drive discovery, refresh and backup/restore acceptance.

### P1: Restore cancellation does not cancel the queued daemon operation

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** In progress
- **Context:** A010: Synchronous Backrest requests are monitored while pending; cancellation explicitly stops the owned daemon operation and awaits terminal acknowledgement. No five-minute operation deadline remains. Quiet JSON removes duration-based log growth; progress is indeterminate until authoritative counts arrive.
- **Current result:** Synchronous Backrest requests are monitored while pending; cancellation explicitly stops the owned daemon operation and awaits terminal acknowledgement. No five-minute operation deadline remains. Quiet JSON removes duration-based log growth; progress is indeterminate until authoritative counts arrive.
- **Remaining:** Review the linked evidence and accept the item, or describe what still needs adjustment.
- **Proof — open and look:**
  - [Restore safety evidence](../audit-dashboard/evidence/restore-repairs/report.html) — Open exact safety assertions, real E11 counts and session screenshots.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** Tests use disposable data. Review the linked report scope before accepting the item.
- **I need from you:** A010: Review the linked evidence and accept the item, or describe what still needs adjustment.

### P1: Every restore has a hard five-minute wait limit

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** A011: Remove the hard five-minute restore cutoff, keep progress visible, and ensure cancellation cannot release repository ownership while the engine is still running.
- **Current result:** Restores now report live progress for several selected files and for backups with a speed limit, as well as single-file and single-folder restores. Progress messages are discarded after display, so they do not fill an ever-growing log. The completed bar shows 100% even when unwanted filename matches were downloaded temporarily and then discarded.
- **Remaining:** Review the two screenshots and the test results. The live percentage is implemented and verified with real local backup engines. Live cloud acceptance remains under B034; no multi-day restore was run.
- **Proof — open and look:**
  - 🖼️ [Selected files restoring with a speed limit](../audit-dashboard/evidence/agent-work-20261010/streaming-progress.png) — A real intermediate percentage before the two selected files finish.
  - 🖼️ [Completed and cancelled results](../audit-dashboard/evidence/agent-work-20261010/streaming-complete-cancel.png) — The successful restore shows 2 of 2 files and a full bar; the cancelled restore shows zero recovered files.

  - [Live restore progress, cancellation and damaged-backup results](../audit-dashboard/evidence/agent-work-20261010/streaming-restore.html) — Restores now report live progress for several selected files and for backups with a speed limit, as well as single-file and single-folder restores. Progress messages are discarded after display, so they do not fill an ever-growing log. The completed bar shows 100% even when unwanted filename matches were downloaded temporarily and then discarded.

  - [Real native restore results](../audit-dashboard/evidence/agent-work-20261010/native-restore.html) — The five-minute cutoff remains removed. Single-file and single-folder restores now show real live progress from Backrest’s dedicated restore API. A new test captures intermediate progress during an actual 128 MiB restore, checks all recovered bytes, and cancels a second restore without replacing an existing file.
  - 🖼️ [Actual restore in progress](../audit-dashboard/evidence/agent-work-20261010/01-real-restore-in-progress.png) — The Restores page shows a real intermediate percentage before the operation finishes.

  - [10 October: answer and evidence](../audit-dashboard/evidence/feedback-20261010/restore-architecture.html) — The five-minute cutoff is removed, but a live percentage is still missing from the production restore path. The interface can display a percentage when it receives progress; the current command suppresses those updates to avoid overflowing Backrest’s command log during long restores. It usually shows an indeterminate bar until the final result.
  - [Restore safety evidence](../audit-dashboard/evidence/restore-repairs/report.html) — Open exact safety assertions, real E11 counts and session screenshots.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** Real bundled Backrest and restic used disposable encrypted local storage. The Stop restore check clicks the real control in headless Avalonia. Live cloud restores, a multi-day run, and Windows refusing to terminate a process remain unverified. The separate buffer tests use synthetic messages; they are not a long-running restore.
- **Follow-up received 2026-10-10:**
  > But we need to show a percentage (we know total number of files or size and progress so we can compute a ratio, no?), don't we show anything?

- **I need from you:** Review the two screenshots and the test results. The live percentage is implemented and verified with real local backup engines. Live cloud acceptance remains under B034; no multi-day restore was run.

### P1: VSS and elevation on Windows

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** B004: - Make sure VSS always enabled on Windows (require app elevation if needed at launch/auto elevate)
- **Current result:** Actual Restic backup cells now route through VssElevationCoordinator when policy requires elevation. Backup and standalone prune/check work retain maintenance read ownership for their full duration.
- **Remaining:** Review the helper routing proof, then authorize a disposable locked-file backup through the real UAC/VSS flow for native acceptance.
- **Proof — open and look:**
  - [Final infrastructure checks](../audit-dashboard/evidence/infrastructure-tests/final-report.html) — 50 selected tests passed; broad run933passed2credentialskips0failed
  - [Infrastructure repair evidence](../audit-dashboard/evidence/infrastructure-tests/report.html) — 86 passed, 0 failed, 0 skipped
- **Limitations / adversarial review:** Default VSS and isolated elevated-helper ownership are tested. Actual UAC consent, locked-file snapshot inclusion and real elevated cancellation remain untested.
- **I need from you:** B004: Review the helper routing proof, then authorize a disposable locked-file backup through the real UAC/VSS flow for native acceptance.

### P1: Test restore against the original bytes

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** B006: - Test restore feature that actually compares restored files with known local files to see if bytes match (unless you think this is useless, if there is already some hashing happening, integrity can always be reliably verified right? Is it realistic to expect any kind of other data corruption or missing files from the backup compared to the local files?).
- **Clarification received 2026-10-10:** This concerns the app's Test restore button, not only automated tests of ordinary restores. Trace that exact action, check its underlying restore and comparison behavior, and correct the review summary and proof to cover the feature itself.
- **Current result:** The Test restore button uses the same RestoreEngine as ordinary restores. It restores all snapshot files from every source and destination in batches, compares unchanged originals and reports failures or files it cannot compare. Corrected its stale “10 random files” tooltip. Hovering over the result now shows restored/compared counts or the failure summary.
- **Remaining:** Review the actual button-flow screenshots and the feature's stated limits.
- **Proof — open and look:**
  - 🖼️ [Test restore succeeds and reports all 31 matches](../audit-dashboard/evidence/issue-context-20261010/test-restore-success.png) — Actual control click in headless Avalonia with real Backrest/restic, two source folders and an empty file. The tooltip shows 31 restored and 31 byte-identical.
  - 🖼️ [A same-size mismatch makes the button report failure](../audit-dashboard/evidence/issue-context-20261010/test-restore-mismatch.png) — A second click detects changed bytes in the second source even though size and modification time were preserved; the tooltip reports one of 31 failed.
  - [Test restore feature, results and limits](../audit-dashboard/evidence/issue-context-20261010/test-restore.html) — All 17 focused checks passed, no skips. The button saves separate successful and failed Test restore runs with logs.
- **Limitations / adversarial review:** The button scenario uses real engine processes and disposable encrypted local storage; the 16 boundary checks use controlled inputs or the in-memory engine. This does not establish native Windows interactions, live cloud, metadata/permissions recovery, or historical bytes of originals that changed or disappeared. The negative case changes a disposable original while retaining its size/date; it does not corrupt repository packs. Linked reports distinguish the feature from ordinary restore testing.

- **Review comments received 2026-10-05 (B006):**
  > - Restoring a selected file whose name contains brackets leaves an unrelated existing file unchanged. It also leaves a similarly named file unchanged when a wildcard would otherwise match it.
  > => WTH? How is this a check designed to catch unsafe results? This is a horrible bug!
  >
  > - In /proof/restore-repairs/report.html, the lsit of failures includes S3 read timed out and connection reset which sounds like retryable errors. What is the retry strategy?

- **I need from you:** B006: Review the two Test restore button screenshots and confirm that the feature and its explanation address your original request.

### P1: Healthchecks start, failure and completion pings

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** In progress
- **Context:** B007: - Healthchecks.io integration (restic uses hooks for this, is it equivalent? to fire immediately in case of failure, and upon completion for healthy ping).
- **Current result:** Backrest provides native Healthchecks hooks. Backspace currently reads Backrest operation results but sends its own Healthchecks signals for the whole Backspace backup, which can contain several sources, destinations and retries.
- **Remaining:** Review the explanation of the remaining wrapper. Actual delivery to your Healthchecks service still needs a real check URL.
- **Proof — open and look:**
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
  - [Cloud settings, notifications and skipped-backup tests](../audit-dashboard/evidence/cloud-repairs/report.html) — The report explains the cloud-connection and notification changes, shows their local tests, and identifies the checks that still need real accounts or devices.
  - [Recorded test results and remaining gaps](../audit-dashboard/evidence/repair-verification.html) — Read the pass and fail results, open the original test records if needed, and see what each run did and did not test.
- **Limitations / adversarial review:** The current app does not configure Backrest Healthchecks hooks. Local tests exercise request contents and timing using a test server; they do not prove delivery to your service.

- **Review comments received 2026-10-05 (B007):**
  > Why don't you answer my questions: restic uses hooks for this, is it equivalent?
  > Are we using Restic hooks? We should not reinvent the wheel if they already provide good plumbing to detect failed or successful backups. Also this would make it possible in the future to support more success/failure notification channels than only healthchecks.io.

- **Clarification:** In these comments, “Restic” means the combined Restic + Backrest stack. Prefer existing engine capabilities where they cover the required behavior.

- **I need from you:** B007: Review the explanation of the remaining wrapper. Actual delivery to your Healthchecks service still needs a real check URL.

### P1: Explain quitting during active operations

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** In progress
- **Context:** B023: Also it is misleading to say "Backspace keeps running in the background so scheduled backups still fire"! App doesn't need to run in the background for this! User should be reassured about this. Only useful to keep to reopen fast. How are things handled if a backup or any other operation is running and user wants to quit? I think we should still push to keep app running in the systray if you can confirm CPU impact will be zero when no operation is running. This can be reassuring for people to see Backspace if there. But we should reassure them even if Backspace is quit, it'll stop existing operations but scheduled operations will be triggered as usual. Find the best way to frame this.
- **Current result:** Close-to-tray saves immediately. The tray now shows whether actual Windows scheduled tasks match the enabled backups, with separate paused, off, attention and unverified states. Checks refresh after configuration or task changes, when the app or menu opens, and when a snooze expires. The check time is shown; drives and sign-in are checked when a backup runs. Existing Quit and Force quit behavior is retained.
- **Remaining:** The schedule indicator is implemented. Native Windows tray interaction and forced exit with a live engine still require a desktop check; native computer control is unavailable in this session.
- **Proof — open and look:**
  - 🖼️ [Actual tray badge artwork](../audit-dashboard/evidence/remaining-work-20261009/tray-status-icons.png) — Ready, paused, off, attention and unverified use distinct labels and meaningful badge shapes. Production renderer, shown in a headless gallery rather than the Windows notification area.
  - [Tray checks and pending-test panels](../audit-dashboard/evidence/remaining-work-20261009/report.html) — Full suite: 1,023 passed, two credential skips, no failures. Negative cases reject altered task accounts, triggers, times and actions; native tray behavior remains unverified.
  - 🖼️ [The tray preference survives an immediate close](../audit-dashboard/evidence/review-feedback/01-tray-setting-saved-immediately.png) — The test enabled close-to-tray, immediately closed the window and reopened the same window without exiting.
  - 🖼️ [Force quit explains the consequences](../audit-dashboard/evidence/review-feedback/02-force-quit-warning.png) — The confirmation explains interrupted work and offers Keep running. The test clicks Keep running.
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
  - 🖼️ [Quit explains the effect on active work](../audit-dashboard/evidence/review-ui/05-tray-quit-protects-running-restore.png) — The dialog offers Keep running. The accompanying test chooses it and confirms that the restore remains active.
- **Limitations / adversarial review:** The actual settings checkbox and close handler were tested, as were the force-quit warning and its cancellation. A real engine was not forcibly terminated in a native desktop test.

- **Review comments received 2026-10-05 (B023):**
  > I tried to enable the systray option in settings, and when i exited the app, it did not stay in the systray!
  > Why didn't you bother to answer me on this: "I think we should still push to keep app running in the systray if you can confirm CPU impact will be zero when no operation is running."
  > Can we achieve zero CPU usage when the app is idle (no active backuping)?
  >
  > Systray icon should make it possible to easily open or completely exit Backspace. If a backup or restore operation is ongoing, it should still be possible to forcibly and immediately quit, just warn the user accordingly.
  >
  > If we changed the app to only do scheduled runs when the app is loaded in systray, what would the downsides be? Today, can the backups run if a user is not logged in (e.g. to backup a whole computer, with the backup configured on the admin account, while other accounts are more regularly used)?
  > I'm wondering if we should not aim to always have a systray icon showing up so a user can easily forcibly quit, while the app should still be able to do backups per schedule even if user is not logged in (so best of both worlds).

  > Having systray icon reflect whether app is "armed" or not seems standard practice for this kind of app, isn't it?

- **Follow-up — 2026-10-09:** Implement the proposed truthful tray scheduling indication and complete all available native checks autonomously. Do not leave implementable work for the user.
- **I need from you:** B023: No action yet; the agent still needs to complete the remaining work.

### P1: Tolerable destination outages are skipped

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** In progress
- **Context:** B025: - If a destination is unreachable AND it is because of a known tolerable error (e.g. we're offline or not at home or drive is not present), always Skip with warning. Never consider as Failed.
- **Current result:** Expected availability problems remain Skipped. When the grace period expires, the app sends a warning that says the backup was skipped and explains why. Other errors remain failures; some are retried first.
- **Remaining:** Compare the warning and failure screenshots. The report lists every current offline-error match and explains which errors are retried before failure.
- **Proof — open and look:**
  - 🖼️ [A skipped backup produces a warning](../audit-dashboard/evidence/review-feedback/03-skipped-warning-is-not-a-failure.png) — The actual alert says skipped and names the disconnected drive. It does not label this as a failed backup.
  - 🖼️ [Failure notification before clicking](../audit-dashboard/evidence/review-feedback/01-failure-popup-before-click.png) — The actual alert has a readable title, the error, and an Open run details button.
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
  - [Cloud settings, notifications and skipped-backup tests](../audit-dashboard/evidence/cloud-repairs/report.html) — The report explains the cloud-connection and notification changes, shows their local tests, and identifies the checks that still need real accounts or devices.
- **Limitations / adversarial review:** The popup is the actual production view, but the outage is prepared test data. Router, VPN, NAS and physical-drive disconnections remain separate live checks.

- **Review comments received 2026-10-05 (B025):**
  > What is the exhaustive list of known tolerable errors? And list of other errors (which would cause immediate marking as Failed)
  >
  > skipped with warning notifications would be fired as normal. These are not as bad as real failure so should clearly be indicated as such
  > => Please provide proof of how this will look.

- **I need from you:** B025: Compare the warning and failure screenshots. The report lists every current offline-error match and explains which errors are retried before failure.

### P1: Unexpected destination errors remain failures

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** In progress
- **Context:** B026: - If a destination is unreachable or failed and it is another error, mark as Failed.
- **Current result:** Skip grace suppresses human alerts; expiration emits warning severity without promoting the stored run to Failed. Reset suppresses subsequent skip alerts. Share access probes preserve permission/missing-path failures and distinguish known outages. Relocation guidance is conditional, requires previous success and a local destination, and excludes deliberate cancellation/metered stops. Easy mode now always exposes the selector and Reset on its schedule step, including before any skipped run and after expiry. Its old Failed-escalation wording is corrected. The real wizard Save/reopen scenario confirms the 14-day choice/reset persist without faking a successful run.
- **Remaining:** Review the report and listed limitations; accept the issue if this proof matches your requirement. No credentials or external messages were used.
- **Proof — open and look:**
  - 🖼️ [Easy grace controls before a holiday](../audit-dashboard/evidence/cloud-repairs/easy-grace-before-holiday.png) — Healthy backup shows 14-day selector, Reset and warning-only explanatory copy before any skipped run.
  - 🖼️ [Grace values survive Save and reopening](../audit-dashboard/evidence/cloud-repairs/easy-grace-reopened.png) — The real wizard Save handler persisted the selected 14 days and manual reset, which remain visible when reopened.
  - [Grace, reset and real orchestration checks](../audit-dashboard/evidence/cloud-repairs/report.html) — Expired-grace absent drive remains Skipped on disk; a nonexistent share fails during grace, while warning notification/reset behavior is exercised through SendAlertsAsync.
  - [Fresh Easy UI, notification and cleanup checks](../audit-dashboard/evidence/cloud-repairs/followup-report.html) — 36 matching passing followup results are listed, with the complete broad-run failures disclosed separately.
- **Limitations / adversarial review:** Actual VPN/router/disconnected-NAS behavior is represented by known error codes and local fixtures; no physical network outage was induced.
- **I need from you:** B026: Review the report and listed limitations; accept the issue if this proof matches your requirement. No credentials or external messages were used.

### P1: After grace, skipped backups warn without failing

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** In progress
- **Context:** B029: In the backup settings (Easy Mode), there should be a grace period number of days selector setting to ignore skipped backups. During this grace period, no notifications should be sent if skipped; also add a button to restart the grace period (useful if user is offline for 2 weeks and grace period is 7 days). After grace period, skipped with warning notifications would be fired as normal. These are not as bad as real failure so should clearly be indicated as such (if we fired Failed just because we're offline, user would be unrightly worried, but of course after some time it is important user decides if it is normal backups are always skipped).
- **Current result:** Skip grace suppresses human alerts; expiration emits warning severity without promoting the stored run to Failed. Reset suppresses subsequent skip alerts. Share access probes preserve permission/missing-path failures and distinguish known outages. Relocation guidance is conditional, requires previous success and a local destination, and excludes deliberate cancellation/metered stops. Easy mode now always exposes the selector and Reset on its schedule step, including before any skipped run and after expiry. Its old Failed-escalation wording is corrected. The real wizard Save/reopen scenario confirms the 14-day choice/reset persist without faking a successful run.
- **Remaining:** Review the report and listed limitations; accept the issue if this proof matches your requirement. No credentials or external messages were used.
- **Proof — open and look:**
  - 🖼️ [Easy grace controls before a holiday](../audit-dashboard/evidence/cloud-repairs/easy-grace-before-holiday.png) — Healthy backup shows 14-day selector, Reset and warning-only explanatory copy before any skipped run.
  - 🖼️ [Grace values survive Save and reopening](../audit-dashboard/evidence/cloud-repairs/easy-grace-reopened.png) — The real wizard Save handler persisted the selected 14 days and manual reset, which remain visible when reopened.
  - [Grace, reset and real orchestration checks](../audit-dashboard/evidence/cloud-repairs/report.html) — Expired-grace absent drive remains Skipped on disk; a nonexistent share fails during grace, while warning notification/reset behavior is exercised through SendAlertsAsync.
  - [Fresh Easy UI, notification and cleanup checks](../audit-dashboard/evidence/cloud-repairs/followup-report.html) — 36 matching passing followup results are listed, with the complete broad-run failures disclosed separately.
- **Limitations / adversarial review:** Actual VPN/router/disconnected-NAS behavior is represented by known error codes and local fixtures; no physical network outage was induced.
- **I need from you:** B029: Review the report and listed limitations; accept the issue if this proof matches your requirement. No credentials or external messages were used.

### P1: Keep relocation separate from offline handling

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** In progress
- **Context:** B030: The locate moved backup UX should have NOTHING to do with this flow (except that the skipped with warning should mention, IF the backup previously succeeded at least once, what you sufggested earlier so user knows it is possible to point to a new location)
- **Current result:** Locate moved repository is available for an existing local, external-drive or network destination regardless of the last backup result. Its optional suggestion in a skipped-run message requires at least one earlier success. The chosen folder is checked with real Restic before a path change is offered.
- **Remaining:** Open the confirmation and wrong-password screenshots. The real-engine test moves a repository, reuses it, keeps the earlier snapshot and restores the new backup correctly. The native Windows folder picker remains a separate check.
- **Proof — open and look:**
  - 🖼️ [Confirm a repository that Restic actually opened](../audit-dashboard/evidence/review-feedback/03-repository-confirmation.png) — The confirmation says the saved password works, and explains why that alone does not identify the original repository.
  - 🖼️ [Reject a repository with the wrong password](../audit-dashboard/evidence/review-feedback/04-wrong-password-refused.png) — Restic rejected the password. The dialog refuses the change and the test checks that the old path remains saved.
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
  - [Cloud settings, notifications and skipped-backup tests](../audit-dashboard/evidence/cloud-repairs/report.html) — The report explains the cloud-connection and notification changes, shows their local tests, and identifies the checks that still need real accounts or devices.
- **Limitations / adversarial review:** The test supplies the folder path that a picker would return and clicks the production confirmation buttons. It uses the real engine, but it does not automate the Windows folder picker. Password access alone cannot identify the original repository.

- **Review comments received 2026-10-05 (B030):**
  > Show me proofs of how the locate moved backup flow is working (and what shows up if the last backup failed).
  >
  > HOWEVER, why do we care if the backup failed before? Won't it just redo a fresh new backup if the previous one failed? No risk of corruption?

- **I need from you:** B030: Open the confirmation and wrong-password screenshots. The real-engine test moves a repository, reuses it, keeps the earlier snapshot and restores the new backup correctly. The native Windows folder picker remains a separate check.

### P1: Clarify when Expected Drive Letter is useful

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** In progress
- **Context:** B032: - In which cases does it make sense to show Expected Drive Letter when creating a external drive destination?
- **Current result:** This remains Still needs testing. A prepared Windows check creates its own small virtual disk, gives it a unique label, checks it through the production drive detector, changes its letter and checks again.
- **Remaining:** Run the prepared virtual-drive check from an administrator PowerShell session, then ask me to inspect its result. It has not yet run with the required Windows privileges.
- **Proof — open and look:**
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
  - [Cloud settings, notifications and skipped-backup tests](../audit-dashboard/evidence/cloud-repairs/report.html) — The report explains the cloud-connection and notification changes, shows their local tests, and identifies the checks that still need real accounts or devices.
  - [Recorded test results and remaining gaps](../audit-dashboard/evidence/repair-verification.html) — Read the pass and fail results, open the original test records if needed, and see what each run did and did not test.
- **Limitations / adversarial review:** The script checks the identity of its newly created virtual disk before formatting it and never selects an existing physical disk. The current agent session is not elevated, so no mounted-disk result is claimed.

- **Review comments received 2026-10-05 (B032):**
  > Mark this a still need testing. Can't you simulate a fake external drive with custom label?

- **I need from you:** B032: Run the prepared virtual-drive check from an administrator PowerShell session, then ask me to inspect its result. It has not yet run with the required Windows privileges.

### P1: Failure notification layout and link to logs

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** In progress
- **Context:** B036: - Check that if a backup fail, the notification looks good and takes us to the failed run logs. It should jump a lineafter destinations FAILED, below show the failed destinations preceded by a bullet point, and then show X of Y destinations SUCCEEDED: 10 files restored, 10 byte-identical (same for all destinations normally), jump line and a bullet point list of successes.
- **Current result:** The test now renders the real failure popup and pointer-clicks its Open run details button. It verifies that the earlier failed run is selected even when a newer successful run exists, and that a missing run ID does not silently select an unrelated run.
- **Remaining:** Open the before-click popup and after-click log screenshots. Windows desktop placement and focus still need a native check.
- **Proof — open and look:**
  - 🖼️ [Failure notification before clicking](../audit-dashboard/evidence/review-feedback/01-failure-popup-before-click.png) — The actual alert has a readable title, the error, and an Open run details button.
  - 🖼️ [The alert opens the earlier failed run](../audit-dashboard/evidence/review-feedback/02-alert-selects-earlier-failed-run.png) — After a pointer click on Open run details, the older failed run is selected even though a newer successful run exists.
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
  - 🖼️ [The alert opens the failure it belongs to](../audit-dashboard/evidence/cloud-repairs/exact-failed-run.png) — An earlier failed run is selected even though a newer successful run is also in the history.
- **Limitations / adversarial review:** The alert handling and popup are production code; history is prepared to create the exact earlier-failure/newer-success case. This is a headless interface test, not a screenshot of a native Windows notification arriving during a live failure.

- **Review comments received 2026-10-05 (B036):**
  > Just test this exact scenario and prove it visually with screenshots showing what I want to verify!

- **I need from you:** B036: Open the before-click popup and after-click log screenshots. Windows desktop placement and focus still need a native check.

### P2: Remove the backup setup time estimate

- **Raised:** 2026-10-10
- **Type:** polish
- **Status:** Awaiting validation
- **Context:** The user asked to remove “About 2 minutes” from “Set up a backup” because it sounds long and does not reliably describe how long setup takes.
- **Current result:** The invitation says “Set up a backup”. No other user-facing occurrence of this time estimate was found.
- **Proof — open and look:** 🖼️ [Setup invitation without an estimate](../audit-dashboard/evidence/agent-work-20261010/setup-without-time-estimate.png) — The primary button has no duration claim.
- **Limitations / adversarial review:** Existing empty-state scenario passed with the real application resources. This is a wording change; no new behavior is introduced.
- **I need from you:** Confirm the revised wording.

### P2: E2E coverage audit — user-action ↔ scenario matrix

- **Raised:** 2026-06-11
- **Type:** coverage / process
- **Status:** Awaiting validation
- **Context:** R015: The user-action matrix now states exactly which tests click real UI, call models/fake engines, exercise local backup/restore, or inspect real OS policy objects. Newly added interactive coverage replaces several old model-only claims, and environment acceptance remains visible.
- **Current result:** The user-action matrix now states exactly which tests click real UI, call models/fake engines, exercise local backup/restore, or inspect real OS policy objects. Newly added interactive coverage replaces several old model-only claims, and environment acceptance remains visible.
- **Remaining:** Review the remaining partial/environment-dependent rows and choose which integration gaps should be prioritized next.
- **Proof — open and look:**
  - [R015 · Carryover evidence and test scope](../audit-dashboard/evidence/carryovers/report.html) — Open the corrected scope inventory and remaining gaps, with actual test records linked.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** This closes the audit-deliverable mismatch, not the separate request in R006 to fill every coverage gap. Latest test-run results, not green inventory cells, establish pass/fail.
- **I need from you:** R015: Review the remaining partial/environment-dependent rows and choose which integration gaps should be prioritized next.

### P2: Custom Healthchecks management server does not control the ping host

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** In progress
- **Context:** A003: Healthchecks terminal signals run before email and outside human-alert daily caps; first failed cells send failure promptly. Created or verified checks preserve the service's actual ping_url, including self-hosted ping hosts.
- **Current result:** Healthchecks terminal signals run before email and outside human-alert daily caps; first failed cells send failure promptly. Created or verified checks preserve the service's actual ping_url, including self-hosted ping hosts.
- **Remaining:** Review the report and listed limitations; accept the issue if this proof matches your requirement. No credentials or external messages were used.
- **Proof — open and look:**
  - [Monitoring delivery and self-host routing checks](../audit-dashboard/evidence/cloud-repairs/report.html) — A blocked fake email cannot delay the captured terminal ping; repeated same-status runs still ping; self-host verification preserves its ping URL.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** No live monitoring service contacted. A bare legacy UUID without a stored ping URL retains the public hc-ping.com destination until reverified. Failed pings are logged without changing backup results; management batch creation is not retried automatically.
- **I need from you:** A003: Review the report and listed limitations; accept the issue if this proof matches your requirement. No credentials or external messages were used.

### P2: Dropbox with our own app ID

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Open
- **Context:** B001: - Easily adding Dropbox, OneDrive, GoogleDrive destinations (consumer things) through RClone with our own app ID (so faster and we can control the scope).
- **Current result:** Production cloud operations materialize separate files per destination, legacy shared-file writes retain unrelated sections, and newer rclone-refreshed tokens are imported into the encrypted vault. Later interactive sign-in wins over stale disk credentials.
- **Remaining:** Authorize a disposable Dropbox account for actual sign-in, token refresh/restart, backup and byte-verified restore acceptance.
- **Proof — open and look:**
  - [Concurrent destinations and token persistence — passing file-backed tests](../audit-dashboard/evidence/cloud-repairs/report.html) — Two simultaneous writers preserve A and B; a simulated on-disk refresh survives rebuilding, while a new account sign-in defeats the older file.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** No real cloud sign-in, expiry/refresh or restart was performed. Official OneDrive and Google registration IDs are still empty and require the maintainer. Isolation prevents A/B writers overwriting one another; concurrent rclone processes for the same destination still use rclone's own token-write behavior and need live-account coverage.
- **I need from you:** B001: Authorize a disposable Dropbox account for actual sign-in, token refresh/restart, backup and byte-verified restore acceptance.

### P2: Email delivery integration

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** In progress
- **Context:** B008: - Email sending (does Restic have something equivalent to what we have?)
- **Current result:** Backrest supports email delivery through Shoutrrr. Backspace currently sends through its own Resend or Mailgun integration after combining the results of all parts of a backup.
- **Remaining:** Review the distinction between a Backrest operation alert and a complete Backspace backup summary. A real delivery test needs a configured provider and your chosen test recipient.
- **Proof — open and look:**
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
  - [Cloud settings, notifications and skipped-backup tests](../audit-dashboard/evidence/cloud-repairs/report.html) — The report explains the cloud-connection and notification changes, shows their local tests, and identifies the checks that still need real accounts or devices.
  - [Recorded test results and remaining gaps](../audit-dashboard/evidence/repair-verification.html) — Read the pass and fail results, open the original test records if needed, and see what each run did and did not test.
- **Limitations / adversarial review:** No email was sent. Local request tests do not prove delivery or inbox appearance. The app has not been migrated to Backrest or Shoutrrr delivery.

- **Review comments received 2026-10-05 (B008):**
  > You did not answer my question, once again! does Restic have something equivalent to what we have for email sending? Which we should piggyback on instead of reinventing the wheel?

  > Thanks.

- **Clarification:** In these comments, “Restic” means the combined Restic + Backrest stack. Prefer existing engine capabilities where they cover the required behavior.

- **I need from you:** B008: Review the distinction between a Backrest operation alert and a complete Backspace backup summary. A real delivery test needs a configured provider and your chosen test recipient.

### P2: Useful logs for every operation

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** In progress
- **Context:** B009: - Better logging info (per backup and per all other operations) like what we've achieved.
- **Current result:** Backspace now copies the engine’s original operation diagnostics into its saved run log. In the actual Dropbox test, the log now names the omitted file and includes Restic’s unsupported-file error instead of only saying that some files could not be read.
- **Remaining:** Open the Dropbox report and saved log. Wider checks against real cloud accounts remain outstanding.
- **Proof — open and look:**
  - [Actual Dropbox test: no download and no backed-up contents](../audit-dashboard/evidence/cloud-placeholder-20261005/report.html) — See the file measurements, snapshot checks and original engine error. The supplied file remained online-only.
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
  - 🖼️ [Returning to Backups shows a background failure](../audit-dashboard/evidence/cloud-repairs/external-scheduled-backup-card.png) — The card and health banner show a failure written by a separate settings/history writer while the app was already open.
  - 🖼️ [Activity shows the background run and its saved log](../audit-dashboard/evidence/cloud-repairs/external-scheduled-history.png) — The externally recorded failed run is selected, with its two-line failure output visible.
- **Limitations / adversarial review:** The Dropbox backup is a real engine run. Additional protocol tests cover fragmented log text and failure to fetch diagnostics. This does not prove every cloud operation and provider.

- **Review comments received 2026-10-05 (B009):**
  > Are we piggybacking on restic's logs as much as possible? Or did we reinvent the wheel to log stuff? Our app should be a light wrapper as much as possible, only adding missing functionality or exposing functionality better.

- **Clarification:** In these comments, “Restic” means the combined Restic + Backrest stack. Prefer existing engine capabilities where they cover the required behavior.

- **I need from you:** B009: Open the Dropbox report and saved log. Wider checks against real cloud accounts remain outstanding.

### P2: Review the referenced Word document

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** B010: (also check Docx file to make sure all is good)
- **Current result:** I reviewed the supplied Backspace TODO.docx in full. Backspace now implements its Option 2: an Avalonia desktop app with a bundled Backrest daemon driving restic. The document is an earlier architecture discussion, not a list of entirely missing features. The attached comparison distinguishes implemented behavior, deliberate differences, remaining work and claims in the document that should not guide new work.
- **Remaining:** Open the Word-document comparison. My recommendation is to keep the current architecture. Continue the existing restore/progress, cloud-source and native acceptance work; do not create a second engine rewrite or web-UI port. The review also found a local-authentication startup defect, tracked separately as N017.
- **Proof — open and look:**

  - [10 October: answer and evidence](../audit-dashboard/evidence/feedback-20261010/word-comparison.html) — I reviewed the supplied Backspace TODO.docx in full. Backspace now implements its Option 2: an Avalonia desktop app with a bundled Backrest daemon driving restic. The document is an earlier architecture discussion, not a list of entirely missing features. The attached comparison distinguishes implemented behavior, deliberate differences, remaining work and claims in the document that should not guide new work.
  - No completion proof claimed; the missing input is stated above.
- **Limitations / adversarial review:** The comparison combines code inspection and existing test evidence; it is not fresh live-cloud, reboot, VSS or mounting validation. The original document is preserved and its extracted text is included unchanged in the report.
- **Follow-up received 2026-10-10:**
  > "C:\Users\erwin\Desktop\Backspace TODO.docx" This is the file. Where do we stand compared to the options listed inside?
  > I want to see if we may need to update our roadmap or not.

- **I need from you:** Open the Word-document comparison. My recommendation is to keep the current architecture. Continue the existing restore/progress, cloud-source and native acceptance work; do not create a second engine rewrite or web-UI port. The review also found a local-authentication startup defect, tracked separately as N017.

### P2: Measure idle tray CPU use

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** B024: Also it is misleading to say "Backspace keeps running in the background so scheduled backups still fire"! App doesn't need to run in the background for this! User should be reassured about this. Only useful to keep to reopen fast. How are things handled if a backup or any other operation is running and user wants to quit? I think we should still push to keep app running in the systray if you can confirm CPU impact will be zero when no operation is running. This can be reassuring for people to see Backspace if there. But we should reassure them even if Backspace is quit, it'll stop existing operations but scheduled operations will be triggered as usual. Find the best way to frame this.
- **Current result:** Backspace now uses Avalonia 12.1.3, whose rendering loop sleeps when idle. Matched native measurements show less CPU use than the old framework. A separate low-overhead, one-minute sample measured about 0.0036% of this machine’s total CPU with the window hidden; Backrest used no measurable CPU in that interval.
- **Remaining:** Review the measurements, then check the actual app at your Windows display scale and leave it idle in the tray while watching Task Manager. Confirm that the layout remains correct and CPU settles near zero. The isolated native-window reduction is measured, but the full tray and scaled-display check remains pending. B040 retains the reboot test.
- **Proof — open and look:**

  - [Measured idle CPU before and after the framework update](../audit-dashboard/evidence/agent-work-20261010/idle-cpu-12.html) — Backspace now uses Avalonia 12.1.3, whose rendering loop sleeps when idle. Matched native measurements show less CPU use than the old framework. A separate low-overhead, one-minute sample measured about 0.0036% of this machine’s total CPU with the window hidden; Backrest used no measurable CPU in that interval.

  - [10 October: answer and evidence](../audit-dashboard/evidence/feedback-20261010/idle-cpu.html) — The new native measurements still show idle CPU use: about 0.156% of this 22-thread machine with the window hidden. Backrest itself used no measurable CPU in that interval. Teleporter uses Avalonia 12.1.3, software rendering and a fully closed window. Backspace uses Avalonia 11.2.3; copying the renderer and closing the window in isolated probes did not reach zero CPU.
  - [Idle CPU with the final design](../audit-dashboard/evidence/design-native-idle/report.html) — Two 30-second samples: 0.203% visible and 0.209% hidden of total CPU on this 22-thread host. Empty disposable configuration; no universal zero-CPU claim.
  - [Idle CPU measurements and diagnosis](../audit-dashboard/evidence/idle-cpu-comparison.html) — Compare raw visible/hidden-window samples and the shared production-builder confirmation; measurement scope and machine details are disclosed.
- **Limitations / adversarial review:** Production window and styles in a native Windows host, real owned Backrest, empty disposable configuration. No tray icon, scheduled task registration, update checks or active backups in the CPU probe. Detailed and low-overhead samplers are identified separately. An artificial 125% bitmap capture showed displaced content; actual scaled-display acceptance is still needed. Windows screenshot inspection timed out awaiting app approval.
- **Follow-up received 2026-10-10:**
  > You did not address all my original comments/questions. 
  > FYI With this project you achieved 0% CPU usage when app was in systray but not doing anything:
  > C:\Users\erwin\Dropbox\Projects\GitHub\Teleporter

- **I need from you:** Review the measurements, then check the actual app at your Windows display scale and leave it idle in the tray while watching Task Manager. Confirm that the layout remains correct and CPU settles near zero. The isolated native-window reduction is measured, but the full tray and scaled-display check remains pending. B040 retains the reboot test.

### P2: Interactive OAuth test switch and reusable tokens

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** In progress
- **Context:** B034: - To simplify the obtaining of tokens for the test, and also test at the same time what a user would face for this, please add a config flag for the test (set to false by default, can be changed by the developer manually in the tests project main file) that'll make the test always show the UX to the user while testing while trying to get the token, so the user can interact only at that point. Then save for all further tests. This way if the E2E test suite runs, it'll reuse any existing tokens, and always ask the developer if a token is missing (or the always ask config flag is set to true, only helpful when trying to test specifically the auth screens).
- **Current result:** The interactive sign-in switch and saved-token reuse exist, but successful authentication against your actual provider accounts is still unproven. This issue remains Still needs testing.
- **Remaining:** Choose a provider, sign in through the real destination editor, run a small backup, restart the app, run again without signing in, then restore and compare the files. The report and chat give the steps.
- **Proof — open and look:**
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
  - 🖼️ [OneDrive after a simulated successful sign-in](../audit-dashboard/evidence/cloud-repairs/onedrive-connected.png) — Connected appears only after the editor receives the sign-in result and discovers the drive. Microsoft’s responses were simulated for this screenshot.
  - 🖼️ [OneDrive after permission is refused](../audit-dashboard/evidence/cloud-repairs/onedrive-denied.png) — The refusal message remains visible and the editor stops showing a busy state. No token exchange follows the refusal.
  - [Recorded test results and remaining gaps](../audit-dashboard/evidence/repair-verification.html) — Read the pass and fail results, open the original test records if needed, and see what each run did and did not test.
- **Limitations / adversarial review:** OneDrive and Google Drive need valid app-registration details. Mock authorization responses cannot prove provider consent, refreshed tokens or real file delivery.

- **Review comments received 2026-10-05 (B034):**
  > mark this as still needs testing. In our chat session, tell me what i need to do step by step to complete testing which needs me.

- **Follow-up — 2026-10-09:** Show this explicitly as a pending user test in the dashboard, with complete sign-in, backup, reopen, restore and file-comparison instructions.
- **I need from you:** B034: Choose a provider, sign in through the real destination editor, run a small backup, restart the app, run again without signing in, then restore and compare the files. The highlighted pending-test panel in [B034](http://127.0.0.1:4317/#B034) gives all the steps and explains what to report back.

### P2: Measure cloud-file hydration after backup

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** In progress
- **Context:** B035: - Test if --exclude-cloud-files is OFF, whether it'll cause the files to be downloaded permanently, or only temporarily (useful to backup dropbox to another destination without cluttering the local drive)/
- **Current result:** A working experiment now proves the key restic behavior. Through a real temporary Windows filesystem, the first backup reads both files; the next complete backup reads neither. A same-size, same-date change is missed unless the virtual source supplies a distinct timestamp for that revision. With that signal, only the changed file is read. This uses simulated cloud data, so the online-only backup feature is still unfinished.
- **Remaining:** Agent work: connect the Dropbox source API; persist provider revisions and original file dates; download only requested revisions into a space-limited temporary cache; integrate the virtual source with backup, restore and the default-on exclusion option. Preserve complete snapshots and leave original placeholders untouched. Source-account consent and a live Dropbox check will be needed after this is implemented.
- **Proof — open and look:**

  - [Real-engine experiment: reuse unchanged contents without missing a changed revision](../audit-dashboard/evidence/agent-work-20261010/cloud-change-detection.html) — A working experiment now proves the key restic behavior. Through a real temporary Windows filesystem, the first backup reads both files; the next complete backup reads neither. A same-size, same-date change is missed unless the virtual source supplies a distinct timestamp for that revision. With that signal, only the changed file is read. This uses simulated cloud data, so the online-only backup feature is still unfinished.

  - [10 October: answer and evidence](../audit-dashboard/evidence/feedback-20261010/cloud-source.html) — Other projects demonstrate the important pieces. Cloudstic reads Google Drive and OneDrive directly through their APIs and tracks changes; it has its own backup engine. Arq avoids downloading unchanged files but makes the original Windows files local. A separate source layer could give Restic and Backrest the requested behavior. Backspace still needs that implementation and proof. Skipping online-only files is already the default.
  - [Online-only source research and proposed approach](../audit-dashboard/evidence/remaining-work-20261009/cloud-source-research.html) — Compares verified competitor behavior, explains the Restic constraint, and lists the acceptance checks. Research, not a claim of working implementation.
  - [Actual Dropbox test: no download and no backed-up contents](../audit-dashboard/evidence/cloud-placeholder-20261005/report.html) — See the file measurements, snapshot checks and original engine error. The supplied file remained online-only.
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
  - [Recorded test results and remaining gaps](../audit-dashboard/evidence/repair-verification.html) — Read the pass and fail results, open the original test records if needed, and see what each run did and did not test.
- **Limitations / adversarial review:** The experiment uses real WinFSP, Backrest and restic with simulated remote metadata and contents. No Dropbox account or original placeholder was accessed. It does not test provider authorization, disk-space limits, temporary-cache cleanup, original-date recovery or the finished interface. The distinct timestamp belongs only to the experimental virtual source; production must retain the original date separately.

- **Review comments received 2026-10-05 (B035):**
  > mark as still needs testing and provide me step by step instruction in the chat so we can complete this test together.
  > HOWEVER it seems you can already do a test yourself with this folder:
  > C:\Users\erwin\Dropbox\Apps\InpsydeBackWPup\upbraining.com-store
  > Contains one file online only. See how it is handled. If your testing makes this file local and no longer online only, tell me.

  > exclude-cloud-files => Is it an option we have currently? Is it a legacy one from Duplicacy we had in duplimate? If so, how does Restic handles this?

- **Follow-up — 2026-10-09:** Investigate backing up online-only Dropbox, Google Drive and OneDrive files without changing the user’s local availability choice. Check provider metadata for changes, download changed content into a separate temporary location, check free space before downloading, and warn clearly when a file cannot be included. The requested default is to ignore online-only files; including them must explain that local placeholders stay online-only. Research existing products and the Restic/Backrest integration before proposing implementation.
- **Research result — 2026-10-09:** Cloudstic documents direct Google Drive and OneDrive source reads with change tracking; Arq documents changed-file checks but hydrates the original Windows files. Restic users also report backups from rclone-mounted sources. These are useful precedents, not proof of the exact requested workflow. The linked report separates documented behavior from implementation and testing still needed in Backspace.
- **I need from you:** No action now; the agent retains this implementation work.

### P2: Prove the schedule fires after a real reboot

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** In progress
- **Context:** B040: Before pointing it at anything you care about, do this once with a throwaway folder: - Reboot the machine — wait for the scheduled task to fire on its own, confirm a second snapshot appears. - Restore the first snapshot to a different folder. - fc /b (or PowerShell Get-FileHash) the restored files against the originals.
- **Current result:** This is now explicitly Still needs testing. A prepared script creates an isolated local backup and a Windows scheduled task using the production task definition. After a real reboot, it checks when the task ran and compares a file recovered from the resulting snapshot.
- **Remaining:** Use Setup in an administrator PowerShell window, restart before the displayed due time, leave Windows at the sign-in screen until two minutes after that time, then use Check. The script never restarts the machine itself.
- **Proof — open and look:**
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
  - [The repaired scheduled command really starts the app](../audit-dashboard/evidence/packaged-task-action-final/report.html) — The exact generated command was tested through 25 process launches and 46 checks. It waits for completion and returns failures correctly. Windows Task Scheduler registration and reboot are separate checks.
  - [Backup, maintenance and migration in the packaged app](../audit-dashboard/evidence/packaged-workflows-final/report.html) — Twenty-two process launches passed 55 checks. The report includes actual snapshots, full-file comparisons, paused or missing backups, and repeating migration without duplicating its result.
  - [The original scheduled-command failure](../audit-dashboard/evidence/packaged-task-action-original-failure/report.html) — This preserved diagnostic shows the old command reporting success without starting a backup. Compare it with the repaired-command report.
- **Limitations / adversarial review:** Only task-definition generation has been checked so far. No real reboot result is claimed. The planned test covers a local folder, not cloud authentication, network shares or other users’ private files.

- **Review comments received 2026-10-05 (B040):**
  > What do i need to do exactly? Did you create a powershell script to test this? Why is this issue not marked as Still needs testing?

- **Follow-up — 2026-10-09:** The dashboard must explicitly show this as a pending test for the user, with Setup, reboot timing and Check instructions inside the issue.
- **I need from you:** B040: Use Setup in an administrator PowerShell window, restart before the displayed due time, leave Windows at the sign-in screen until two minutes after that time, then use Check. The script never restarts the machine itself.

### P2: Restore the first snapshot to a different folder

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** B041: Before pointing it at anything you care about, do this once with a throwaway folder: - Reboot the machine — wait for the scheduled task to fire on its own, confirm a second snapshot appears. - Restore the first snapshot to a different folder. - fc /b (or PowerShell Get-FileHash) the restored files against the originals.
- **Current result:** The local E22 scenario restores an earlier revision into a different folder. Real restore validation now checks SHA-256 for every regular file, including empty files, and rejects same-size corruption and wrong-revision bytes.
- **Remaining:** Review the linked evidence and accept the item, or describe what still needs adjustment.
- **Proof — open and look:**
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** Disposable local data was used. Unsupported metadata and real cloud accounts have separate acceptance limits.
- **I need from you:** B041: Review the linked evidence and accept the item, or describe what still needs adjustment.

### P2: Compare restored files with the original hashes

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** B042: Before pointing it at anything you care about, do this once with a throwaway folder: - Reboot the machine — wait for the scheduled task to fire on its own, confirm a second snapshot appears. - Restore the first snapshot to a different folder. - fc /b (or PowerShell Get-FileHash) the restored files against the originals.
- **Current result:** The Huge E34 scenario completed with exactly 20,000 files and 2,147,483,648 bytes. Real backup and restore succeeded; every restored file matched full SHA-256, with zero failures. The copied test host and unique D: scratch kept this run separate from ordinary E34 artifacts.
- **Remaining:** Review the linked evidence and accept the item, or describe what still needs adjustment.
- **Proof — open and look:**
  - 🖼️ [App after the large restore](../audit-dashboard/evidence/huge-scale/E34-restore-long-include-list/screenshots/03-after-restore.png) — Backspace remains open after the 20,000-file real restore. Use the linked rendered report for restored counts and full-file hashes; the backup card alone is not content-integrity proof.
  - [20,000-file / 2 GiB restore evidence](../audit-dashboard/evidence/huge-scale/report.html) — Open exact source/restored counts, full-file SHA-256 verdict, metadata scope, final test-runner result and app screenshot.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** Disposable local data was used. Unsupported metadata and real cloud accounts have separate acceptance limits.
- **I need from you:** B042: Review the linked evidence and accept the item, or describe what still needs adjustment.

### P2: Audit interrupted work against the original notes

- **Raised:** 2026-10-02
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** Thoroughly audit the interrupted implementation, tests, docs and original notes; preserve each request and distinguish implementation from proof.
- **Result:** 76 issues in the local dashboard, including every original request and the older active roadmap entries. No existing item was deleted.
- **Proof — open and look:** [Audit overview](../audit-dashboard/evidence/audit-overview.html) — test results, per-issue evidence and remaining work; [dashboard screenshot](../audit-dashboard/evidence/dashboard-overview.png) — separate issues, evidence links and user-controlled completion.
- **Limitations / adversarial review:** Real-account OAuth/email, VSS, reboot, native simultaneous mounts and idle CPU remain unproven. Restore safety and persistence probes reproduce negative cases, and complete hash checks include a wrong-revision control. Historical screenshots are not promoted to fresh results.
- **I need from you:** Review the dashboard and identify which findings or scope need revision.

### P3: "Restores" multi-restore cards — shipped

- **Raised:** 2026-06-12
- **Type:** feature
- **Status:** In progress
- **Context:** B018: - If user initiates a restore operation, and then go to another menu item, he cannot go back to see the status of the restore operation. Please add a Restores menu item on the left with a visual dynamic icon indicating it is restoring something (like an animation that'd express a file download), and inside this menu item it should show a list of cards with all the active restore operations with basic status. Clicking on one card should get the user back to where he was before he moved away from the restore screen. As soon as user creates the restore, it should show this menu item, highlighted in the same color as when user is in Destinations or Backup.  B019: - If user initiates a restore operation, and then go to another menu item, he cannot go back to see the status of the restore operation. Please add a Restores menu item on the left with a visual dynamic icon indicating it is restoring something (like an animation that'd express a file download), and inside this menu item it should show a list of cards with all the active restore operations with basic status. Clicking on one card should get the user back to where he was before he moved away from the restore screen. As soon as user creates the restore, it should show this menu item, highlighted in the same color as when user is in Destinations or Backup.
- **Current result:** The duplicate was the selected restore’s detail panel appearing above the same restore in the card list. Opening a restore now replaces the list with its details. Cards show View progress while running and View results after finishing, with an All restores button to return.
- **Remaining:** Open the list and result screenshots. Confirm that the one-card-to-one-result navigation is clear.
- **Proof — open and look:**
  - 🖼️ [One restore, one card, an explicit results button](../audit-dashboard/evidence/review-feedback/03-restore-list-explicit-results.png) — The card identifies the source, destination and result. View results is a visible action.
  - 🖼️ [The result replaces the list](../audit-dashboard/evidence/review-feedback/04-restore-result-without-duplicate-card.png) — The selected result appears once, with an All restores button. A zero-file failure no longer says Preparing.
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
  - [Restore safety and file checks](../audit-dashboard/evidence/restore-repairs/report.html) — See which files were recovered, which existing files were protected, and how failures, cancellation and password changes were checked.
- **Limitations / adversarial review:** The test uses the production views and real pointer clicks, with a prepared restore history. A complete live desktop restore followed through this navigation still needs testing.

- **Review comments received 2026-10-05 (B018):**
  > The Restores page is not clear at all. Why are there two entries for BackupB? It is not intuitive what users have to do to see restore results (is it supposed to take them back to original screen they were on when they initiated the restore?).

- **I need from you:** B018: Open the list and result screenshots. Confirm that the one-card-to-one-result navigation is clear.

### P3: OneDrive + Google Drive OAuth client_ids

- **Raised:** 2026-06-12 (audit of BACKSPACE_PORT_DESIGN.md § 6.2)
- **Type:** feature
- **Status:** Open
- **Context:** B002: - Easily adding Dropbox, OneDrive, GoogleDrive destinations (consumer things) through RClone with our own app ID (so faster and we can control the scope).  B003: - Easily adding Dropbox, OneDrive, GoogleDrive destinations (consumer things) through RClone with our own app ID (so faster and we can control the scope).
- **Current result:** The OneDrive editor discovers the signed-in user's actual default drive ID/type before marking connected; engine use discovers missing legacy identity. Runtime uses the documented localhost callback host/path for Microsoft native registration. The real editor command is covered through local callback, PKCE exchange, discovery and consent refusal with simulated provider HTTP responses. Google rclone configuration now matches the actual drive.file OAuth grant, and the editor describes that restricted access accurately.
- **Remaining:** Supply or register the official OneDrive app ID and authorize a disposable Microsoft account for actual sign-in, drive discovery, refresh and backup/restore acceptance. Supply or register the official Google app ID and authorize a disposable Google account for actual sign-in, refresh and backup/restore acceptance.
- **Proof — open and look:**
  - 🖼️ [OneDrive editor after simulated successful sign-in](../audit-dashboard/evidence/cloud-repairs/onedrive-connected.png) — Connected appears after the editor command, real local callback and fake token/drive responses; this is not a real account proof.
  - 🖼️ [Consent refusal remains visible](../audit-dashboard/evidence/cloud-repairs/onedrive-denied.png) — Denying consent leaves a red refusal message, no stuck busy state, and no token exchange.
  - [Concurrent destinations and token persistence — passing file-backed tests](../audit-dashboard/evidence/cloud-repairs/report.html) — Two simultaneous writers preserve A and B; a simulated on-disk refresh survives rebuilding, while a new account sign-in defeats the older file.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** Default personal/business drive only; no SharePoint-site or multi-drive selector. Real browser consent, account permissions and official app registration are unverified. Legacy expired tokens may require signing in again before drive discovery. Google registration and real account proof remain required. Repositories created by another OAuth client may not be accessible under drive.file.
- **I need from you:** B002: Supply or register the official OneDrive app ID and authorize a disposable Microsoft account for actual sign-in, drive discovery, refresh and backup/restore acceptance. B003: Supply or register the official Google app ID and authorize a disposable Google account for actual sign-in, refresh and backup/restore acceptance.

### P3: Delete BACKSPACE_PORT_DESIGN.md once port-plan gaps close

- **Raised:** 2026-06-12 (user request)
- **Type:** docs cleanup
- **Status:** In progress
- **Context:** R017: The ignored historical BACKSPACE_PORT_DESIGN.md still exists. Its June port-gap table is stale relative to the repaired implementation; current README, architecture, gotchas and testing documents carry the maintained description.
- **Current result:** The old port document still names unfinished work: official OneDrive/Google app registrations and native acceptance of Windows mounting. Most other items are implemented, including process ownership, Test restore and Windows update support. It also contains obsolete statements, so it should be treated only as historical context.
- **Remaining:** Keep the historical file until the remaining port gaps are completed or explicitly deferred, then apply your existing permission to remove it. The maintained roadmap now maps every unresolved item, so nothing depends on remembering this document. No repeat deletion approval is needed once your condition is met.
- **Proof — open and look:**

  - [10 October: answer and evidence](../audit-dashboard/evidence/feedback-20261010/port-plan.html) — The old port document still names unfinished work: official OneDrive/Google app registrations and native acceptance of Windows mounting. Most other items are implemented, including process ownership, Test restore and Windows update support. It also contains obsolete statements, so it should be treated only as historical context.
  - [R017 · Carryover evidence and test scope](../audit-dashboard/evidence/carryovers/report.html) — The report records that the historical file was preserved and identifies its stale gap claims without treating them as present implementation facts.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** The root document was retained because your permission was conditional on no unported useful work remaining. Implemented Windows updates still need their separately listed native acceptance checks.
- **Follow-up received 2026-10-10:**
  > So there is no remaining useful thing inside (or element we haven't ported yet)? If so you can delete.

- **I need from you:** No action for this work now; the agent retains the next step.

### P3: Auto-update channel — light variant

- **Raised:** 2026-06-12 (audit of BACKSPACE_PORT_DESIGN.md § 13)
- **Type:** infrastructure
- **Status:** In progress
- **Context:** R018: The production update checker now attaches a callback that opens the matching official release page. Injected HTTP exercises the actual checker, including rejection of the current version, prereleases and an unsafe file URL.
- **Current result:** The later Windows request is implemented under U001: periodic checks, release notes, skipping a version, verified bundle installation and recovery. Other platforms retain the lightweight release-page notification. The original notification tests and the new updater tests passed; native Windows acceptance is still pending.
- **Remaining:** The later Windows updater request expands this original scope. Windows now supports verified bundle installation and recovery; finish the native acceptance checks under U001. Other platforms retain the lightweight notification.
- **Proof — open and look:**
  - [R018 · Carryover evidence and test scope](../audit-dashboard/evidence/carryovers/report.html) — All four production-checker cases passed: only a newer stable HTTPS release supplies a click action; invoking it opens precisely that release URL.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** The four test cases use deterministic GitHub-style responses and an observed open-release action. No live public release request, native popup, download, installation or packaged startup is claimed.
- **I need from you:** R018: Review the Windows update evidence under U001 and its pending native checks. This earlier request remains open until you validate it.

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

**Current workaround:** substitute unsupported bracket matches with
single-character wildcards and restore into an isolated staging folder
on the destination volume. Validate that operation's result and promote
only requested paths into the user's target. Unrelated pre-existing
target files are never swept. Negative preservation, failed-output and
containment cases are covered in the repair report. Restic restores
regular file bytes, but Windows metadata, junctions, sparse allocation
and ACLs have explicit limitations; a perfect metadata round-trip is
not claimed.

**Frequency**: rare. Brackets in filenames are uncommon; this only
fires for partial restore on Windows when the user picks at least one
file with `[` or `]` in its name. Folder picks with bracketed folder
names work natively via the `snapshotID:subfolder` syntax. The
over-fetch is small (only same-length siblings in the same directory)
and confined to the operation's staging folder. Only verified requested
output is promoted; failed or cancelled output cannot overwrite a good
existing file.

**Upstream status**: prior attempts at a literal-include mode in restic
were closed (#2276, #2448, #3005, #3658 — the last explicitly as
`not planned`; #4867 remains open as the original feature request;
#5565 closed as duplicate of #4867).
- [`garethgeorge/backrest#1254`](https://github.com/garethgeorge/backrest/pull/1254)
  added the same `?`-substitution to Backrest's `escapeGlob` —
  **merged 2026-06-30**. It fixes Backrest's `/v1.Backrest/Restore` RPC, which
  Backspace does not use (we build a raw `restic restore` invocation and
  run it via `/v1.Backrest/RunCommand`), so it does **not** gate our
  workaround — it only helps other Backrest users.
- [`restic/restic#21872`](https://github.com/restic/restic/pull/21872)
  adds `--include-from-raw` (literal-path matching, mirrors
  `backup --files-from-raw` from PR #3017). Closes #4867 and #5565.
  **Open when checked on 2026-10-02**. Literal-path support could retire
  wildcard substitution after the bundled version and negative cases
  are validated; safe staging remains useful independently.

The in-process workaround stays load-bearing until restic #21872 ships
in a restic release we bundle. The writeup for the (now merged) Backrest
side lives at
[`docs/backrest-upstream-bracket-bug.md`](backrest-upstream-bracket-bug.md).

**Linux / macOS**: not affected. Backrest's `escapeGlob`
backslash-escapes `[` and `]` correctly there, and restic honors the
escape via `filepath.Match`'s documented `'\\' c` rule on non-Windows.
