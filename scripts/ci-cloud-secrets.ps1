# Stores the cloud test tokens of tests/Backspace.Tests/secrets.env as the
# repository's encrypted GitHub secrets, for the Linux and macOS test runs
# (U244). Values go to `gh secret set` on its standard input: they are never
# printed, written elsewhere or put in the repository. Use throwaway accounts.
#
#   powershell -File scripts/ci-cloud-secrets.ps1          # store
#   powershell -File scripts/ci-cloud-secrets.ps1 -Remove  # delete them from GitHub
param([switch]$Remove)
$ErrorActionPreference = 'Stop'
$repo = 'backspace-backup/backspace'
$keys = 'DROPBOX_TEST_TOKEN', 'ONEDRIVE_TEST_TOKEN', 'GOOGLEDRIVE_TEST_TOKEN'
if ($Remove) {
    foreach ($k in $keys) { gh secret delete $k --repo $repo 2>$null; Write-Host "$k removed (if it was there)." }
    return
}
$file = Join-Path $PSScriptRoot '..\tests\Backspace.Tests\secrets.env'
if (-not (Test-Path $file)) { throw "No secrets.env yet: sign in with the throwaway accounts first." }
$values = @{}
foreach ($line in [System.IO.File]::ReadAllLines($file, [System.Text.UTF8Encoding]::new($false))) {
    $eq = $line.IndexOf('=')
    if ($line.TrimStart().StartsWith('#') -or $eq -lt 1) { continue }
    $values[$line.Substring(0, $eq).Trim()] = $line.Substring($eq + 1).Trim()
}
foreach ($k in $keys) {
    if ([string]::IsNullOrWhiteSpace($values[$k])) { Write-Host "$k`: not in secrets.env, skipped."; continue }
    $values[$k] | gh secret set $k --repo $repo
    if ($LASTEXITCODE -ne 0) { throw "$k could not be stored." }
    Write-Host "$k stored on GitHub (value not shown)."
}
