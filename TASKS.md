# Backspace — Task board

_Generated from the Agent-Manager task board (source of truth: the brain DB). Do not hand-edit._

## Backlog (4)

- **E2E revamp pass — click X, see Y across all 13 areas** — _P0, human, test coverage, process_
  - User confirmed: the "unit tests prove the algorithm, no
- **Investigate "why do we deviate from Backrest's native restore RPC?"** — _P0, human, architecture review_
  - Backspace's "thin wrapper" promise (see AGENT.md design
  - **Proof (note):** Per-file Files dict / RestoredFile / RestoreFileStatus / retry
  - **Proof (note):** E01 matrix (Smoke 31s, Many 1m4s, Large 1m5s) + E34 (1m3s)
- **Folder-picker rows should toggle expand on click anywhere left of the checkbox** — _P2, human, bug (ux)_
  - Picker rows today require the user to click the small
  - **Proof (note):** Markup + code-behind at
  - **Proof (note):** Visual confirmation: the existing source-tree-picker scenario
- **OneDrive + Google Drive OAuth client_ids** — _P3, human, feature_
  - `OAuthAppRegistry` ships with empty strings for both
  - **Proof (note):** tests/.../OAuthProviderTests.cs

## Human review (15)

- **Restore wizard doesn't offer a revision picker (regression vs Duplimate)** — _P0, human_
  - User reported: "Bring Files back" doesn't even offer
  - **Proof (note):** Fix in
  - **Proof (note):** Regression guard E52_RevisionPickerVisible — drives the
  - **Proof (note):** Visual proof — S14_RestoreSingleSourceRevisionPicker
- **Restore enumerates every leaf when a folder is selected** — _P0, human, bug + design_
  - User restored 2 folders containing ~2400 files. Engine
  - **Proof (note):** tests/Backspace.Tests/Services/RestoreSelectionMinimizerTests.cs
  - **Proof (note):** E01 matrix (Smoke 32 s, Many 1 m 8 s, Large 1 m 7 s) re-run after
- **Restore crashes with "filename or extension is too long" on large file sets** — _P0, human_
  - Windows' CreateProcess command-line ceiling is ~32 KB.
  - **Proof (note):** E34_RestoreLongIncludeList PASSed (58 s) restoring 1500 files
  - **Proof (note):** E01 matrix re-run (Smoke 32 s, Many 1 m 8 s, Large 1 m 7 s) — all
  - **Proof (note):** BACKSPACE_E2E_SCALE=huge env var defined and wired through
- **Mount fails for the user's basic dist-folder backup** — _P0, human_
  - User ran a simple backup against the bundled `dist/`
  - **Proof (note):** The original WebDAV-failure diagnostic (MountRepro.NetUseReproducesUserFailure)
  - **Proof (note):** E04_SnapshotMountWindows now drives the WinFSP path
  - **Proof (note):** Mount actually-works lives in its own ROADMAP entry below
- **Mount feature actually working (WinFSP-backed)** — _P0, human, feature, architecture_
  - See the parent entry for why the previous WebDAV path
  - **Proof (note):** WinFSP filesystem at
  - **Proof (note):** Coordinator at
  - **Proof (note):** Runtime detector at
- **Estimated savings next to "Apply recommended exclusions" shows 0 B** — _P1, human_
  - User reported "Estimated savings" showed 0 B. The
  - **Proof (note):** tests/Backspace.Tests/ViewModels/BackupEditorSavingsLabelTests.cs
- **Skipped-run alerts block visibility logic** — _P1, human, bug_
  - In Easy mode the "Skipped run alerts" block is shown
  - **Proof (note):** tests/Backspace.Tests/Models/BackupShouldShowGraceBlockTests.cs
  - **Proof (note):** Advanced block markup added at
- **Collapsible-section hover styling has weird stripe** — _P1, human_
  - User screenshot of the restore wizard's "Advanced"
  - **Proof (note):** Style change visible at
  - **Proof (note):** Visual confirmation: re-running the visual scenario suite will
- **Filter editor wording + recommended-exclusion polish** — _P2, human, bug + cleanup_
  - Three sub-issues in the filter editor's Advanced section.
  - **Proof (note):** tests/Backspace.Tests/Services/RecommendedFiltersTests.cs
  - **Proof (note):** Help text change visible at
- **Custom-exclusion markers should sit BEFORE Backspace-generated block** — _P2, human, ux_
  - When the editor seeds a filter file, the Backspace-curated
  - **Proof (note):** tests/Backspace.Tests/Services/RecommendedFiltersTests.cs
  - **Proof (note):** Same file MergeIntoFilters_emptyExisting_yieldsCustomBlockThenAutoBlock
  - **Proof (note):** Same file MergeIntoFilters_replacesExistingBlock_inPlace —
- **E2E coverage audit — user-action ↔ scenario matrix** — _P2, human, coverage, process_
  - Two production bugs in this session (Mount, long-include
  - **Proof (note):** docs/TESTING.md § Coverage matrix
  - **Proof (note):** Acceptance criterion: every action shows a scenario name or a
- **"Restores" left-nav item — single-restore variant shipped** — _P3, human, feature_
  - **Proof (note):** Visible nav-item markup at
  - **Proof (note):** Animation style at
  - **Proof (note):** Wiring at
- **"Restores" multi-restore cards — shipped** — _P3, human, feature_
  - Today, starting a restore and navigating away forfeits
  - **Proof (note):** tests/Backspace.Tests/Services/ActiveRestoreRegistryTests.cs
  - **Proof (note):** Code at
- **rclone orphan-safe kill** — _P3, human, reliability_
  - Backrest spawns `rclone.exe` on demand. If Backrest
  - **Proof (note):** src/Backspace/Services/RcloneOrphanReaper.cs
  - **Proof (note):** Wire-up at src/Backspace/Program.cs:51-58.
  - **Proof (note):** Failure-isolated: the whole sweep is wrapped in try/catch +
- **Auto-update channel — light variant** — _P3, human, infrastructure_
  - Today users download a fresh zip from Releases when
  - **Proof (note):** src/Backspace/Services/UpdateChecker.cs
  - **Proof (note):** Wire-up at

## Blocked (1)

- **Delete BACKSPACE_PORT_DESIGN.md once port-plan gaps close** — _P3, human, docs cleanup_
  - `BACKSPACE_PORT_DESIGN.md` is the historical port brief.
