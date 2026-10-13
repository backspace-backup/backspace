# Preparing a release

Every release needs an agent-completed checklist for its exact commit. Automated tests cannot judge whether explanations and screenshots are accurate; the agent must inspect them and record specific findings. No human approval is required by this process.

## Prepare and record the checks

1. Finish the changes, run the relevant tests, review the documentation and screenshots, and commit any corrections.
2. Run the full unit/interface suite, the real-engine 20,000-file round trip and the Windows package/update checks for that exact commit. Commands and fixtures are in [TESTING.md](TESTING.md). A test that was skipped or run before the last change does not establish coverage for the release.
3. Run `node scripts/release-checklist.cjs create` from a clean checkout. It creates `release-checks/<full-commit>.json` with every box unchecked. The directory is Git-ignored; copy the completed file separately if moving the checkout.
4. Read every requirement in `scripts/release-checklist-template.json`. Fill in who performed the review, when it happened, whether the UX changed, and specific findings or evidence for every item. Tick each box only after doing that work. Inspect README, contributor instructions, technical guides, app/website screenshots and captions against this build. Regenerate images affected by UX changes and review them with your eyes. Record remaining limitations and resolve release-blocking gaps.
5. Validate the record with `node scripts/release-checklist.cjs validate release-checks/<full-commit>.json <full-commit>`. If any source, guide or screenshot changes afterward, commit it and prepare a checklist for the new commit. If the template changes, repeat the review using its latest requirements.

A checklist is a durable statement of work performed, not proof that an LLM understood every sentence correctly. The validator checks the exact revision, template fingerprint, required items, true checkboxes, review metadata and non-empty findings. It rejects absent, incomplete, wrongly named and stale records.

## Run the release workflow

Once the checked commit is on `origin/main`, run:

```text
node scripts/release-checklist.cjs dispatch release-checks/<full-commit>.json
```

This performs a rehearsal without creating a tag or release. To publish an explicitly authorized version:

```text
node scripts/release-checklist.cjs dispatch release-checks/<full-commit>.json --publish --tag v1.0.0
```

The dispatcher verifies a clean checkout and that the checked commit is current `origin/main`. It sends the exact JSON as a GitHub Actions input. The workflow saves it under its commit filename, so the record can remain Git-ignored locally while still being available to the release gate and included in the evidence artifact.

The workflow rejects an invalid checklist before building. It then independently reruns the interface suite, a real backup and restore of at least 20,000 files and approximately 2 GiB, and the packaged Windows update/rollback test. Every restored file must match its original SHA-256. A missing or skipped large test fails the gate.

The final job downloads the candidate and checklist from that same run. It validates the checklist again and, for publication, checks its template against the current template on main. A newly added requirement therefore invalidates a candidate prepared under the older template. Only the publishing job receives repository write permission. It creates a new tag atomically and publishes the already-tested Windows bundle. Existing tags are rejected; a failure after tag creation needs investigation, not automatic tag deletion or retargeting.

A development-branch rehearsal can be dispatched directly through GitHub Actions with publishing off. This exercises the candidate gate without changing the main-only publishing rule. Its checklist must still truthfully record checks performed on that exact branch commit.

## Windows update package contract

The workflow embeds the requested tag's version in the executable and publishes `Backspace-windows-x64.zip`. Stable tags use three numeric components; prerelease tags are marked as prereleases and are not offered by the stable updater. Keep the bundle flat, with Backspace, Backrest, Restic and rclone executables together. The updater requires GitHub's SHA-256 asset digest and checks the executable's product version against the release tag. A missing digest or mismatched bundle must fail closed and offer the manual release page.

Verify the actual packaged download/restart flow on Windows, including an active scheduled run, cancellation, insufficient space, rollback and the recovery launcher from a folder containing spaces and non-ASCII characters. A mocked release response or a passing replacement unit test does not establish native acceptance.

## GitHub settings and scope

The `release` environment restricts publication to `main`. It must have no required human reviewers or waiting timer for this process. Preserve the branch restriction when updating its settings. The previous human-review requirement is superseded by the agent checklist.

The workflow is the enforced publishing route for agents. GitHub repository administrators can still change workflow or permission settings, and maintainers with release-write access can publish through other routes. This mechanism cannot make manual publication impossible for an administrator. Keep those privileges limited and require this workflow for every release.

Record actual configuration and rehearsal results in the local Review desk issue N011. Do not describe the new gate as exercised until the candidate workflow has passed a non-publishing rehearsal and its rejection cases have been tested. The older October 9 human-approval rehearsal is historical evidence for that earlier workflow, not for this checklist gate.

GitHub documents [deployment environments and their protection rules](https://docs.github.com/en/rest/deployments/environments). Live cloud, reboot and application-specific Windows checks remain separate where relevant; a large local round trip does not prove them.
