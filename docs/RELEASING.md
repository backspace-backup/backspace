# Preparing a release

A passing build cannot tell whether a README explanation or a screenshot is still accurate. Every release needs both automated checks and a human review of the exact revision being released.

1. Run **Release checks** in GitHub Actions on the intended release revision. It runs the unit and interface suite, then backs up and restores at least 20,000 files and approximately 2 GiB through the real bundled Backrest and Restic engine. It compares restored contents with the originals. A missing or skipped large test fails the check.
2. Open the resulting `release-evidence-<commit>` artifact. Inspect the new screenshots and the test results. The commit recorded in the artifact must match the release revision; rerun after any change.
3. Review README, DESIGN, the architecture and testing guides, and the website's screenshots against that build. Update obsolete wording and images. Check the first-run, backup, destination setup, restore, settings and logs screens. Check the documented build and launch commands. Record the reviewed revision and any known limitations in the release notes.
4. A human reviewer confirms that the screenshots and documentation match the build. Do not treat generated images, their timestamps, or an agent's claim as that confirmation.
5. Publish only the revision that passed these checks and was reviewed. Do not reuse an older successful run for a newer tag.

## Enforcing the process on GitHub

The repository includes a manual **Release checks** workflow. It produces evidence and does not publish a release. This is useful preparation, but it cannot prevent someone with permission from publishing manually.

For enforcement, use one release workflow whose publish job depends on these checks and targets a protected `release` environment with a required reviewer. The reviewer opens the evidence for that revision and approves the documentation and screenshots before the publish job can run. Restrict release-tag creation and release publishing to that workflow and the designated maintainers; otherwise manual publication remains a bypass. The exact settings depend on the repository's plan and access rules.

Those GitHub settings and the publishing workflow have not been enabled by the local audit. Keep the release-enforcement issue open until a real workflow run and its approval gate have been exercised. `AGENTS.md` is a standing reminder, not a substitute for the gate.

GitHub describes [deployment environments and required reviewers](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments). Cloud sign-in, reboot, VSS and physical-drive acceptance checks remain separate requirements where relevant; a local large-data test does not prove them.
