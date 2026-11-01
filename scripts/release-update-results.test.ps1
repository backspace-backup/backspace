$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('backspace-update-gate-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$script = Join-Path $PSScriptRoot 'release-update-results.ps1'
$first = 'Backspace.Tests.Services.E2E_WindowsUpdateBundle.Recovery_launcher_waits_for_native_failure_and_preserves_the_installation'
$second = 'Backspace.Tests.Services.E2E_WindowsUpdateBundle.Real_portable_bundle_download_install_launch_and_rollback'
$goodRows = "<UnitTestResult testName='$first' outcome='Passed'/><UnitTestResult testName='$second' outcome='Passed'/>"
$cases = @(
    @{ Name = 'both required tests pass'; Rows = $goodRows; Total = 2; Passed = 2; Executed = 2; Failed = 0; Accept = $true },
    @{ Name = 'old one-test result'; Rows = "<UnitTestResult testName='$second' outcome='Passed'/>"; Total = 1; Passed = 1; Executed = 1; Failed = 0; Accept = $false },
    @{ Name = 'a skipped test'; Rows = $goodRows.Replace("testName='$first' outcome='Passed'", "testName='$first' outcome='NotExecuted'"); Total = 2; Passed = 1; Executed = 1; Failed = 0; Accept = $false },
    @{ Name = 'a failed test'; Rows = $goodRows.Replace("testName='$first' outcome='Passed'", "testName='$first' outcome='Failed'"); Total = 2; Passed = 1; Executed = 2; Failed = 1; Accept = $false },
    @{ Name = 'unrelated test replacing a required test'; Rows = $goodRows.Replace($first, 'Unrelated.Test'); Total = 2; Passed = 2; Executed = 2; Failed = 0; Accept = $false },
    @{ Name = 'duplicate replacing a required test'; Rows = $goodRows.Replace($first, $second); Total = 2; Passed = 2; Executed = 2; Failed = 0; Accept = $false },
    @{ Name = 'passing counters conceal skipped row'; Rows = $goodRows.Replace("testName='$first' outcome='Passed'", "testName='$first' outcome='NotExecuted'"); Total = 2; Passed = 2; Executed = 2; Failed = 0; Accept = $false },
    @{ Name = 'unexpected additional test'; Rows = $goodRows + "<UnitTestResult testName='Unexpected.Test' outcome='Passed'/>"; Total = 3; Passed = 3; Executed = 3; Failed = 0; Accept = $false },
    @{ Name = 'empty run'; Rows = ''; Total = 0; Passed = 0; Executed = 0; Failed = 0; Accept = $false }
)
foreach ($case in $cases) {
    $file = Join-Path $root ($case.Name.Replace(' ', '-') + '.trx')
    [xml]$fixtureRows = "<Results>$($case.Rows)</Results>"
    $definitions = ''; $rows = ''; $index = 0
    foreach ($row in @($fixtureRows.Results.UnitTestResult)) {
        if ($null -eq $row) { continue }
        $index++; $split = $row.testName.LastIndexOf('.')
        $class = $row.testName.Substring(0, $split); $method = $row.testName.Substring($split + 1)
        $definitions += "<UnitTest id='$index'><TestMethod className='$class' name='$method'/></UnitTest>"
        $rows += "<UnitTestResult testId='$index' testName='$method' outcome='$($row.outcome)'/>"
    }
    $xml = "<TestRun xmlns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010'><Results>$rows</Results><TestDefinitions>$definitions</TestDefinitions><ResultSummary><Counters total='$($case.Total)' passed='$($case.Passed)' executed='$($case.Executed)' failed='$($case.Failed)'/></ResultSummary></TestRun>"
    [IO.File]::WriteAllText($file, $xml, [Text.UTF8Encoding]::new($false))
    $accepted = $false
    try { & $script -ResultFile $file | Out-Null; $accepted = $true } catch { if ($case.Accept) { throw } }
    if ($accepted -ne $case.Accept) { throw "Wrong gate result: $($case.Name)" }
    Write-Output "PASS: $($case.Name)"
}
Write-Output "$($cases.Count) release update-result checks passed. Fixtures: $root"
