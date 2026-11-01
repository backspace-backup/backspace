param([Parameter(Mandatory = $true)][string]$ResultFile)

$ErrorActionPreference = 'Stop'
[xml]$result = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $ResultFile).Path, [Text.UTF8Encoding]::new($false))
$required = @(
    'Backspace.Tests.Services.E2E_WindowsUpdateBundle.Recovery_launcher_waits_for_native_failure_and_preserves_the_installation',
    'Backspace.Tests.Services.E2E_WindowsUpdateBundle.Real_portable_bundle_download_install_launch_and_rollback'
)
$counts = $result.TestRun.ResultSummary.Counters
$rows = @($result.TestRun.Results.UnitTestResult)
if ([int]$counts.passed -ne $required.Count -or [int]$counts.total -ne $required.Count -or
    [int]$counts.executed -ne $required.Count -or [int]$counts.failed -ne 0 -or $rows.Count -ne $required.Count) {
    throw 'Both native Windows update tests must execute and pass. Missing, skipped, failed or unexpected tests reject the candidate.'
}
foreach ($name in $required) {
    # xUnit's TRX display name can omit the namespace and class. Use the
    # linked TestMethod identity, never the human-readable result label.
    $definitions = @($result.TestRun.TestDefinitions.UnitTest | Where-Object {
        ($_.TestMethod.className + '.' + $_.TestMethod.name) -ceq $name
    })
    if ($definitions.Count -ne 1) { throw "Required native update test definition missing or duplicated: $name" }
    $matches = @($rows | Where-Object { $_.testId -ceq $definitions[0].id })
    if ($matches.Count -ne 1 -or $matches[0].outcome -cne 'Passed') {
        throw "Required native update check did not pass exactly once: $name"
    }
}
Write-Output 'Both required native Windows update tests ran and passed.'
