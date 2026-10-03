# Scenario tests — visual + interaction E2E suite

Headless walk-throughs of every important user flow. Each scenario:

1. Creates throwaway source / destination / restore directories under
   `%TEMP%/backspace-scenarios/<name>-<id>/`, outside the synced checkout.
2. Boots the real Avalonia views (same DI graph as the GUI).
3. Drives a scripted user flow (`ctx.Driver.ClickButton(...)`,
   `ctx.Driver.EnterText(...)`, or direct VM pokes for UI-awkward bits).
4. Renders a PNG at every interesting step via `ctx.Snapshot(window, "label")`.
5. Records missing-secret skips. Cloud scenarios may acquire missing tokens
   interactively unless `BACKSPACE_E2E_NONINTERACTIVE=1` is set.

The suite-level `ScenarioSuiteFixture` writes
`artifacts/scenarios/SUMMARY.md` after every test run with the
step-completion/skip/failure breakdown, missing-secret table, and per-step
screenshot index. Inspect the runner/TRX verdict too: scenario completion
does not include later runner teardown, and a recorded scenario skip may
appear as a passing runner result under xUnit v2.

## Running

```powershell
# Run scenarios without opening provider-consent screens:
$env:BACKSPACE_E2E_NONINTERACTIVE = '1'
dotnet test tests/Backspace.Tests --filter "FullyQualifiedName~Scenarios"

# One scenario:
dotnet test tests/Backspace.Tests --filter "FullyQualifiedName~S03_TwoSourcesLocalAndDropbox"

# Open the report:
start tests/Backspace.Tests/artifacts/scenarios/SUMMARY.md
```

## Adding a scenario

1. Create `Sxx_DescriptiveName.cs` in this folder.
2. Subclass `ScenarioBase`. Override `ScenarioName` and `RunAsync`.
3. Mark with `[Collection(ScenarioCollection.Name)]` so xUnit groups
   it under the assembly fixture.
4. Add a single `[AvaloniaFact] public async Task Run_Sxx() => await Run();`
   so xUnit discovers it.
5. Inside `RunAsync`:
   - Use `ctx.MakeSource(...)`, `ctx.MakeLocalDestination(...)` to
     materialise throwaway directories.
   - Drive UI via `ctx.Driver.ClickButton(view, "Save")`,
     `ctx.Driver.EnterText(view, "MyTextBox", "value")`, etc.
   - Call `ctx.Snapshot(window, "label", "optional human note")` at
     each interesting moment.
   - For secret-gated flows: `if (!ctx.Secrets.TryGetDropboxToken(out var t)) { ctx.Skip("…"); return; }`.

## Secrets

Credentials live in the ignored `tests/Backspace.Tests/secrets.env` file,
one `KEY=value` per line. The scenario loader creates a commented template
if the file is absent. The cloud E2E loader also reads environment overrides.
`E2EConfig.AlwaysShowOAuthUx` defaults to false; enabling it requests fresh
provider consent even when a saved token exists. The acquirer shares the
production OAuth coordinator but does not itself render the destination editor.

Missing secrets do not fail the suite — they are recorded in
`SUMMARY.md` so the user knows exactly which file to drop in to
unlock more coverage on the next run. Unset `BACKSPACE_E2E_NONINTERACTIVE`
only when deliberately running live account tests with a person available.

## Iterating with /loop

The user's standing rule: *they should not be the QA loop*. The
agent runs the suite, opens SUMMARY.md + select PNGs, identifies
issues, fixes them in the source, re-runs the suite. Recommended
`/loop` prompt:

```
/loop Run the scenario tests
(`dotnet test tests/Backspace.Tests --filter Scenarios`),
read the SUMMARY.md, view at least the screenshots flagged by the
auditor as anomalous, identify any visual or behavioural issues
(blank frames, clipped controls, wrong colours, broken hover
states), fix them in src/Backspace, and run again. Stop when
the runner/TRX and scenario reports have no unexplained failures or anomalies;
report every skipped or unexercised flow explicitly.
```

Use sparingly — each iteration spends real time + cost. The
intention is "polish a specific area to perfection", not "run
forever".

## Evidence boundaries

- **Real local engine:** `Scenarios/E2E` and `E2E/LocalBackupRestoreTests.cs`
  use bundled Backrest/restic with disposable repositories. Most `Sxx`
  scenarios use an in-memory engine; check each report's scope.
- **OAuth:** local callback/provider simulations exercise deterministic
  contracts and editor controls. E07–E10 require live accounts for actual
  consent, token refresh, cloud backup and byte-verified restore.
- **Windows scheduling:** definition tests and actual generated-action
  process probes prove arguments, execution and exit handling. Real task
  registration, S4U permissions and sleep/reboot firing remain OS acceptance.

## October 2026 repair safeguards

Mutable fixtures now live in a unique `%TEMP%/backspace-scenarios/<name>-<id>/` directory outside the synced checkout. Screenshots stay under `artifacts/scenarios/<name>/screenshots/`. The HTML/Markdown report labels completed scenario steps as **STEPS COMPLETED**, not a full test verdict; later runner teardown is covered only by the TRX result.

The shared source builders and manually shaped E11/E30/E31/E34/E35 fixtures honor `BACKSPACE_E2E_SCALE=huge` (20,000 files, approximately 2 GiB per source fixture). Real restore validation compares every regular file with SHA-256. Use the small default for iteration and opt into the expensive suite before release. Live account, native elevation and reboot results must not be inferred from deterministic provider or UI fixtures.
