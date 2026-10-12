# Preparing a release

A passing build cannot tell whether a README explanation or a screenshot is still accurate. Every release needs both automated checks and a human review of the exact revision being released.

1. Run **Release checks** in GitHub Actions on `main`, enable **publish**, and enter a new version tag such as `v1.0.0`. It runs the unit and interface suite, then backs up and restores at least 20,000 files and approximately 2 GiB through the real bundled Backrest and Restic engine. It compares restored contents with the originals. A missing or skipped large test fails the check. Leave **publish** off for a rehearsal that creates no tag or release.
2. Open the resulting `release-evidence-<commit>` artifact. Inspect the new scenario and website screenshots, the test results and the recorded engine versions. The commit recorded in the artifact must match the release revision; rerun after any change. Engine versions are selected in `engine-versions.json`; both platform downloaders use those release tags. Existing developer binaries are preserved, so compare their versions when reproducing a result. Upgrading an engine requires changing that file deliberately and repeating the real-engine tests.
3. Review README, DESIGN, the architecture and testing guides, and the website's screenshots against that build. Update obsolete wording and images. Check the first-run, backup, destination setup, restore, settings and logs screens. Check the documented build and launch commands. Record the reviewed revision and any known limitations in the release notes.
4. A human reviewer confirms that the screenshots and documentation match the build, then approves the waiting `release` environment job. Do not treat generated images, their timestamps, or an agent's claim as that confirmation. An agent must not approve on the reviewer's behalf.
5. The approved job publishes the Windows candidate already built by this run. It checks the recorded revision and creates a new tag atomically; an existing tag is rejected rather than reused or retargeted. If publication fails after tag creation, investigate before retrying; do not automatically delete the tag.

## What the reviewer must actually check

Automation prepares the evidence and prevents this publishing job from running without approval. It cannot judge whether the explanations are accurate or the screenshots are useful. An agent can perform that comparison and propose corrections, but a human must approve the result. Adding a reminder to AGENTS.md alone cannot enforce that pause.

Before approving, record the following in the GitHub approval comment, using the full revision from the evidence artifact:

- **Revision reviewed:** the exact commit, with a link to this run's evidence.
- **README and guides:** compare the installation, first backup, restore, scheduling, tray/quit and update instructions with the candidate. Run the documented commands. Name any corrections, or say that no corrections were needed.
- **Screenshots and website:** open the published images alongside the newly rendered screens. Check labels, navigation, layout and the behavior described by captions. A recently generated screenshot can still depict the wrong state. Name the images replaced or checked.
- **Limitations:** record unfinished native Windows or live-account checks and whether they block this release.

An example approval comment is: “Reviewed revision <full commit> and its linked evidence. Checked README and guides against the candidate, compared website images with the current screens, and reviewed the recorded limitations. Approved for release.” Add specific findings rather than copying this statement without doing the review. If a correction changes the release revision, start a new run and review the new candidate.

The approval comment is an audit record, not a machine-checked claim of correctness. The enforced boundary is the required human approval. An LLM review could assist later, but would not remove the need for judgment or prove that every statement is correct.

## Windows update package contract

The workflow embeds the requested tag's version in the executable and publishes `Backspace-windows-x64.zip`. Stable tags use three numeric components; prerelease tags are marked as prereleases and are not offered by the stable updater. Keep the bundle flat, with Backspace, Backrest, Restic and rclone executables together. The updater requires GitHub's SHA-256 asset digest and checks the executable's product version against the release tag. A missing digest or mismatched bundle must fail closed and offer the manual release page.

Before the first public updater release, verify the actual packaged download/restart flow on Windows, including an active scheduled run, cancellation, insufficient space, rollback and the recovery launcher from a folder containing spaces and non-ASCII characters. A mocked release response or a passing replacement unit test does not establish native acceptance.

## Enforcing the process on GitHub

The **Release checks** workflow makes publishing depend on successful tests and a human approval. Its check job has read-only repository access; only the protected publishing job receives write access. The `release` environment was configured on 9 October 2026 with `mayerwin` as its required reviewer and administrator bypass disabled. The maintainer can approve their own manually started run because this repository currently has one designated reviewer.

The allowed publishing branch is `main`. The implementation branch may temporarily be allowed for a non-publishing rehearsal, and must be removed from the environment's branch policies afterward. The workflow independently rejects publishing requests from any other branch.

This protects this workflow. It does not remove an administrator's ability to change repository settings, nor prevent someone who already has repository write permission from publishing manually through a different route. Keep write access limited to trusted maintainers and use this workflow for every release. Do not describe the environment as a repository-wide ban on manual releases. `AGENTS.md` is a standing reminder, not a substitute for the gate.

The [9 October 2026 rehearsal](https://github.com/backspace-backup/backspace/actions/runs/37926997445), at revision `d344e04`, passed all automatic checks and reached the waiting approval job. It was deliberately cancelled there without publishing. Its temporary implementation-branch exception was removed. The local Review desk links the saved waiting-state screenshot and detailed results. This proves the approval path for that revision; every real release must run fresh checks on its own revision. The implementation branch still needs review and merging before main contains this complete version of the workflow. Do not approve a rehearsal automatically merely to obtain a green result.

GitHub describes [deployment environments and required reviewers](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments). Cloud sign-in, reboot, VSS and physical-drive acceptance checks remain separate requirements where relevant; a local large-data test does not prove them.
