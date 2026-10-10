# ROADMAP.md — work tracker

This file is the **source of truth** for in-flight work. Pair with
[GOTCHAS.md](GOTCHAS.md) for the upstream-driven limitations the code
already works around, and [AGENTS.md § The ROADMAP workflow](../AGENTS.md#the-roadmap-workflow-source-of-truth)
for the workflow rules an agent must follow before touching this file.

## Actively working

**Review queue — 9 October 2026:** Open [the local review desk](http://127.0.0.1:4317/) using the root BAT launcher. Its filters show the current review and testing queues. New Windows-update evidence and online-only file research are attached to their issues. Reboot and live-cloud instructions remain prominently marked as pending tests from you. Your accepted issues and comments remain preserved. [Your original notes](http://127.0.0.1:4317/notes) link to all 42 derived issues.

**I need from you:** Review the new replies and screenshots. Live cloud checks need account consent and provider configuration; email and Healthchecks checks need a chosen test destination. The prepared Windows checks need an administrator session and, for B040, a real reboot. The referenced Word document and the existing restore-architecture and historical-plan decisions remain outstanding. No restart or outgoing email has been performed.

**Older items need attention:** The June and July items still marked Awaiting validation are more than a week old. Please accept their proof or explain what is still missing; they have not been silently removed.

### P0: Development launchers terminate unrelated processes and replace configuration

- **Raised:** 2026-10-03
- **Type:** bug
- **Status:** In progress
- **Context:** A024: All four development launchers preserve portable configuration and unrelated output files, avoid terminating app/engine processes, and never seed test settings. --build-only supports rebuilds without starting the app. Build failure or missing output is an error and does not launch a stale executable; file-lock errors direct the user to Quit normally and retry.
- **Current result:** All four development launchers preserve portable configuration and unrelated output files, avoid terminating app/engine processes, and never seed test settings. --build-only supports rebuilds without starting the app. Build failure or missing output is an error and does not launch a stale executable; file-lock errors direct the user to Quit normally and retry.
- **Remaining:** Review the 24-case launcher safety report and confirm the item.
- **Proof — open and look:**
  - [24 launcher safety checks](../audit-dashboard/evidence/launcher-safety.html) — Expand each result to see the command output: preserved settings and files, no stale launch on failure or missing output, no process termination, and safe paths containing spaces, & and !.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** Copied launchers ran through actual Windows cmd and Git Bash against fake dotnet/app tools in disposable folders, including spaces and shell metacharacters. Configuration/secret bytes and unrelated files were preserved; four disposable same-name processes stayed alive. This proves launcher behavior, not a real build or native macOS/Linux app launch.
- **I need from you:** A024: Review the 24-case launcher safety report and confirm the item.

### P0: rclone orphan-safe kill

- **Raised:** 2026-06-12 (audit of BACKSPACE_PORT_DESIGN.md § 6.3)
- **Type:** reliability
- **Status:** Awaiting validation
- **Context:** B005: - When invoking rclone, keep track of PIDs so they can be reliably killed orphan processes (but never kill other rclone which may have been started by other unrelated processes)
- **Current result:** Backspace records the identity of each child process it starts. On a later launch, it stops a leftover child only if its PID, start time and executable still match. This prevents an old PID from identifying an unrelated process after Windows reuses that number.
- **Remaining:** Review the ownership tests. They include an unrelated process that must stay alive and an old PID whose identity no longer matches.
- **Proof — open and look:**
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
  - [Background-process and settings safety checks](../audit-dashboard/evidence/infrastructure-tests/final-report.html) — All 50 focused checks passed. An earlier broad run recorded 933 passes, no failures and 2 skips that required credentials.
  - [Earlier background-service repair results](../audit-dashboard/evidence/infrastructure-tests/report.html) — All 86 checks passed, with no failures or skips. The report records the settings, credentials and process behavior covered by that run.
  - [Recorded test results and remaining gaps](../audit-dashboard/evidence/repair-verification.html) — Read the pass and fail results, open the original test records if needed, and see what each run did and did not test.
- **Limitations / adversarial review:** If Windows will not let Backspace inspect a process, it leaves that process alone. This can leave an orphan running, but avoids killing another program.

- **I need from you:** B005: Review the ownership tests. They include an unrelated process that must stay alive and an old PID whose identity no longer matches.

### P0: Bracket-name restore cleanup deletes unrelated pre-existing target files

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** A008: Restores use unique staging on the destination volume and promote only selected output. Wildcard overmatches and unrelated existing files are preserved.
- **Current result:** The restore now writes to a separate temporary folder first, then copies only the files you selected into the destination. Files already there are left alone unless they are the selected files you asked to restore.
- **Remaining:** Open the restore safety report. Check the examples where unrelated files and an existing original file survive the restore.
- **Proof — open and look:**
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
  - [Restore safety and file checks](../audit-dashboard/evidence/restore-repairs/report.html) — See which files were recovered, which existing files were protected, and how failures, cancellation and password changes were checked.
  - [Recorded test results and remaining gaps](../audit-dashboard/evidence/repair-verification.html) — Read the pass and fail results, open the original test records if needed, and see what each run did and did not test.
- **Limitations / adversarial review:** The tests include names with brackets, extra files matched by a wildcard, and a restore that fails after writing only part of a file.

- **Review comments received 2026-10-05 (A008):**
  > - B005  When invoking rclone, keep track of PIDs so they can be reliably killed orphan processes (but never kill other rclone which may have been started by other unrelated processes)
  > => Why would you even need to check start times? Didn't you do what I suggested? If you kept track of the PID of launched child processes, how could ownership possibly be uncertain?

- **I need from you:** A008: Open the restore safety report. Check the examples where unrelated files and an existing original file survive the restore.

### P0: Restore wizard doesn't offer a revision picker (regression vs Duplimate) — Awaiting validation

- **Raised:** 2026-06-12
- **Type:** bug (feature regression vs Duplimate)
- **Status:** In progress
- **Context:** R005: The populated picker is present. S14 renders five dated revisions with the newest selected and Next enabled. S44 asserts a dynamically loaded nonempty list and older selection; E22 separately restores and byte-compares the older real snapshot.
- **Current result:** The populated picker is present. S14 renders five dated revisions with the newest selected and Next enabled. S44 asserts a dynamically loaded nonempty list and older selection; E22 separately restores and byte-compares the older real snapshot.
- **Remaining:** Open the populated picker image; confirm that the revision-selection step meets your expectation.
- **Proof — open and look:**
  - 🖼️ [Five revisions and an enabled Next button](../audit-dashboard/evidence/carryovers/revision-picker-populated.png) — Five dated rows (#5 to #1) appear; #5 is highlighted and Next: pick files to restore is enabled.
  - [R005 · Carryover evidence and test scope](../audit-dashboard/evidence/carryovers/report.html) — The separate real E22 test compares every restored regular file against the saved older source copy, not the newer source.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** S14 uses synthetic list entries. S44 uses the in-memory engine and does not click Next. E22 supplies real-engine old-revision byte proof through the restore service, not the exact GUI selection-to-restore click sequence.
- **I need from you:** R005: Open the populated picker image; confirm that the revision-selection step meets your expectation.

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
- **Status:** Open
- **Context:** R007: Restore still uses Backrest RunCommand with a raw restic restore command for snapshot subfolder targeting and selected-file recovery. The unsafe target sweep, false terminal success and unacknowledged cancellation were repaired; those fixes do not decide the architectural tradeoff.
- **Current result:** Restore still uses Backrest RunCommand with a raw restic restore command for snapshot subfolder targeting and selected-file recovery. The unsafe target sweep, false terminal success and unacknowledged cancellation were repaired; those fixes do not decide the architectural tradeoff.
- **Remaining:** Decide whether to retain the guarded raw-command restore path, or commission a native Backrest Restore-RPC parity investigation covering bracket/subfolder semantics, overwrite safety, cancellation and retry behavior.
- **Proof — open and look:**
  - [R007 · Carryover evidence and test scope](../audit-dashboard/evidence/carryovers/report.html) — The report lists the passing overmatch/partial-error/cancellation negative controls and the unresolved architecture choice.
  - [Detailed restore safety report](../audit-dashboard/evidence/restore-repairs/report.html) — Open restore staging, terminal truth and cancellation evidence with its integration limits.
- **Limitations / adversarial review:** Raw-command restores retain extra ownership and staging code in Backspace. Progress is intentionally indeterminate until authoritative output arrives. The regression suite uses controlled daemon responses for cancellation boundaries; it does not prove every network interruption.
- **I need from you:** R007: Decide whether to retain the guarded raw-command restore path, or commission a native Backrest Restore-RPC parity investigation covering bracket/subfolder semantics, overwrite safety, cancellation and retry behavior.

### P0: Restore enumerates every leaf when a folder is selected

- **Raised:** 2026-06-11
- **Type:** bug + design
- **Status:** Awaiting validation
- **Context:** R008: Fully selected subtrees collapse to a minimal include cover. S47 checks whole-folder collapse and the negative partial-selection case; E35 sends collapsed patterns through the real restore engine and verifies bytes. E34 separately uses an include file for large selections.
- **Current result:** Fully selected subtrees collapse to a minimal include cover. S47 checks whole-folder collapse and the negative partial-selection case; E35 sends collapsed patterns through the real restore engine and verifies bytes. E34 separately uses an include file for large selections.
- **Remaining:** Inspect the folder-selection and real-restore evidence; accept if that addresses the 2,397-argument report.
- **Proof — open and look:**
  - [R008 · Carryover evidence and test scope](../audit-dashboard/evidence/carryovers/report.html) — S47 proves full folders collapse and partial leaves stay explicit; E35 and E34 passed with real local backup/restore and full regular-file byte verification.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** S47 exercises the selection projection, not a pointer click on a large tree. E35 exercises the request-to-engine path with the minimized set. The normal run uses smaller default data; huge-scale evidence is recorded separately.
- **I need from you:** R008: Inspect the folder-selection and real-restore evidence; accept if that addresses the 2,397-argument report.

### P0: Restore crashes with "filename or extension is too long" on large file sets

- **Raised:** 2026-06-11
- **Type:** bug (regression from snap:subfolder refactor)
- **Status:** Awaiting validation
- **Context:** B021: Regarding the include-file issue, make sure to increase the FixtureSize to match what I tested in prod. MAybe you could add a global flag for ALL the E2E tests, that'd make it possible to artificially make the repos much larger and bigger (up to 2GB and tens of thousands of files). We could enable it only before pushing a commit to Github to make sure all the tests also work at scale. For common iterations, we'd not need to test them (the goal is that this test would be unlikely to fail if the smaller ones passed).
- **Current result:** The GitHub rehearsal passed the full suite and the real-engine backup/restore of 20,000 files, approximately 2 GiB. Every restored file matched its original SHA-256. Publishing then stopped at the required human approval. The rehearsal was deliberately cancelled there; no tag or release was created.
- **Remaining:** Review the recorded results. Actual releases must repeat these checks for their exact revision after the implementation branch is approved and merged.
- **Proof — open and look:**
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
  - [20,000 restored files, each compared with its original](../audit-dashboard/evidence/huge-scale/report.html) — See the source and restored file counts, the 2 GiB total, the full SHA-256 comparison result, and the limits on metadata preservation.
  - 🖼️ [The app after the 20,000-file restore](../audit-dashboard/evidence/huge-scale/E34-restore-long-include-list/screenshots/03-after-restore.png) — Backspace remains open after the real restore. Use the report for the file counts and content comparisons; the card alone cannot prove file integrity.
  - [Recorded test results and remaining gaps](../audit-dashboard/evidence/repair-verification.html) — Read the pass and fail results, open the original test records if needed, and see what each run did and did not test.
- **Release proof — open and look:**
  - 🖼️ [Successful checks waiting for human approval](../audit-dashboard/evidence/remaining-work-20261009/release-approval-waiting.png) — The Windows checks are green and the publishing job is waiting, with no publishing steps started.
  - [Release rehearsal results](../audit-dashboard/evidence/remaining-work-20261009/release-gate.html) — Exact revision, test counts, full file comparison, approval settings and deliberate cancellation.
- **Limitations / adversarial review:** Failed rehearsals blocked publication; the successful rehearsal reached approval and was not approved. The environment protects this publishing workflow, not manual releases made through other routes by trusted users with write access. This local-repository test does not establish live-cloud or reboot behavior.

- **Review comments received 2026-10-05 (B021):**
  > How can we best force ourselves to run this large test before pushing a release on github?

- **I need from you:** B021: Review the release rehearsal report and confirm whether the large-test requirement is satisfied.

### P0: Mount fails for the user's basic dist-folder backup

- **Raised:** 2026-06-11
- **Type:** bug + architectural decision needed
- **Status:** Open
- **Context:** R009: The real E04 mount now hard-fails on runtime errors and passes two-volume byte/read/unmount checks. The published application also passed its actual WinFSP loader check. The unavailable original user dist configuration is the remaining reproduction boundary.
- **Current result:** The real E04 mount now hard-fails on runtime errors and passes two-volume byte/read/unmount checks. The published application also passed its actual WinFSP loader check. The unavailable original user dist configuration is the remaining reproduction boundary.
- **Remaining:** If Mount still fails with your original dist-folder configuration, provide that reproduction context and its app log. Otherwise inspect the packaged-loader and real-mount evidence and decide whether to accept.
- **Proof — open and look:**
  - [Published command-line and native-loader checks](../audit-dashboard/evidence/packaged-vault-final/loader/report.html) — Nine final-executable CLI and native-loader checks passed, with expected exits, no configuration creation and PID-matched loader traces. Installed WinFSP and existing app extraction cache reused. Does not claim fresh-cache extraction, a GUI Mount click, or the user original repository.
  - [R009 · Carryover evidence and test scope](../audit-dashboard/evidence/carryovers/report.html) — Earlier carryover report: E04 proves native mount behavior on disposable data. The later packaged-loader report closes the dependency-discovery gap; the original user repository and a physical Explorer click remain unexercised.
- **Limitations / adversarial review:** Published loader proof establishes dependency discovery and native DLL loading; E04 separately establishes real readable-volume behavior. These complementary tests do not recreate the user's unavailable original config or a physical Explorer click.
- **I need from you:** R009: If Mount still fails with your original dist-folder configuration, provide that reproduction context and its app log. Otherwise inspect the packaged-loader and real-mount evidence and decide whether to accept.

### P0: Mount feature actually working (WinFSP-backed) — Awaiting validation

- **Raised:** 2026-06-12 (split from the original Mount investigation)
- **Type:** feature / architecture
- **Status:** Awaiting validation
- **Context:** R016: The native WinFSP E04 test passes: two different drive letters mount concurrently, the first volume's complete file list and bytes match its source, then unmounting it leaves the second drive readable.
- **Current result:** The native WinFSP E04 test passes: two different drive letters mount concurrently, the first volume's complete file list and bytes match its source, then unmounting it leaves the second drive readable.
- **Remaining:** Inspect the native mount assertions. The original packaged-app acceptance remains separately tracked as R009.
- **Proof — open and look:**
  - [R016 · Carryover evidence and test scope](../audit-dashboard/evidence/carryovers/report.html) — The actual native test result and assertions prove readable Windows volumes and independent mount lifetimes; no backup-card screenshot is presented as drive-content proof.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** This ran against local disposable repositories and the installed WinFSP runtime. It does not exercise cloud fetching, a machine without WinFSP, or native Explorer UI.
- **I need from you:** R016: Inspect the native mount assertions. The original packaged-app acceptance remains separately tracked as R009.

### P1: Combine review status and reply-turn filters without clearing either

- **Raised:** 2026-10-09
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** Choosing Needs your input and then Your review cleared the status filter and showed every issue awaiting the user. Status and whose turn it is must work together. Show both breakdowns so the user can find issues needing their decision while excluding issues already sent back to the agent.
- **Root cause + fix:** The status and turn click handlers reset each other's selection, category and search. Each now changes only its own filter. Counts reflect the other active filters, and removable tags show the complete combination. Empty matches stay empty.
- **Proof — open and look:**
  - 🖼️ [Needs your input and Your review selected together](../audit-dashboard/evidence/filter-combinations-20261009/laptop-1366.png) — Both filters remain selected on a 1366 × 768 viewport, showing three matching issues; the unanswered decision issue belongs to With Agent instead.
  - [Browser checks and filter behavior](../audit-dashboard/evidence/filter-combinations-20261009/report.html) — Exact click sequence, reverse order, search and category retention, empty intersections, closed issues and automated test results.
- **Limitations / adversarial review:** Actual Chrome interactions reproduced the user's starting sequence. The unanswered-comment case was explicitly excluded, and a zero-match combination did not broaden itself. Four filter regressions and the existing server persistence/discussion regression passed. Screenshot counts reflect the data at test time and will change as discussions progress. Existing user comments and decisions were preserved.
- **I need from you:** N013: Try Needs your input followed by Your review, and confirm whether the combined filters and counts match how you want to browse.

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

### P1: Recover review comments after a temporary file lock

- **Raised:** 2026-10-05
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** A browser check found that Dropbox briefly locked the review JSON while it was being replaced. The save failed, and the comment button stayed disabled even though the draft was still visible.
- **Current result:** A temporary Windows file lock could prevent a JSON save and leave the Post comment button disabled. Saves now retry brief file locks, and the form recovers after either success or failure.
- **Remaining:** Review the browser and persistence checks, then use the desk normally.
- **Proof — open and look:**
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
- **Limitations / adversarial review:** The server test holds a real Windows file lock while a save is pending. Permanent failures still report an error and preserve the existing file.

- **I need from you:** A029: Review the browser and persistence checks, then use the desk normally.

### P1: Show the engine's file errors in backup logs

- **Raised:** 2026-10-05
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** A real backup of the user's Dropbox placeholder returned a warning and no file content, but Backspace's saved log only said that some files might not have been read. Restic reported the concrete error: unsupported file type “irregular”. Preserve the engine's actual diagnostics so the user can tell which files were not backed up and why.
- **Current result:** Backspace now copies the engine’s original operation diagnostics into its saved run log. In the actual Dropbox test, the log now names the omitted file and includes Restic’s unsupported-file error instead of only saying that some files could not be read.
- **Remaining:** Open the real Dropbox run log. It identifies the file Restic could not back up and preserves the original engine error.
- **Proof — open and look:**
  - [Actual Dropbox test: no download and no backed-up contents](../audit-dashboard/evidence/cloud-placeholder-20261005/report.html) — See the file measurements, snapshot checks and original engine error. The supplied file remained online-only.
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
- **Limitations / adversarial review:** Tested with a real Dropbox placeholder, plus protocol tests for split text and failed log retrieval.

- **I need from you:** A027: Open the real Dropbox run log. It identifies the file Restic could not back up and preserves the original engine error.

### P1: Explain unsupported Dropbox placeholders without claiming they were backed up

- **Raised:** 2026-10-05
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** The supplied 124,333,386-byte Dropbox file uses an Offline reparse point that bundled Restic 0.18.1 reports as an unsupported irregular file. Backups with cloud-file exclusion both on and off omitted it and returned Warning. The file remained online-only with zero allocated bytes. Earlier source documentation asserted tested download behavior and a snapshot marker without evidence; correct that claim and expose the limitation plainly.
- **Current result:** The actual Dropbox file you supplied stayed online-only, with zero allocated bytes throughout the checks. Neither backup contained its contents. Bundled Restic 0.18.1 rejected this placeholder as an unsupported irregular file. The run returned Warning, and the app log now preserves the specific filename and error.
- **Remaining:** Review the corrected result. This issue records the warning and removes the false download claim; it does not claim support for that placeholder.
- **Proof — open and look:**
  - [Actual Dropbox test: no download and no backed-up contents](../audit-dashboard/evidence/cloud-placeholder-20261005/report.html) — See the file measurements, snapshot checks and original engine error. The supplied file remained online-only.
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
- **Limitations / adversarial review:** Actual backup support for this online-only file remains unresolved. B035 retains the broader testing work.

- **I need from you:** A028: Review the corrected result. This issue records the warning and removes the false download claim; it does not claim support for that placeholder.

### P1: Show incomplete production checks as still needing testing

- **Raised:** 2026-10-05
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** The review desk labels issues ready even when the proof describes incomplete testing. Review each issue against its actual evidence. Mock-only interfaces must remain marked as still needing testing, and the filter must find them.
- **Current result:** Issues with missing production checks are now listed under Still needs testing. Proof text distinguishes actual engine runs, actual interface tests with prepared data, and simulated external responses. Passing a mock test no longer makes the complete provider or Windows journey ready.
- **Remaining:** Use Still needs testing to see the outstanding checks. Each issue names what its proof covers and what remains.
- **Proof — open and look:**
  - 🖼️ [Outstanding production tests remain visible](../audit-dashboard/evidence/review-feedback/queues.png) — The Still needs testing list contains the missing production checks.
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
- **Limitations / adversarial review:** This is a correction of review status, not a claim that the outstanding tests have been completed.

- **I need from you:** N010: Use Still needs testing to see the outstanding checks. Each issue names what its proof covers and what remains.

### P1: Require current screenshots and documentation before a GitHub release

- **Raised:** 2026-10-05
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** The user wants a systematic check of screenshots, README and documentation before each GitHub release, and asks whether AGENTS.md is sufficient. Add a review issue and recommend enforceable release checks with a clear distinction between automated checks and human visual/content review.
- **Current result:** The protected environment requires the maintainer’s approval and disables administrator bypass. On 9 October, run 37926997445 at revision d344e0485d3db3931624cbd9d7aebaf3d813b3c9 passed 1,059 suite tests, the 20,000-file restore and the actual Windows updater bundle test. It then waited for human approval. The rehearsal was cancelled without approval, a tag or a release. Its temporary branch exception was removed; only main remains allowed.
- **Remaining:** Review the proof. Before an actual release, approve and merge the implementation branch, rerun the workflow on that exact main revision, and review its current documentation and screenshots.
- **Proof — open and look:**
  - 🖼️ [Successful checks waiting for human approval](../audit-dashboard/evidence/remaining-work-20261009/release-approval-waiting.png) — Green Windows checks lead to a waiting human-review job; publishing has not started.
  - [Release rehearsal results](../audit-dashboard/evidence/remaining-work-20261009/release-gate.html) — Test records, full file comparison, saved settings and the reason the overall run now says cancelled.
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
- **Limitations / adversarial review:** Earlier failing checks blocked publication. Successful checks then reached required approval, proving both paths. Human review of image and prose accuracy remains necessary. No publication was attempted. The environment protects this workflow; repository administrators and existing write permissions can still publish manually through another route.

- **Clean-run findings — 2026-10-09:** Earlier hosted runs exposed missing first-build engine copies, a 24-hour-only assertion, a hosted metered-network signal cancelling unrelated tests, and a version query that left Backrest's Windows tray running. These were repaired before the successful rehearsal. Production network detection and its dedicated cancellation tests remain enabled.

- **Packaging follow-up — 2026-10-09:** The fallback warning executable had the same first-build resource-list problem. Include it after it is generated, fail ordinary builds if generation produces no file, and make the release suite verify the actual embedded executable. Development builds can still explicitly skip it.

- **Engine-version finding — 2026-10-09:** The third hosted run passed the interface suite but failed restore with newly downloaded Backrest 1.14.1. Its request formats differ from the tested 1.13.0 engine. Both downloaders now read explicit version selections: Backrest 1.13.0, Restic 0.18.1 and rclone 1.74.1. Existing local binaries stay untouched. This prevents an unreviewed automatic upgrade; it does not claim compatibility with Backrest 1.14.1 or cryptographic verification of every download.

- **Follow-up — 2026-10-09:** Complete all release-gate implementation and available repository configuration without waiting for the user. Identify a concrete access or approval blocker before leaving work open.
- **I need from you:** N011: Review the waiting-approval screenshot and report, then confirm whether this release process meets your requirement. This does not ask you to publish a release now.

### P1: Show live output when a backup starts its first run

- **Raised:** 2026-10-04
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** If you opened a backup’s logs before it had ever run, starting its first operation could leave the empty message on screen. The LIVE badge could remain hidden too.
- **Root cause + fix (short):** The view now updates as soon as the first activity appears. It replaces the empty message with current output and one LIVE badge. When the operation is saved as history, the console shows that saved result without the LIVE badge.
- **Proof — open and look:**
  - 🖼️ **[The first run appears immediately](../audit-dashboard/evidence/revision-after/logs/07-first-activity-replaces-the-empty-hint.png)** — The empty message is gone, the first operation is selected, and one LIVE badge is visible.
  - 🖼️ **[The first saved result](../audit-dashboard/evidence/revision-after/logs/09-first-saved-result-replaces-the-empty-hint.png)** — The successful result remains available as history, with no LIVE badge.
  - **[Tests for the revised app](../audit-dashboard/evidence/design-checks.html)** — Read the results, the behaviors that were checked, and the limits of those checks.
- **Adversarial checks and limitations:** The test stays on the same backup throughout the transition. It also removes an unfinished sample activity without saving it and checks that the empty message returns. The activity is a test fixture rather than a real cloud backup.
- **I need from you:** Open the two screenshots to compare the first live run with its saved result.

### P1: Make review explanations understandable on first reading

- **Raised:** 2026-10-03
- **Type:** refactor
- **Status:** Awaiting validation
- **Context:** resume, and also I want you to rewrite ALL the text you wrote in the review desk (but don't touch my verbatim quotes) so it is much clearer to understand for a normal human. I am not a machine who can read super compressed sentences full of jargon (even if I am a veteran software engineer).
- **Root cause + fix (short):** All 86 existing issue titles, explanations, next steps and proof captions have been rewritten in clear sentences. The 21 supporting reports were rewritten too. New issues use the same style. Your original notes and follow-up quotes remain unchanged; raw test names and logs are still available when you want the technical details.
- **Proof — open and look:**
  - 🖼️ **[An issue in plain language](../audit-dashboard/evidence/review-assets/readable-issue.png)** — Your original quote appears first. Below it, the explanation describes what changed and what you can check.
  - **[Review text and saved-data checks](../audit-dashboard/evidence/review-assets/review-checks.html)** — Shows the quote comparison, report-link checks, browser reading sizes and persistence-test result.
- **Adversarial checks and limitations:** The complete notes page was compared with the original attachment and matches exactly. The report rewrite preserved all 308 existing links and 1,992 raw evidence blocks. Historical test names and diagnostic output remain verbatim so the results can still be checked.
- **I need from you:** Read any issue, including the three examples you mentioned. If a sentence still takes effort to understand, leave a comment on that issue.

### P1: Make dropdown controls visibly distinguishable from text fields

- **Raised:** 2026-10-03
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** Some dropdowns looked like ordinary text fields because their arrows were almost invisible. It was hard to tell that you could open a list of choices.
- **Root cause + fix (short):** The dropdown arrow now has its own small, visible shape. The control keeps its normal popup, keyboard navigation and saved selection behavior.
- **Proof — open and look:**
  - 🖼️ **[Visible dropdown arrows](../audit-dashboard/evidence/dropdown-repair/after-light.png)** — The Theme and Accent color controls now have clear arrows at their right edge.
  - **[Dropdown comparison and interaction checks](../audit-dashboard/evidence/dropdown-repair/report.html)** — Compare the old and revised controls at the same size, then inspect the open list and keyboard-selection evidence.
  - **[Tests for the revised app](../audit-dashboard/evidence/design-checks.html)** — Read the results, the behaviors that were checked, and the limits of those checks.
- **Adversarial checks and limitations:** The tests inspect the rendered arrow and use real Avalonia dropdown controls. They cover pointer opening and selection, keyboard navigation, Escape, disabled controls and saved choices in both themes.
- **I need from you:** Open the before-and-after comparison. Check that dropdowns now visibly offer a list of choices in both themes.

### P1: Fit consumer laptop work areas and Windows scaling

- **Raised:** 2026-10-03
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** What's the benefit of Before 1024 × 768; proposal 1240 × 820? Wasn't it supposed to be a minimum width? I just want to make sure the app will open fine on 99.9% of consumer laptops.
- **Root cause + fix (short):** The different screenshot sizes offered no benefit; they came from a change in the screenshot tool. Separately, the app required at least 1180 × 720 logical pixels and did not fit itself to the available screen. The main window now allows 920 × 540 and starts at 1180 × 760 when there is room. It shrinks to the monitor’s usable area when Windows scaling or the taskbar leaves less space. The editors also fit themselves to that area.
- **Proof — open and look:**
  - 🖼️ **[Small window, before scrolling](../audit-dashboard/evidence/revision-after/laptop/01-minimum-920x540-initial-overview.png)** — 920 × 540 logical pixels. This is the initial view of the backup card.
  - 🖼️ **[Small window, after scrolling](../audit-dashboard/evidence/revision-after/laptop/03-minimum-920x540-backups.png)** — The action buttons are reachable and stay within the card.
  - 🖼️ **[Space available on a scaled laptop](../audit-dashboard/evidence/revision-after/laptop/04-1366x768-at125-percent-initial-overview.png)** — 1076 × 558 logical pixels, representing a 1366 × 768 display at 125% scaling with taskbar space reserved.
  - 🖼️ **[Actions on that scaled layout](../audit-dashboard/evidence/revision-after/laptop/06-1366x768-at125-percent-backups.png)** — The same card after scrolling, with every action inside the page margins.
  - **[Tests for the revised app](../audit-dashboard/evidence/design-checks.html)** — Read the results, the behaviors that were checked, and the limits of those checks.
- **Adversarial checks and limitations:** The tests cover 920 × 540, 1076 × 558 and 1264 × 672 logical-pixel layouts, short editor windows, and several scaling calculations. They also simulate moving between monitors and restoring a maximized window. They do not establish a percentage of all laptops or replace a check on a physical multi-monitor setup. The review caught and fixed clipped card actions, inconsistent gutters, and an early window event that could overwrite the requested startup size. Tests now cover these failures as well as the intended layouts.
- **I need from you:** Open the small-window screenshots and try the safe preview on your laptop. Tell me if a control is hard to reach at your usual Windows scaling setting.

### P1: Generated scheduled action silently skips backups

- **Raised:** 2026-10-03
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** A021: The former exact scheduled task action silently exited0 without launching a backup. Replaced malformed cmd quoting with an absolute stock Windows PowerShell encoded literal-data action, correct native argv quoting, stable system working directory, missing-main fallback, child wait and exact exit-code propagation. Task identities/policies unchanged.
- **Current result:** The former exact scheduled task action silently exited0 without launching a backup. Replaced malformed cmd quoting with an absolute stock Windows PowerShell encoded literal-data action, correct native argv quoting, stable system working directory, missing-main fallback, child wait and exact exit-code propagation. Task identities/policies unchanged.
- **Remaining:** Review the linked evidence and accept the item, or describe what still needs adjustment.
- **Proof — open and look:**
  - [Scheduler, guard and parser regression results](../audit-dashboard/evidence/infrastructure-tests/runtime-coverage-report.html) — 57 passed, 0 failed, 0 skipped: 44 guard/parser plus13 action regressions
  - [Actual generated scheduled-action execution](../audit-dashboard/evidence/packaged-task-action-final/report.html) — 25 actual processes, 46 assertions, all passed; generated action invokes the final packaged executable Compiled production action builder launches actual published binary plus harmless WinExe fixtures; no OS task registration.
- **Limitations / adversarial review:** Original failure retained. 13 focused action/native-parser tests and actual25process/46assertion proof passed; real published backup adds exactly1snapshot with bothfileSHA256 matches. Delayed WinExe, hostile names, exit23, missing-main17, missing-both1 and published missing-backup2 are asserted.
- **I need from you:** A021: Review the linked evidence and accept the item, or describe what still needs adjustment.

### P1: Unattended runs bypass unreadable-vault validation

- **Raised:** 2026-10-03
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** A022: Unattended backup, prune and check now validate saved credentials before operations. Corrupt-vault reads keep the encrypted bytes in place so later launches repeat the warning; an explicit credential save preserves a separate recovery copy before replacement. GUI startup shows the current preserved-file warning.
- **Current result:** Unattended backup, prune and check now validate saved credentials before operations. Corrupt-vault reads keep the encrypted bytes in place so later launches repeat the warning; an explicit credential save preserves a separate recovery copy before replacement. GUI startup shows the current preserved-file warning.
- **Remaining:** Review the linked evidence and accept the item, or describe what still needs adjustment.
- **Proof — open and look:**
  - 🖼️ [Unreadable credentials remain explained on later launches](../audit-dashboard/evidence/final-persistence/unreadable-vault-warning.png) — The actual warning names the original secrets.bin as preserved; OK closes it without changing ciphertext. A fresh store also raises the same warning.
  - [Final packaged app: saved-credential validation](../audit-dashboard/evidence/packaged-vault-final/report.html) — 12 public CLI launches and 145 assertions passed, plus nine loader/argument checks. Each backup, prune and check entry point rejects the same unreadable ciphertext on two launches without changing its bytes or starting an operation. Real current-user DPAPI empty-map and absent-vault negatives proceed to normal missing-backup handling. Synthetic disposable vaults; no personal credential recovery, live cloud account, registered task or reboot is claimed.
  - [Final credential and transcript safety proof](../audit-dashboard/evidence/final-persistence/report.html) — 38 passing checks: actual unattended entry points, repeat-launch preservation, locked vault, complete distinct transcripts, and selective retention.
- **Limitations / adversarial review:** Tests invoke the actual production unattended entry points in-process; separately launched packaged-process coverage remains distinct. Re-entering credentials uses the existing recovery flow and does not recover foreign-account DPAPI contents.
- **I need from you:** A022: Review the linked evidence and accept the item, or describe what still needs adjustment.

### P1: Parallel operations can collide on the same transcript filename

- **Raised:** 2026-10-03
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** A023: Backup-cell, restore and verification transcripts now receive unique filename identities. Fast same-basename cells retain separate complete logs and correct history links; concurrent writers at an identical timestamp cannot overwrite one another.
- **Current result:** Backup-cell, restore and verification transcripts now receive unique filename identities. Fast same-basename cells retain separate complete logs and correct history links; concurrent writers at an identical timestamp cannot overwrite one another.
- **Remaining:** Review the linked evidence and accept the item, or describe what still needs adjustment.
- **Proof — open and look:**
  - [Final credential and transcript safety proof](../audit-dashboard/evidence/final-persistence/report.html) — 38 passing checks: actual unattended entry points, repeat-launch preservation, locked vault, complete distinct transcripts, and selective retention.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** Cells inside one backup run sequentially. The integration test follows that policy; a separate test runs 100 simultaneous writers per label at the same timestamp. Earlier incorrect parallel-cell fixture failure is retained in the report.
- **I need from you:** A023: Review the linked evidence and accept the item, or describe what still needs adjustment.

### P1: Switching editor modes can change a backup even after Cancel

- **Raised:** 2026-10-03
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** A020: The backup editor now owns a deep draft, so switching Easy/Advanced cannot alter the live configuration before Save. Easy mode preserves existing storage names, multiple storages on one destination and per-target thread overrides instead of silently deriving replacement targets.
- **Current result:** The backup editor now owns a deep draft, so switching Easy/Advanced cannot alter the live configuration before Save. Easy mode preserves existing storage names, multiple storages on one destination and per-target thread overrides instead of silently deriving replacement targets.
- **Remaining:** Inspect the preserved custom rules and passing Cancel/Save/storage-identity checks, then confirm the behavior.
- **Proof — open and look:**
  - 🖼️ [The mode round trip preserves the editable draft](../audit-dashboard/evidence/carryovers/editor-mode-round-trip.png) — The user-edited name and custom rule remain after real Easy/Advanced toggle clicks. Tests additionally assert Cancel leaves the original live record untouched.
  - [Current interaction and carryover evidence](../audit-dashboard/evidence/carryovers/report.html) — Exact passing test results, actual pointer/animation behavior and negative controls are documented alongside remaining integration boundaries.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** S56 uses real windows and pointer clicks but deliberately avoids OS task registration. The normal SaveBackupEdit boundary is invoked after the actual Save button returns the draft, matching the caller's persistence step; the test then loads a fresh ConfigStore. The real E05/E06/E23 backup/restore scenarios passed in the same 36-case subset. They use isolated disposable local repositories, not the user's existing repositories.
- **I need from you:** A020: Inspect the preserved custom rules and passing Cancel/Save/storage-identity checks, then confirm the behavior.

### P1: Integrity Check invoked snapshot indexing instead of repository checking

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** A018: The integrity-check enum now sends wire value3; the old value1 requested only snapshot indexing. E15 now requires a real OperationCheck terminal success, and E14 requires real successful forget operations and exactly one retained revision.
- **Current result:** The integrity-check enum now sends wire value3; the old value1 requested only snapshot indexing. E15 now requires a real OperationCheck terminal success, and E14 requires real successful forget operations and exactly one retained revision.
- **Remaining:** Review the linked evidence and accept the item, or describe what still needs adjustment.
- **Proof — open and look:**
  - 🖼️ [App after an actual integrity check](../audit-dashboard/evidence/restore-repairs/screenshots/real-integrity-check.png) — The final E15 run reports success. Its recorded OperationCheck #4 reached STATUS_SUCCESS; snapshot indexing cannot satisfy that assertion. Open the rendered report for the operation proof.
  - 🖼️ [App after real retention enforcement](../audit-dashboard/evidence/restore-repairs/screenshots/real-retention-one-revision.png) — Three backups with keep-last 1 produced six successful Forget operations and exactly one retained revision. The final retained snapshot was restored and fully compared.
  - [Restore safety evidence](../audit-dashboard/evidence/restore-repairs/report.html) — Open exact safety assertions, real E11 counts and session screenshots.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** Tests use disposable data. Review the linked report scope before accepting the item.
- **I need from you:** A018: Review the linked evidence and accept the item, or describe what still needs adjustment.

### P1: Concurrent GUI and scheduled runs can lose or hide activity history

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** A017: Cross-process run history updates retain both writers. Opening Backups or Activity and returning focus refreshes external configuration/history without polling; unsaved editor drafts remain separate.
- **Current result:** Cross-process run history updates retain both writers. Opening Backups or Activity and returning focus refreshes external configuration/history without polling; unsaved editor drafts remain separate.
- **Remaining:** Review the external-run navigation proof and confirm the resulting history display.
- **Proof — open and look:**
  - 🖼️ [Returning to Backups shows the external scheduled failure](../audit-dashboard/evidence/cloud-repairs/external-scheduled-backup-card.png) — The real main window's card and health banner show the failure written through an independent config/history instance while this GUI was already open.
  - 🖼️ [The exact external run and saved log are available](../audit-dashboard/evidence/cloud-repairs/external-scheduled-history.png) — After navigating to Activity & logs, the failed external run is selected and its saved two-line failure output is visible.
  - [Final infrastructure checks](../audit-dashboard/evidence/infrastructure-tests/final-report.html) — 50 selected tests passed; broad run933passed2credentialskips0failed
  - [Infrastructure repair evidence](../audit-dashboard/evidence/infrastructure-tests/report.html) — 86 passed, 0 failed, 0 skipped
- **Limitations / adversarial review:** The external writer is a second ConfigStore/LogStore instance against disposable data; real scheduled process execution across reboot remains B040.
- **I need from you:** A017: Review the external-run navigation proof and confirm the resulting history display.

### P1: Headless test session degrades ~70s into combined suite runs

- **Raised:** 2026-07-05 (found while validating the hover refinements)
- **Type:** bug (test infrastructure + two real product leaks)
- **Status:** Awaiting validation
- **Context:** A014: Scenario cleanup is part of the recorded outcome. Reports say STEPS COMPLETED, and the actual test-runner result remains authoritative. Every screenshot is compared immediately after it is rendered, so stale previous-run images cannot silently satisfy visual checks. The UI driver now invokes registered dialog Click handlers instead of silently accepting a missing reflection shim, and refuses disabled actions.
- **Current result:** Scenario cleanup is part of the recorded outcome. Reports say STEPS COMPLETED, and the actual test-runner result remains authoritative. Every screenshot is compared immediately after it is rendered, so stale previous-run images cannot silently satisfy visual checks. The UI driver now invokes registered dialog Click handlers instead of silently accepting a missing reflection shim, and refuses disabled actions.
- **Remaining:** Review the linked evidence and accept the item, or describe what still needs adjustment.
- **Proof — open and look:**
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** A scenario page alone is never evidence that the entire suite passed. Open the final test-runner report too.
- **I need from you:** A014: Review the linked evidence and accept the item, or describe what still needs adjustment.

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

### P1: Filters > Advanced > Simulate fails with exit status 1

- **Raised:** 2026-07-03
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** R002: Simulate now walks local source files using the filter evaluator. S40 opens Filters > Advanced, invokes the Simulate button, checks included and excluded paths, checks that excluded directories are not descended, and rejects the former error output.
- **Current result:** Simulate now walks local source files using the filter evaluator. S40 opens Filters > Advanced, invokes the Simulate button, checks included and excluded paths, checks that excluded directories are not descended, and rejects the former error output.
- **Remaining:** Inspect the populated simulation output.
- **Proof — open and look:**
  - 🖼️ [Simulate produces include/exclude rows](../audit-dashboard/evidence/carryovers/filter-simulation.png) — The real Advanced filter editor shows *.tmp and cache/ on the left, with one included row and two excluded rows on the right.
  - [R002 · Carryover evidence and test scope](../audit-dashboard/evidence/carryovers/report.html) — Assertions cover a narrow fixture, no filters, specific filters and a nonexistent source; the button path runs without the old RunCommand error.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** This deliberately previews the local filesystem and filter rules; it is not a restic dry-run or a cloud connection check. Long paths scroll horizontally in the shown output pane.
- **I need from you:** R002: Inspect the populated simulation output.

### P1: Status text next to "Apply recommended exclusions" is cropped

- **Raised:** 2026-07-03
- **Type:** bug (visual)
- **Status:** Awaiting validation
- **Context:** R003: The reported recommended-savings message wraps beside Apply at 980 × 820. S50 asserts at least two lines and a finite measured width; the screenshot shows the full sentence within the card.
- **Current result:** The reported recommended-savings message wraps beside Apply at 980 × 820. S50 asserts at least two lines and a finite measured width; the screenshot shows the full sentence within the card.
- **Remaining:** Inspect the demonstrated savings-row fix and confirm the layout. Finite display-size and dynamic-string limits remain disclosed rather than being treated as an unfinishable acceptance condition.
- **Proof — open and look:**
  - 🖼️ [Savings message wraps beside Apply](../audit-dashboard/evidence/carryovers/savings-wrap-current.png) — The complete blue zero-match message occupies two lines and stays inside the card; the corrected rule-order prose above says custom rules precede recommended rules.
  - [R003 · Carryover evidence and test scope](../audit-dashboard/evidence/carryovers/report.html) — Bounds and multi-line assertions passed; this is not a blanket no-clipping claim.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** No finite screenshot set proves every dynamic string, localization or display scale. The final screenshot includes the subsequently corrected two-sentence rule-order copy.
- **I need from you:** R003: Inspect the demonstrated savings-row fix and confirm the layout. Finite display-size and dynamic-string limits remain disclosed rather than being treated as an unfinishable acceptance condition.

### P1: Estimated savings next to "Apply recommended exclusions" shows 0 B

- **Raised:** 2026-06-11
- **Type:** UX bug (the math was right, the message was wrong)
- **Status:** Awaiting validation
- **Context:** R010: The savings label distinguishes no matching exclusions, unreadable/empty input, and positive estimated savings. S34 uses real filesystem fixtures and asserts all three model labels; the zero-match message is rendered in S50.
- **Current result:** The savings label distinguishes no matching exclusions, unreadable/empty input, and positive estimated savings. S34 uses real filesystem fixtures and asserts all three model labels; the zero-match message is rendered in S50.
- **Remaining:** Inspect the zero-match explanation and decide whether it communicates the result clearly.
- **Proof — open and look:**
  - 🖼️ [Zero matches have an explanation](../audit-dashboard/evidence/carryovers/savings-wrap-current.png) — The blue message explains that no files matched and that this is normal for narrow sources, instead of showing Estimated savings: 0 B.
  - [R010 · Carryover evidence and test scope](../audit-dashboard/evidence/carryovers/report.html) — Narrow, empty and cache-heavy real filesystem fixtures produce different expected label states.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** S34 does not render all three label states. Positive savings and unreadable/empty wording are model assertions; the screenshot covers only the originally confusing zero-match state.
- **I need from you:** R010: Inspect the zero-match explanation and decide whether it communicates the result clearly.

### P1: Skipped-run alerts block visibility logic

- **Raised:** 2026-06-11 (user asked 2026-07-03 to confirm the Easy-mode
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** B027: In the backup settings (Easy Mode), there should be a grace period number of days selector setting to ignore skipped backups. During this grace period, no notifications should be sent if skipped; also add a button to restart the grace period (useful if user is offline for 2 weeks and grace period is 7 days). After grace period, skipped with warning notifications would be fired as normal. These are not as bad as real failure so should clearly be indicated as such (if we fired Failed just because we're offline, user would be unrightly worried, but of course after some time it is important user decides if it is normal backups are always skipped).  B028: In the backup settings (Easy Mode), there should be a grace period number of days selector setting to ignore skipped backups. During this grace period, no notifications should be sent if skipped; also add a button to restart the grace period (useful if user is offline for 2 weeks and grace period is 7 days). After grace period, skipped with warning notifications would be fired as normal. These are not as bad as real failure so should clearly be indicated as such (if we fired Failed just because we're offline, user would be unrightly worried, but of course after some time it is important user decides if it is normal backups are always skipped).
- **Current result:** Skip grace suppresses human alerts; expiration emits warning severity without promoting the stored run to Failed. Reset suppresses subsequent skip alerts. Share access probes preserve permission/missing-path failures and distinguish known outages. Relocation guidance is conditional, requires previous success and a local destination, and excludes deliberate cancellation/metered stops. Easy mode now always exposes the selector and Reset on its schedule step, including before any skipped run and after expiry. Its old Failed-escalation wording is corrected. The real wizard Save/reopen scenario confirms the 14-day choice/reset persist without faking a successful run. Skip grace suppresses human alerts; expiration emits warning severity without promoting the stored run to Failed. Reset suppresses subsequent skip alerts. Share access probes preserve permission/missing-path failures and distinguish known outages. Relocation guidance is conditional, requires previous success and a local destination, and excludes deliberate cancellation/metered stops. Easy mode now always exposes the selector and Reset on its schedule step, including before any skipped run and after expiry. Its old Failed-escalation wording is corrected. The real wizard Save/reopen scenario confirms the 14-day choice/reset persist without faking a successful run.
- **Remaining:** Review the report and listed limitations; accept the issue if this proof matches your requirement. No credentials or external messages were used.
- **Proof — open and look:**
  - 🖼️ [Easy grace controls before a holiday](../audit-dashboard/evidence/cloud-repairs/easy-grace-before-holiday.png) — Healthy backup shows 14-day selector, Reset and warning-only explanatory copy before any skipped run.
  - 🖼️ [Grace values survive Save and reopening](../audit-dashboard/evidence/cloud-repairs/easy-grace-reopened.png) — The real wizard Save handler persisted the selected 14 days and manual reset, which remain visible when reopened.
  - [Grace, reset and real orchestration checks](../audit-dashboard/evidence/cloud-repairs/report.html) — Expired-grace absent drive remains Skipped on disk; a nonexistent share fails during grace, while warning notification/reset behavior is exercised through SendAlertsAsync.
  - [Fresh Easy UI, notification and cleanup checks](../audit-dashboard/evidence/cloud-repairs/followup-report.html) — 36 matching passing followup results are listed, with the complete broad-run failures disclosed separately.
- **Limitations / adversarial review:** Actual VPN/router/disconnected-NAS behavior is represented by known error codes and local fixtures; no physical network outage was induced.
- **I need from you:** B027: Review the report and listed limitations; accept the issue if this proof matches your requirement. No credentials or external messages were used. B028: Review the report and listed limitations; accept the issue if this proof matches your requirement. No credentials or external messages were used.

### P1: Collapsible-section hover styling has weird stripe

- **Raised:** 2026-06-11. **Re-opened 2026-07-03:** user reports the
- **Type:** bug (visual regression)
- **Status:** Awaiting validation
- **Context:** R011: S55 moves the real headless pointer over Settings headers with the production transition enabled in both light and dark themes. It asserts intermediate alpha frames retain the theme's neutral RGB, reaches the final rounded wash, extends left of the icon and returns to transparent after the pointer leaves.
- **Current result:** S55 moves the real headless pointer over Settings headers with the production transition enabled in both light and dark themes. It asserts intermediate alpha frames retain the theme's neutral RGB, reaches the final rounded wash, extends left of the icon and returns to transparent after the pointer leaves.
- **Remaining:** Inspect the dark intermediate and settled frames, then confirm the hover appearance.
- **Proof — open and look:**
  - 🖼️ [Dark hover during the live transition](../audit-dashboard/evidence/carryovers/dark-hover-during-transition.png) — At the sampled intermediate frame, the wash is a dark neutral hue rather than a white flash; the test recorded alpha 155/255.
  - 🖼️ [Dark hover settles without a stripe](../audit-dashboard/evidence/carryovers/dark-hover-settled.png) — The final rounded wash extends left of Window behavior's icon; the chevron has no independent dark block.
  - 🖼️ [Light hover during the same enabled transition](../audit-dashboard/evidence/carryovers/light-hover-during-transition.png) — The sampled light-theme wash has alpha 137/255; both themes assert neutral RGB throughout sampled frames.
  - [Current interaction and carryover evidence](../audit-dashboard/evidence/carryovers/report.html) — Exact passing test results, actual pointer/animation behavior and negative controls are documented alongside remaining integration boundaries.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** Frames are sampled in headless Avalonia with the real transition and pointer events. This is substantially stronger than the old transition-disabled still, but native display scaling and compositor timing can still differ.
- **I need from you:** R011: Inspect the dark intermediate and settled frames, then confirm the hover appearance.

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

### P1: A scheduled run can overwrite newer GUI configuration edits

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** A004: ConfigStore serializes file transactions across processes, reloads latest state inside every write and rejects unloaded/stale write paths. Editor drafts merge current runtime and notification results before saving.
- **Current result:** ConfigStore serializes file transactions across processes, reloads latest state inside every write and rejects unloaded/stale write paths. Editor drafts merge current runtime and notification results before saving.
- **Remaining:** Review the linked evidence and accept the item, or describe what still needs adjustment.
- **Proof — open and look:**
  - [Final infrastructure checks](../audit-dashboard/evidence/infrastructure-tests/final-report.html) — 50 selected tests passed; broad run933passed2credentialskips0failed
  - [Infrastructure repair evidence](../audit-dashboard/evidence/infrastructure-tests/report.html) — 86 passed, 0 failed, 0 skipped
- **Limitations / adversarial review:** Independent and concurrent stale instances retain disjoint edits; stale Save preserves newer state; failed writes roll back memory; stale editor retains new background completion.
- **I need from you:** A004: Review the linked evidence and accept the item, or describe what still needs adjustment.

### P1: Independent cached secret writers can drop newly saved keys

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** A005: SecretsStore rereads the latest vault under a shared file transaction; transient access/IO failures propagate without renaming the vault or treating it as empty. Real corruption is preserved before replacement. Startup explicitly validates the lazy vault off the UI thread before warning; temporary access failures receive a distinct retry message.
- **Current result:** SecretsStore rereads the latest vault under a shared file transaction; transient access/IO failures propagate without renaming the vault or treating it as empty. Real corruption is preserved before replacement. Startup explicitly validates the lazy vault off the UI thread before warning; temporary access failures receive a distinct retry message.
- **Remaining:** Review the linked evidence and accept the item, or describe what still needs adjustment.
- **Proof — open and look:**
  - [Final infrastructure checks](../audit-dashboard/evidence/infrastructure-tests/final-report.html) — 50 selected tests passed; broad run933passed2credentialskips0failed
  - [Infrastructure repair evidence](../audit-dashboard/evidence/infrastructure-tests/report.html) — 86 passed, 0 failed, 0 skipped
- **Limitations / adversarial review:** Final passing tests cover real exclusive file locks with intact bytes and retry, concurrent writers, corrupt-vault preservation before any prior read, and actual warning-dialog acknowledgement with unchanged preserved ciphertext.
- **I need from you:** A005: Review the linked evidence and accept the item, or describe what still needs adjustment.

### P1: Temporary secrets-file lock can cause a later save to discard existing credentials

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** A006: Unattended backup, prune and check now validate saved credentials before operations. Corrupt-vault reads keep the encrypted bytes in place so later launches repeat the warning; an explicit credential save preserves a separate recovery copy before replacement. GUI startup shows the current preserved-file warning.
- **Current result:** Unattended backup, prune and check now validate saved credentials before operations. Corrupt-vault reads keep the encrypted bytes in place so later launches repeat the warning; an explicit credential save preserves a separate recovery copy before replacement. GUI startup shows the current preserved-file warning.
- **Remaining:** Review the linked evidence and accept the item, or describe what still needs adjustment.
- **Proof — open and look:**
  - 🖼️ [Unreadable credentials are explained at startup](../audit-dashboard/evidence/infrastructure-tests/secrets-startup-warning.png) — The real warning names the preserved encrypted file; OK closes it without modifying the preserved bytes.
  - 🖼️ [Unreadable credentials remain explained on later launches](../audit-dashboard/evidence/final-persistence/unreadable-vault-warning.png) — The actual warning names the original secrets.bin as preserved; OK closes it without changing ciphertext. A fresh store also raises the same warning.
  - [Final packaged app: saved-credential validation](../audit-dashboard/evidence/packaged-vault-final/report.html) — 12 public CLI launches and 145 assertions passed, plus nine loader/argument checks. Each backup, prune and check entry point rejects the same unreadable ciphertext on two launches without changing its bytes or starting an operation. Real current-user DPAPI empty-map and absent-vault negatives proceed to normal missing-backup handling. Synthetic disposable vaults; no personal credential recovery, live cloud account, registered task or reboot is claimed.
  - [Final infrastructure checks](../audit-dashboard/evidence/infrastructure-tests/final-report.html) — 50 selected tests passed; broad run933passed2credentialskips0failed
- **Limitations / adversarial review:** Tests invoke the actual production unattended entry points in-process; separately launched packaged-process coverage remains distinct. Re-entering credentials uses the existing recovery flow and does not recover foreign-account DPAPI contents.
- **I need from you:** A006: Review the linked evidence and accept the item, or describe what still needs adjustment.

### P1: Starting another sidecar can terminate a live Backspace session engine

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** A007: Backrest data directories use exclusive lifetime leases. Startup does not kill daemons by executable path. Process cleanup requires persisted owner and child PID/start-time/binary identities, confirmed dead/reused owner, and exact live child. Unknown/legacy identity is left alone. Headless and elevated runs get separate data directories. Child job objects no longer allow silent breakaway.
- **Current result:** Backrest data directories use exclusive lifetime leases. Startup does not kill daemons by executable path. Process cleanup requires persisted owner and child PID/start-time/binary identities, confirmed dead/reused owner, and exact live child. Unknown/legacy identity is left alone. Headless and elevated runs get separate data directories. Child job objects no longer allow silent breakaway.
- **Remaining:** Review the linked evidence and accept the item, or describe what still needs adjustment.
- **Proof — open and look:**
  - [Final infrastructure checks](../audit-dashboard/evidence/infrastructure-tests/final-report.html) — 50 selected tests passed; broad run933passed2credentialskips0failed
  - [Infrastructure repair evidence](../audit-dashboard/evidence/infrastructure-tests/report.html) — 86 passed, 0 failed, 0 skipped
- **Limitations / adversarial review:** Exact identity negative matrices passed; a real two-daemon run passed and same-directory launch was rejected without stopping the first daemon. Unknown engine outcomes now retain their real OS repository gate through logical drain until confirmed owned-engine disposal; both reader and writer negatives passed.
- **I need from you:** A007: Review the linked evidence and accept the item, or describe what still needs adjustment.

### P1: Failed or cancelled backend restores can be reported as successful

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** A009: Backend terminal errors and cancellations remain truthful. Recovered files are counted from this operation’s staged output; raw restic directory counts cannot inflate leaf totals. E11 recovers 8 regular entries and reports its one unsupported junction as a warning.
- **Current result:** Backend terminal errors and cancellations remain truthful. Recovered files are counted from this operation’s staged output; raw restic directory counts cannot inflate leaf totals. E11 recovers 8 regular entries and reports its one unsupported junction as a warning.
- **Remaining:** Review the linked evidence and accept the item, or describe what still needs adjustment.
- **Proof — open and look:**
  - 🖼️ [Success and warning remain distinct](../audit-dashboard/evidence/restore-repairs/screenshots/sessions-results-retained.png) — Two retained results show 50/50 complete and 79/80 needing attention; warning is not labelled success. The real E11 accounting result is documented in the report.
  - 🖼️ [Warning summary shows actual counts](../audit-dashboard/evidence/restore-repairs/screenshots/summary-needs-attention.png) — The latest S16 screenshot shows Restore needs attention, 1234 total, 1228 restored, 6 failed, and all six recorded reasons. The same test asserts Error and Cancelled never display Restore complete.
  - 🖼️ [Successful summary remains distinct](../audit-dashboard/evidence/restore-repairs/screenshots/summary-success.png) — The latest S16 success screenshot shows Restore complete, all 1234 restored and zero failures, using the production outcome-to-summary mapping.
  - [Restore safety evidence](../audit-dashboard/evidence/restore-repairs/report.html) — Open exact safety assertions, real E11 counts and session screenshots.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** Tests use disposable data. Review the linked report scope before accepting the item.
- **I need from you:** A009: Review the linked evidence and accept the item, or describe what still needs adjustment.

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
- **Context:** A011: Synchronous Backrest requests are monitored while pending; cancellation explicitly stops the owned daemon operation and awaits terminal acknowledgement. No five-minute operation deadline remains. Quiet JSON removes duration-based log growth; progress is indeterminate until authoritative counts arrive.
- **Current result:** Synchronous Backrest requests are monitored while pending; cancellation explicitly stops the owned daemon operation and awaits terminal acknowledgement. No five-minute operation deadline remains. Quiet JSON removes duration-based log growth; progress is indeterminate until authoritative counts arrive.
- **Remaining:** Review the linked evidence and accept the item, or describe what still needs adjustment.
- **Proof — open and look:**
  - [Restore safety evidence](../audit-dashboard/evidence/restore-repairs/report.html) — Open exact safety assertions, real E11 counts and session screenshots.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** Tests use disposable data. Review the linked report scope before accepting the item.
- **I need from you:** A011: Review the linked evidence and accept the item, or describe what still needs adjustment.

### P1: Password rotation force-removes repository locks without exclusive ownership

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** A012: Password rotation holds an exclusive maintenance lease, never force-unlocks repositories, retains encrypted recovery material until the new credential is durably saved, and prevents closing the dialog during mutation.
- **Current result:** Password rotation holds an exclusive maintenance lease, never force-unlocks repositories, retains encrypted recovery material until the new credential is durably saved, and prevents closing the dialog during mutation.
- **Remaining:** Review the linked evidence and accept the item, or describe what still needs adjustment.
- **Proof — open and look:**
  - 🖼️ [Real repository after password rotation](../audit-dashboard/evidence/restore-repairs/screenshots/real-password-rotation.png) — The app remains usable after rotation. Open the evidence report for the actual old-password rejection, new-password acceptance, and six-file pre-rotation snapshot byte checks; the screen alone does not prove encryption.
  - 🖼️ [Closing waits for safe password persistence](../audit-dashboard/evidence/restore-repairs/screenshots/password-close-waits-for-save.png) — The latest S52 scenario actually requests window close during a gated password mutation. The dialog stays open, disables both actions and explains it will finish saving first; only the completed mutation permits closing.
  - [Restore safety evidence](../audit-dashboard/evidence/restore-repairs/report.html) — Open exact safety assertions, real E11 counts and session screenshots.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** Tests use disposable data. Review the linked report scope before accepting the item.
- **I need from you:** A012: Review the linked evidence and accept the item, or describe what still needs adjustment.

### P1: Destructive maintenance does not wait for active restores or verification

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** A013: Backups, restores, verification and mount reads hold shared operation leases; destructive maintenance requires exclusivity. Unknown daemon completion retains the cross-process operating-system lock until owned-engine shutdown is confirmed.
- **Current result:** Backups, restores, verification and mount reads hold shared operation leases; destructive maintenance requires exclusivity. Unknown daemon completion retains the cross-process operating-system lock until owned-engine shutdown is confirmed.
- **Remaining:** Review reader/writer, cancellation, shutdown and unknown-engine negative checks.
- **Proof — open and look:**
  - 🖼️ [Real Windows snapshot mounted](../audit-dashboard/evidence/restore-repairs/screenshots/real-two-mounts-first-drive.png) — The app shows a real snapshot mounted at Z:. The rendered report records two-drive independence and byte checks that cannot be established from the screenshot alone.
  - [Restore safety evidence](../audit-dashboard/evidence/restore-repairs/report.html) — Open exact safety assertions, real E11 counts and session screenshots.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** An unresponsive engine fails closed rather than permitting maintenance. The separate real elevated VSS acceptance remains B004.
- **I need from you:** A013: Review reader/writer, cancellation, shutdown and unknown-engine negative checks.

### P1: Scenario summary can show PASS while xUnit fails the same scenario

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** A014: Scenario cleanup is part of the recorded outcome. Reports say STEPS COMPLETED, and the actual test-runner result remains authoritative. Every screenshot is compared immediately after it is rendered, so stale previous-run images cannot silently satisfy visual checks. The UI driver now invokes registered dialog Click handlers instead of silently accepting a missing reflection shim, and refuses disabled actions.
- **Current result:** Scenario reports identify completed steps separately from the final test result. The latest full suite passed 1,016 checks with two credential skips. Shutdown scenarios now restore their shared state, and the off-thread UI test starts the actual headless UI before scheduling work.
- **Remaining:** Review the test-report checks and the final test-runner results together.
- **Proof — open and look:**
  - [Latest suite and retained earlier failures](../audit-dashboard/evidence/review-feedback/report.html) — 1,016 passed, 2 credential skips, 0 failed; earlier failed runs remain visible with their causes.
  - [Recorded test results and remaining gaps](../audit-dashboard/evidence/repair-verification.html) — Read the pass and fail results, open the original test records if needed, and see what each run did and did not test.
- **Limitations / adversarial review:** The earlier headless startup failure happened before the scenario body. The focused initialization and shutdown checks and the subsequent full suite passed, but a single passing run is not proof that every possible test-order race has been eliminated.

- **I need from you:** A014: Review the test-report checks and the final test-runner results together.

### P1: Edge-case restore reports zero restored files despite materializing every regular file

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** A015: Backend terminal errors and cancellations remain truthful. Recovered files are counted from this operation’s staged output; raw restic directory counts cannot inflate leaf totals. E11 recovers 8 regular entries and reports its one unsupported junction as a warning.
- **Current result:** Backend terminal errors and cancellations remain truthful. Recovered files are counted from this operation’s staged output; raw restic directory counts cannot inflate leaf totals. E11 recovers 8 regular entries and reports its one unsupported junction as a warning.
- **Remaining:** Review the linked evidence and accept the item, or describe what still needs adjustment.
- **Proof — open and look:**
  - 🖼️ [Success and warning remain distinct](../audit-dashboard/evidence/restore-repairs/screenshots/sessions-results-retained.png) — Two retained results show 50/50 complete and 79/80 needing attention; warning is not labelled success. The real E11 accounting result is documented in the report.
  - 🖼️ [Warning summary shows actual counts](../audit-dashboard/evidence/restore-repairs/screenshots/summary-needs-attention.png) — The latest S16 screenshot shows Restore needs attention, 1234 total, 1228 restored, 6 failed, and all six recorded reasons. The same test asserts Error and Cancelled never display Restore complete.
  - 🖼️ [Successful summary remains distinct](../audit-dashboard/evidence/restore-repairs/screenshots/summary-success.png) — The latest S16 success screenshot shows Restore complete, all 1234 restored and zero failures, using the production outcome-to-summary mapping.
  - [Restore safety evidence](../audit-dashboard/evidence/restore-repairs/report.html) — Open exact safety assertions, real E11 counts and session screenshots.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** Tests use disposable data. Review the linked report scope before accepting the item.
- **I need from you:** A015: Review the linked evidence and accept the item, or describe what still needs adjustment.

### P1: Synced fixture attributes can change during validation

- **Raised:** 2026-10-02
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** A016: Disposable test data now lives outside the Dropbox repository folder. Edge-case validation distinguishes supported regular-file content from unsupported filesystem metadata.
- **Current result:** Disposable test data now lives outside the Dropbox repository folder. Edge-case validation distinguishes supported regular-file content from unsupported filesystem metadata.
- **Remaining:** Review the linked evidence and accept the item, or describe what still needs adjustment.
- **Proof — open and look:**
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** This removes sync-client interference from fixtures. It does not claim restic preserves every Windows attribute or ACL.
- **I need from you:** A016: Review the linked evidence and accept the item, or describe what still needs adjustment.

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
- **Current result:** The restore tests check both sides of safety: every selected file must be recovered correctly, and files you did not select must remain unchanged. The large real-engine run restored 20,000 files and compared the contents of every file.
- **Remaining:** Open the report and check the examples of selected files being restored while unrelated existing files stay intact.
- **Proof — open and look:**
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
  - [Restore safety and file checks](../audit-dashboard/evidence/restore-repairs/report.html) — See which files were recovered, which existing files were protected, and how failures, cancellation and password changes were checked.
  - 🖼️ [Five restored files match their originals](../audit-dashboard/evidence/restore-repairs/screenshots/real-test-restore-five-files.png) — The real-engine test restores all five files and reports that their contents match. Separate checks cover empty files, all sources and failure handling.
  - [Recorded test results and remaining gaps](../audit-dashboard/evidence/repair-verification.html) — Read the pass and fail results, open the original test records if needed, and see what each run did and did not test.
- **Limitations / adversarial review:** These checks prove ordinary file contents. Windows metadata, permissions and cloud-provider behavior have their own limits in the linked report.

- **Review comments received 2026-10-05 (B006):**
  > - Restoring a selected file whose name contains brackets leaves an unrelated existing file unchanged. It also leaves a similarly named file unchanged when a wildcard would otherwise match it.
  > => WTH? How is this a check designed to catch unsafe results? This is a horrible bug!
  >
  > - In /proof/restore-repairs/report.html, the lsit of failures includes S3 read timed out and connection reset which sounds like retryable errors. What is the retry strategy?

- **I need from you:** B006: Open the report and check the examples of selected files being restored while unrelated existing files stay intact.

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

### P1: Exercise storage password rotation

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** B037: - Test password rotation
- **Current result:** The real password-change test confirms that the old password is rejected, the new password works, and the earlier snapshot still restores to the original file contents. The dialog waits for the credential save to finish before allowing itself to close.
- **Remaining:** Open the password-change report and the screenshot showing the dialog waiting to finish saving.
- **Proof — open and look:**
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
  - [Restore safety and file checks](../audit-dashboard/evidence/restore-repairs/report.html) — See which files were recovered, which existing files were protected, and how failures, cancellation and password changes were checked.
  - 🖼️ [The app remains usable after a real password change](../audit-dashboard/evidence/restore-repairs/screenshots/real-password-rotation.png) — The screenshot shows the app after the operation. The report proves the old password was rejected, the new one worked and all six original files could still be restored.
  - 🖼️ [The password dialog waits until saving is safe](../audit-dashboard/evidence/restore-repairs/screenshots/password-close-waits-for-save.png) — A close request during the password change leaves the dialog open. Both actions are disabled and the message explains that saving must finish first.
- **Limitations / adversarial review:** The test uses a disposable local repository. A screenshot of the open app is not, by itself, proof that the encryption key changed.

- **Review comments received 2026-10-05 (B037):**
  > So do you use Restic's builtin functionality for this?

- **I need from you:** B037: Open the password-change report and the screenshot showing the dialog waiting to finish saving.

### P2: Retry briefly when Windows temporarily locks a generated screenshot

- **Raised:** 2026-10-05
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** The combined suite reproduced an access-denied error while replacing a generated PNG in the synced checkout. The application checks had passed, but a brief external file lock prevented publishing their screenshot. Retry a bounded number of times; a persistent lock must still fail visibly without damaging the preceding image.
- **Current result:** A temporary Windows file lock could interrupt a test while it saved its screenshot. Screenshot publishing now waits briefly and retries. If the file stays locked, the test fails visibly and preserves the previous screenshot.
- **Remaining:** Review the recorded checks for both a temporary lock and a lock that remains in place.
- **Proof — open and look:**
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
- **Limitations / adversarial review:** Both tests use actual Windows file locks. A persistent lock is deliberately reported as a failure, not hidden.

- **I need from you:** A030: Review the recorded checks for both a temporary lock and a lock that remains in place.

### P2: Filter review issues by whose turn it is

- **Raised:** 2026-10-05
- **Type:** feature
- **Status:** Awaiting validation
- **Context:** The user needs a clear view of issues awaiting their review after an assistant update or reply, and issues where the assistant must address their latest comment. Replies must update issue explanations and evidence where needed. Use “With Agent”, not “With Codex”, so the same desk also works with Claude; individual replies should retain their author.
- **Current result:** Use Your review for issues awaiting your assessment and With Agent for issues awaiting work or a reply. A new comment or issue goes to the agent. An agent reply returns it to you unless the agent records more work still due. Both Codex and Claude replies are recognized.
- **Remaining:** Try the two queue buttons and confirm that they match how you want to review. The queue is named With Agent throughout.
- **Proof — open and look:**
  - 🖼️ [Your review and With Agent](../audit-dashboard/evidence/review-feedback/queues.png) — The two queues show whose next action is due.
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
- **Limitations / adversarial review:** Queue behavior is covered by persistence and browser checks. No automatic notification service was added.

- **I need from you:** N007: Try the two queue buttons and confirm that they match how you want to review. The queue is named With Agent throughout.

### P2: Edit or delete a review comment until it receives a response

- **Raised:** 2026-10-05
- **Type:** feature
- **Status:** Awaiting validation
- **Context:** Let the user edit or delete their unanswered comments, persist the change, and distinguish user and assistant messages clearly in threaded discussions. Answered comments must retain their context.
- **Current result:** Your comments and agent replies have distinct colors, author labels and readable paragraphs. Replies identify the comment they answer. Edit and Delete are available until a comment receives a response; deletion asks for a second click, and previous text is retained in the JSON history.
- **Remaining:** Review the discussion layout. Try editing or deleting an unanswered comment.
- **Proof — open and look:**
  - 🖼️ [Readable comments and replies](../audit-dashboard/evidence/review-feedback/conversation.png) — The original user comment and agent reply have distinct author labels and colors, readable paragraphs, and an explicit reply link.
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
- **Limitations / adversarial review:** Server checks enforce the unanswered rule even if an old browser tab still shows the buttons. Browser checks include repeated posting, editing, deletion and reload persistence.

- **I need from you:** N008: Review the discussion layout. Try editing or deleting an unanswered comment.

### P2: Make review counts useful as filters

- **Raised:** 2026-10-05
- **Type:** feature
- **Status:** Awaiting validation
- **Context:** Clicking a summary count should select its corresponding filter. Remove the total issue count card and replace All review stages with a clear label for active stages. Keep counts and visible issues consistent with completed and discarded items.
- **Current result:** Each of the four summary counts is now a filter button. Clicking one selects the matching stage below and clears other filters that could hide the results. The total-issues card is gone, and the dropdown starts with All active stages.
- **Remaining:** Click a summary count, then check that the stage dropdown and visible issues match.
- **Proof — open and look:**
  - 🖼️ [A summary count selects its filter](../audit-dashboard/evidence/review-feedback/queues.png) — Still needs testing is selected in both the count and the dropdown, and the list shows the matching 27 issues.
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
- **Limitations / adversarial review:** Closed issues are excluded from these active counts. Show completed or discarded remains available to find your earlier decisions.

- **I need from you:** N009: Click a summary count, then check that the stage dropdown and visible issues match.

### P2: Improve review desk legibility and information density

- **Raised:** 2026-10-03
- **Type:** feature
- **Status:** Awaiting validation
- **Context:** Also improve the legibility of the Review desk UX. It's not because it's tiny with plenty of padding that it is more professional. Just do better, be smarter than the frontend-design skill alone!
- **Root cause + fix (short):** Important text was only 9–13 pixels tall. Explanations now use 17-pixel text, original quotes use 16 pixels, and proof captions use 15 pixels. The margins are smaller, the issue list stays available while you read, and the background information at the top can be expanded when needed.
- **Proof — open and look:**
  - 🖼️ **[The revised issue layout](../audit-dashboard/evidence/review-assets/readable-issue.png)** — Larger text, tighter margins and a visible issue list while you read.
  - 🖼️ **[Your notes and their related issues](../audit-dashboard/evidence/review-assets/readable-notes.png)** — The original text uses larger type, with readable issue links beside each section.
  - **[Browser and persistence checks](../audit-dashboard/evidence/review-assets/review-checks.html)** — Includes the small-width layout checks and confirmation that comments and decisions still save.
- **Adversarial checks and limitations:** This changes presentation and wording. Posted comments, completion decisions and discussion threads still use the existing saved review data.
- **I need from you:** Read a few issues and open the original-notes page. Confirm that the text is comfortable at your usual browser zoom, or tell me what still feels cramped.

### P2: Remove redundant LIVE Live labeling in the log console

- **Raised:** 2026-10-03
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** Why Logs & runs have LIVE Live in the console, not redundant?
- **Root cause + fix (short):** The two labels meant the same thing. The console now uses one LIVE badge and a separate description of the operation, such as Restore · Documents. Saved runs do not show the LIVE badge.
- **Proof — open and look:**
  - 🖼️ **[The console with one LIVE label](../audit-dashboard/evidence/revision-after/logs/04-unselected-output-has-one-live-indicator.png)** — The single LIVE badge sits beside Current output. The same test checks saved runs and confirms that clearing the display leaves saved logs intact.
  - **[Tests for the revised app](../audit-dashboard/evidence/design-checks.html)** — Read the results, the behaviors that were checked, and the limits of those checks.
- **Adversarial checks and limitations:** The screenshots use sample activity. The tests also check that clearing the display leaves saved log files and history intact.
- **I need from you:** Open the console screenshots. Check that current output has one LIVE label and saved runs have none.

### P2: Unknown command-line arguments launch the desktop app

- **Raised:** 2026-10-03
- **Type:** bug
- **Status:** Awaiting validation
- **Context:** A019: Unknown command-line inputs previously launched the GUI, while mixed or trailing arguments were ignored. Ordinary modes now require exact arity and a value, before integration startup; helper payload validation remains dedicated.
- **Current result:** Unknown command-line inputs previously launched the GUI, while mixed or trailing arguments were ignored. Ordinary modes now require exact arity and a value, before integration startup; helper payload validation remains dedicated.
- **Remaining:** Review the linked evidence and accept the item, or describe what still needs adjustment.
- **Proof — open and look:**
  - [Scheduler, guard and parser regression results](../audit-dashboard/evidence/infrastructure-tests/runtime-coverage-report.html) — 57 passed, 0 failed, 0 skipped: 44 guard/parser plus13 action regressions
  - [Published command-line and native-loader checks](../audit-dashboard/evidence/packaged-vault-final/loader/report.html) — Nine final-executable CLI and native-loader checks passed, with expected exits, no configuration creation and PID-matched loader traces. Installed WinFSP and existing app extraction cache reused. Does not claim fresh-cache extraction, a GUI Mount click, or the user original repository.
- **Limitations / adversarial review:** 32 parser cases passed; malformed/mixed/trailing/missing inputs return UsageError without changing restore state. Existing option-shaped backup names remain valid. Nine actual published help/version and invalid-input commands passed expected exits and did not create configuration; actual --run -h also created the expected snapshot.
- **I need from you:** A019: Review the linked evidence and accept the item, or describe what still needs adjustment.

### P2: Exact original notes page with complete issue cross-links

- **Raised:** 2026-10-02
- **Type:** feature
- **Status:** Awaiting validation
- **Context:** N001: Can you add a page with the exact text i shared with you originally in this chat, that hyperlinks each section to the corresponding issue? And make sure the issue includes at the very top the originating verbatim extract (if there was any). So I can make sure ALL my original notes were taken into account.
- **Current result:** The Original notes page preserves all 62 lines exactly and links all 42 derived issues beside the matching sections. Each originating quote appears at the top of its issue.
- **Remaining:** Open the notes page and follow any section to check that the issue reflects your intent.
- **Proof — open and look:**
  - 🖼️ [Original notes and issue links](../audit-dashboard/evidence/notes-page.png) — Unchanged source text at left, all corresponding issue links at right.
  - [Coverage verification](../audit-dashboard/evidence/notes-coverage.html) — 62 lines, 42 linked issues, zero unmapped requirements; exact browser-text comparison passed.
- **Limitations / adversarial review:** Coverage has been checked line by line and the displayed source compared to the attachment. Issue closure remains your decision.
- **I need from you:** N001: Open the notes page and follow any section to check that the issue reflects your intent.

### P2: Reversible visual redesign after functional remediation

- **Raised:** 2026-10-02
- **Type:** feature
- **Status:** Awaiting validation
- **Context:** Feel free to improve the overall app design as well so it looks stunning if you see it is lacking in any area (without compromising the ease of use, legibility, and philosophy). The user rejected Georgia for the wordmark and page titles and requested a full interface audit. The first design proposal was not accepted.
- **Root cause + fix (short):** The revised app uses bundled Manrope for the Backspace name and page titles. Page margins and navigation are consistent, Settings sections use less space, dropdown arrows are visible, and the console shows one LIVE label. Windows fit the available laptop work area, and backup-card actions remain reachable when the window narrows.
- **Proof — open and look:**
  - 🖼️ **[The revised Backups page](../audit-dashboard/evidence/revision-after/full/02-backups-overview.png)** — Manrope headings, aligned content and a clear navigation selection.
  - **[Compare the previous and revised designs](../audit-dashboard/evidence/design-review.html)** — Every before-and-after pair has matching dimensions. Smaller laptop layouts and the preview instructions are included.
  - **[Tests for the revised app](../audit-dashboard/evidence/design-checks.html)** — Read the results, the behaviors that were checked, and the limits of those checks.
- **Adversarial checks and limitations:** The gallery uses the same-sized windows for each comparison. Small-laptop screenshots are shown separately. Tests use disposable local data; your aesthetic approval is still needed, and no completion box is checked on your behalf.
- **I need from you:** Open the matching-size comparison and try Launch-Design-Preview.bat. Approve the appearance or leave specific changes on this issue. The design remains separately reversible from the earlier backup and restore repairs.

### P2: UI copy overuses em dashes

- **Raised:** 2026-07-03
- **Type:** polish (copy)
- **Status:** Awaiting validation
- **Context:** R004: Current XAML labels and view-model prose use ordinary punctuation in the inspected copy. Representative restore, settings and filter screens show the result. Em-dash placeholders, log formatting, code comments and explicit filter fences remain where they have a distinct purpose.
- **Current result:** Current XAML labels and view-model prose use ordinary punctuation in the inspected copy. Representative restore, settings and filter screens show the result. Em-dash placeholders, log formatting, code comments and explicit filter fences remain where they have a distinct purpose.
- **Remaining:** Review the representative wording and confirm that its tone matches your preference.
- **Proof — open and look:**
  - 🖼️ [Restore instructions with ordinary punctuation](../audit-dashboard/evidence/carryovers/restore-target-other-folder.png) — The heading, introduction, overwrite warning and target labels use ordinary sentence punctuation.
  - [R004 · Carryover evidence and test scope](../audit-dashboard/evidence/carryovers/report.html) — The report distinguishes inspected product prose from placeholders, logs and filter-marker syntax.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** This verdict includes source inspection and representative visual inspection, not a whole-app string or all-error-dialog proof. User-entered names and paths can contain arbitrary punctuation.
- **I need from you:** R004: Review the representative wording and confirm that its tone matches your preference.

### P2: Folder-picker rows should toggle expand on click anywhere left of the checkbox

- **Raised:** 2026-06-11
- **Type:** bug (UX)
- **Status:** Awaiting validation
- **Context:** R012: S29 sends actual headless pointer events to the row text and checkbox. Text clicks expand; after resetting collapsed state, the checkbox click leaves expansion unchanged.
- **Current result:** S29 sends actual headless pointer events to the row text and checkbox. Text clicks expand; after resetting collapsed state, the checkbox click leaves expansion unchanged.
- **Remaining:** Inspect the picker state and accept if row/checkbox gestures behave as intended.
- **Proof — open and look:**
  - 🖼️ [Checkbox selection leaves the row collapsed](../audit-dashboard/evidence/carryovers/picker-checkbox-without-expansion.png) — The fixture's C: row is checked while its chevron remains collapsed. The companion test first clicks its text and asserts expansion.
  - [R012 · Carryover evidence and test scope](../audit-dashboard/evidence/carryovers/report.html) — The test uses pointer-down/up events and asserts both positive row expansion and the checkbox non-expansion negative control.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** The fixture checks row text and checkbox centers, not every pixel of a row or native Explorer. The final screenshot shows the collapsed selected row; the earlier expansion is established by the assertion.
- **I need from you:** R012: Inspect the picker state and accept if row/checkbox gestures behave as intended.

### P2: Filter editor wording + recommended-exclusion polish

- **Raised:** 2026-06-11
- **Type:** bug + cleanup
- **Status:** Awaiting validation
- **Context:** R013: Advanced help describes bare globs and platform-default case sensitivity. Recommended exclusions use one ntuser.dat* rule, and translator tests cover insensitive Windows routing plus the case-sensitive alternative.
- **Current result:** Advanced help describes bare globs and platform-default case sensitivity. Recommended exclusions use one ntuser.dat* rule, and translator tests cover insensitive Windows routing plus the case-sensitive alternative.
- **Remaining:** Review the help text and recommended rule simplification.
- **Proof — open and look:**
  - 🖼️ [Help explains the rules users actually type](../audit-dashboard/evidence/carryovers/filter-simulation.png) — The Advanced pane says one bare glob per line, gives *.tmp and cache/ examples, and explains Windows-insensitive versus macOS/Linux-sensitive defaults.
  - [R013 · Carryover evidence and test scope](../audit-dashboard/evidence/carryovers/report.html) — Passing tests assert one ntuser.dat* family pattern and both case-routing outcomes.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** Windows is the exercised platform. The alternate case-sensitive route is a controlled unit test, not a macOS/Linux application run.
- **I need from you:** R013: Review the help text and recommended rule simplification.

### P2: Custom-exclusion markers should sit BEFORE Backspace-generated block

- **Raised:** 2026-06-11
- **Type:** UX
- **Status:** Awaiting validation
- **Context:** R014: Custom fences appear above the generated block in the real Advanced editor. S56 clicks Apply twice, checks preserved custom content and idempotence, then switches Easy/Advanced and verifies the ordering remains intact. The matching help copy is corrected.
- **Current result:** Custom fences appear above the generated block in the real Advanced editor. S56 clicks Apply twice, checks preserved custom content and idempotence, then switches Easy/Advanced and verifies the ordering remains intact. The matching help copy is corrected.
- **Remaining:** Inspect the actual Advanced filter text and confirm custom rules appear first.
- **Proof — open and look:**
  - 🖼️ [Actual editor keeps custom rules above generated rules](../audit-dashboard/evidence/carryovers/editor-mode-round-trip.png) — The visible custom block contains *.private and its comment; the recommended block starts below it after Apply and a round trip through Easy mode.
  - 🖼️ [Custom rules are above recommended rules](../audit-dashboard/evidence/carryovers/savings-wrap-current.png) — Both the introduction and Apply explanation say custom rules stay above the recommended block and are preserved.
  - [R014 · Carryover evidence and test scope](../audit-dashboard/evidence/carryovers/report.html) — Tests assert custom-start/custom-end precede recommended-start, legacy rules survive, and a second merge is identical. The final screenshot shows the corrected explanation.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** The actual editor interaction and string assertions cover the tested legacy custom rule and generated block. Arbitrary hand-edited malformed marker files are outside this screenshot's scope.
- **I need from you:** R014: Inspect the actual Advanced filter text and confirm custom rules appear first.

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
- **Status:** Open
- **Context:** B010: (also check Docx file to make sure all is good)
- **Current result:** No .docx file was found in the repository, including ignored files outside generated dependencies. The only getting-started documents found outside README/site belonged to the separate upstream Backrest checkout.
- **Remaining:** Identify or provide the specific Word document and intended getting-started guide before this review can be completed.
- **Proof — open and look:**
  - No completion proof claimed; the missing input is stated above.
- **Limitations / adversarial review:** No claim is made about the missing document's correctness.
- **I need from you:** B010: Identify or provide the specific Word document and intended getting-started guide before this review can be completed.

### P2: Explain scheduling independently of the tray

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** B022: Also it is misleading to say "Backspace keeps running in the background so scheduled backups still fire"! App doesn't need to run in the background for this! User should be reassured about this. Only useful to keep to reopen fast. How are things handled if a backup or any other operation is running and user wants to quit? I think we should still push to keep app running in the systray if you can confirm CPU impact will be zero when no operation is running. This can be reassuring for people to see Backspace if there. But we should reassure them even if Backspace is quit, it'll stop existing operations but scheduled operations will be triggered as usual. Find the best way to frame this.
- **Current result:** Tray copy explains that schedules run independently. Quit confirms active work, cancels owned operations and waits for acknowledgement. It keeps the app open if operations cannot safely drain, and blocks new GUI work during exit.
- **Remaining:** Review the linked evidence and accept the item, or describe what still needs adjustment.
- **Proof — open and look:**
  - 🖼️ [Quit protects active work](../audit-dashboard/evidence/review-ui/05-tray-quit-protects-running-restore.png) — The actual Quit action explains the consequence and offers Keep running; the test confirms that choice preserves the restore.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** This covers app-owned operations. A scheduled task is a separate process; a real reboot remains a separate acceptance item.
- **I need from you:** B022: Review the linked evidence and accept the item, or describe what still needs adjustment.

### P2: Measure idle tray CPU use

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** B024: Also it is misleading to say "Backspace keeps running in the background so scheduled backups still fire"! App doesn't need to run in the background for this! User should be reassured about this. Only useful to keep to reopen fast. How are things handled if a backup or any other operation is running and user wants to quit? I think we should still push to keep app running in the systray if you can confirm CPU impact will be zero when no operation is running. This can be reassuring for people to see Backspace if there. But we should reassure them even if Backspace is quit, it'll stop existing operations but scheduled operations will be triggered as usual. Find the best way to frame this.
- **Current result:** Measured idle CPU exposed a Windows compositor loop. Using Windows redirection composition reduced the measured idle cost by over 90% in the controlled comparison; the final shared production-builder run is linked in the report.
- **Remaining:** Review the linked evidence and accept the item, or describe what still needs adjustment.
- **Proof — open and look:**
  - [Idle CPU with the final design](../audit-dashboard/evidence/design-native-idle/report.html) — Two 30-second samples: 0.203% visible and 0.209% hidden of total CPU on this 22-thread host. Empty disposable configuration; no universal zero-CPU claim.
  - [Idle CPU measurements and diagnosis](../audit-dashboard/evidence/idle-cpu-comparison.html) — Compare raw visible/hidden-window samples and the shared production-builder confirmation; measurement scope and machine details are disclosed.
- **Limitations / adversarial review:** CPU is small but not zero. These measurements describe this Windows machine and isolated window/daemon, not every device or full installed integration.
- **I need from you:** B024: Review the linked evidence and accept the item, or describe what still needs adjustment.

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
- **Current result:** The actual Dropbox file you supplied stayed online-only, with zero allocated bytes throughout the checks. Neither backup contained its contents. Bundled Restic 0.18.1 rejected this placeholder as an unsupported irregular file. The run returned Warning, and the app log now preserves the specific filename and error.
- **Remaining:** Prototype a separate cloud source, starting with Dropbox. Prove that unchanged files remain in each snapshot without being downloaded again, changed revisions are not missed, temporary space is bounded, and original placeholder states never change. This is agent implementation work; source-account authorization will be needed for a live provider trial.
- **Proof — open and look:**
  - [Online-only source research and proposed approach](../audit-dashboard/evidence/remaining-work-20261009/cloud-source-research.html) — Compares verified competitor behavior, explains the Restic constraint, and lists the acceptance checks. Research, not a claim of working implementation.
  - [Actual Dropbox test: no download and no backed-up contents](../audit-dashboard/evidence/cloud-placeholder-20261005/report.html) — See the file measurements, snapshot checks and original engine error. The supplied file remained online-only.
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
  - [Recorded test results and remaining gaps](../audit-dashboard/evidence/repair-verification.html) — Read the pass and fail results, open the original test records if needed, and see what each run did and did not test.
- **Limitations / adversarial review:** This is a real test of the exact supplied Dropbox file. It does not establish that all Dropbox, OneDrive or Google Drive placeholders behave the same way. No successful hydration was observed.

- **Review comments received 2026-10-05 (B035):**
  > mark as still needs testing and provide me step by step instruction in the chat so we can complete this test together.
  > HOWEVER it seems you can already do a test yourself with this folder:
  > C:\Users\erwin\Dropbox\Apps\InpsydeBackWPup\upbraining.com-store
  > Contains one file online only. See how it is handled. If your testing makes this file local and no longer online only, tell me.

  > exclude-cloud-files => Is it an option we have currently? Is it a legacy one from Duplicacy we had in duplimate? If so, how does Restic handles this?

- **Follow-up — 2026-10-09:** Investigate backing up online-only Dropbox, Google Drive and OneDrive files without changing the user’s local availability choice. Check provider metadata for changes, download changed content into a separate temporary location, check free space before downloading, and warn clearly when a file cannot be included. The requested default is to ignore online-only files; including them must explain that local placeholders stay online-only. Research existing products and the Restic/Backrest integration before proposing implementation.
- **Research result — 2026-10-09:** Cloudstic documents direct Google Drive and OneDrive source reads with change tracking; Arq documents changed-file checks but hydrates the original Windows files. Restic users also report backups from rclone-mounted sources. These are useful precedents, not proof of the exact requested workflow. The linked report separates documented behavior from implementation and testing still needed in Backspace.
- **I need from you:** Nothing for the research. A later live prototype will need your sign-in and a disposable source folder; the current checkbox must not yet promise to leave included files online-only.

### P2: Check layout with ten sources and destinations

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** B038: Test adding 10 sources and destinations to see how it looks (scrollbar)
- **Current result:** A new audit-only fixture loads ten sources and ten destinations in one backup. Both lists have independent scrollbars and can display item 10; the action shelf stays visible. Editor top/bottom captures are available too.
- **Remaining:** User visual approval of layout; optional native mouse-wheel test at high display scaling.
- **Proof — open and look:**
  - 🖼️ [Ten-source/destination card, top](../audit-dashboard/evidence/design-after/dense/ten-sources-destinations-top.png) — Counts read 10 SOURCES and 10 DESTINATIONS; independent list scrollbars keep actions visible.
  - 🖼️ [Both lists scrolled to the bottom](../audit-dashboard/evidence/design-after/dense/ten-sources-destinations-bottom.png) — Source-10 and Destination 10 are visible at the bottom of their lists.
  - 🖼️ [Ten-source editor, top](../audit-dashboard/evidence/design-after/dense/ten-sources-editor-top.png) — The source list occupies the scrollable form, with Save buttons fixed below.
  - [Final dense-layout and pointer checks](../audit-dashboard/evidence/design-checks.html) — Five fresh UI probes pass with the proposed design, including ten sources/destinations and reachable scrolling controls.
- **Limitations / adversarial review:** Data is seeded directly into view models; this does not prove ten repetitions of picker/add/save interactions or ten actual backup destinations.
- **I need from you:** B038: User visual approval of layout; optional native mouse-wheel test at high display scaling.

### P2: Review the sidebar grouping and label

- **Raised:** 2026-10-02 (original note reintroduced for audit)
- **Type:** coverage
- **Status:** Awaiting validation
- **Context:** Maybe move the down menu block right below Destinations, see what to do with label Your Backups, ask Claude for advice.
- **Root cause + fix (short):** Backups and Destinations sit together at the top of the sidebar, followed by Settings and Activity & logs. The current page has a highlight across the full navigation row. The version and Backrest credit remain at the bottom.
- **Proof — open and look:**
  - 🖼️ **[The revised Backups page](../audit-dashboard/evidence/revision-after/full/02-backups-overview.png)** — Manrope headings, aligned content and a clear navigation selection.
  - **[Compare the previous and revised designs](../audit-dashboard/evidence/design-review.html)** — Every before-and-after pair has matching dimensions. Smaller laptop layouts and the preview instructions are included.
  - **[Tests for the revised app](../audit-dashboard/evidence/design-checks.html)** — Read the results, the behaviors that were checked, and the limits of those checks.
- **Adversarial checks and limitations:** The gallery uses the same-sized windows for each comparison. Small-laptop screenshots are shown separately. Tests use disposable local data; your aesthetic approval is still needed, and no completion box is checked on your behalf.
- **I need from you:** Open the matching-size comparison and try Launch-Design-Preview.bat. Approve the appearance or leave specific changes on this issue. The design remains separately reversible from the earlier backup and restore repairs.

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

### P3: Create new issues directly in the review desk

- **Raised:** 2026-10-05
- **Type:** feature
- **Status:** Awaiting validation
- **Context:** Add an accessible new-issue form and persist user-created issues in the existing review JSON. Preserve the user's wording and place new issues in the assistant's queue.
- **Current result:** New issue opens a simple title, description and priority form. Saving creates a permanent issue ID, preserves your description at the top and places the issue in With Agent. It survives a page reload and server restart.
- **Remaining:** Use New issue for the next request and confirm that the form contains what you need.
- **Proof — open and look:**
  - 🖼️ [Create an issue from the desk](../audit-dashboard/evidence/review-feedback/new-issue.png) — The dialog asks for a title, description and priority, and sends new issues to the agent.
  - [Replies, screenshots and remaining checks](../audit-dashboard/evidence/review-feedback/report.html) — Read the answers to your comments, inspect the actual interface screenshots, and see which checks used the real engine.
- **Limitations / adversarial review:** The description is kept as your original wording. Later audit imports cannot overwrite it or the discussion.

- **I need from you:** N012: Use New issue for the next request and confirm that the form contains what you need.

### P3: "Restores" left-nav item — single-restore variant shipped

- **Raised:** 2026-06-11
- **Type:** feature
- **Status:** Awaiting validation
- **Context:** B016: - If user initiates a restore operation, and then go to another menu item, he cannot go back to see the status of the restore operation. Please add a Restores menu item on the left with a visual dynamic icon indicating it is restoring something (like an animation that'd express a file download), and inside this menu item it should show a list of cards with all the active restore operations with basic status. Clicking on one card should get the user back to where he was before he moved away from the restore screen. As soon as user creates the restore, it should show this menu item, highlighted in the same color as when user is in Destinations or Backup.  B017: - If user initiates a restore operation, and then go to another menu item, he cannot go back to see the status of the restore operation. Please add a Restores menu item on the left with a visual dynamic icon indicating it is restoring something (like an animation that'd express a file download), and inside this menu item it should show a list of cards with all the active restore operations with basic status. Clicking on one card should get the user back to where he was before he moved away from the restore screen. As soon as user creates the restore, it should show this menu item, highlighted in the same color as when user is in Destinations or Backup.
- **Current result:** Sessions retain independent state and exact per-card navigation. Finished cards stay until explicitly dismissed. Long real restores show a running indeterminate phase and truthful final counts; fake numeric progress is not inferred from preallocated file sizes. Sessions retain independent state and exact per-card navigation. Finished cards stay until explicitly dismissed. Long real restores show a running indeterminate phase and truthful final counts; fake numeric progress is not inferred from preallocated file sizes.
- **Remaining:** Review the linked evidence and accept the item, or describe what still needs adjustment.
- **Proof — open and look:**
  - 🖼️ [Two sessions with independent progress](../audit-dashboard/evidence/restore-repairs/screenshots/sessions-independent-progress.png) — BackupA shows 12 of 50 files while BackupB shows 60 of 80; the rendered values follow separate session updates.
  - 🖼️ [Clicking a card returns to that session](../audit-dashboard/evidence/restore-repairs/screenshots/session-exact-return.png) — BackupB detail shows its snapshot, destination and 60/80 progress, while BackupA retains its independent 12/50 count.
  - 🖼️ [Results stay available until dismissed](../audit-dashboard/evidence/restore-repairs/screenshots/sessions-results-retained.png) — After the former four-second expiry, both finished results remain visible with distinct success/warning states and explicit Dismiss controls.
  - [Restore safety evidence](../audit-dashboard/evidence/restore-repairs/report.html) — Open exact safety assertions, real E11 counts and session screenshots.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** Tests use disposable data. Review the linked report scope before accepting the item.
- **I need from you:** B016: Review the linked evidence and accept the item, or describe what still needs adjustment. B017: Review the linked evidence and accept the item, or describe what still needs adjustment.

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
- **Status:** Open
- **Context:** R017: The ignored historical BACKSPACE_PORT_DESIGN.md still exists. Its June port-gap table is stale relative to the repaired implementation; current README, architecture, gotchas and testing documents carry the maintained description.
- **Current result:** The ignored historical BACKSPACE_PORT_DESIGN.md still exists. Its June port-gap table is stale relative to the repaired implementation; current README, architecture, gotchas and testing documents carry the maintained description.
- **Remaining:** Confirm whether to delete or archive the historical local port plan. Real provider registration and packaged/live integration are still explicitly open elsewhere.
- **Proof — open and look:**
  - [R017 · Carryover evidence and test scope](../audit-dashboard/evidence/carryovers/report.html) — The report records that the historical file was preserved and identifies its stale gap claims without treating them as present implementation facts.
  - [Repair validation: actual test results](../audit-dashboard/evidence/repair-verification.html) — Open the named assertions, pass/fail results, raw test-runner files and explicit limits.
- **Limitations / adversarial review:** No historical file was deleted. This is a housekeeping decision, not an executable feature test.
- **I need from you:** R017: Confirm whether to delete or archive the historical local port plan. Real provider registration and packaged/live integration are still explicitly open elsewhere.

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
