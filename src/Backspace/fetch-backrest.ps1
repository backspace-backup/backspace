# Downloads the host-arch backrest + restic + rclone executables from
# their respective GitHub releases / official download URLs and writes
# them to the given -*OutPath. Invoked by FetchBackrest.targets.
#
# Self-contained: no dependencies outside stock Windows PowerShell 5.1.
# Re-runs are idempotent — each binary is skipped if it already exists.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $BackrestOutPath,
    [Parameter(Mandatory = $true)] [string] $ResticOutPath,
    [Parameter(Mandatory = $true)] [string] $RcloneOutPath
)

$ErrorActionPreference = 'Stop'

# A build launched by PowerShell 7 can pass its module search path to
# this Windows PowerShell 5.1 child. Restore the child's own modules so
# Get-FileHash and Expand-Archive resolve on a fresh GitHub runner.
$env:PSModulePath = (Join-Path $PSHOME 'Modules') + [IO.Path]::PathSeparator + $env:PSModulePath

# Tls12 is the minimum GitHub / rclone downloads accept.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$headers = @{ 'User-Agent' = 'Backspace-build' }
# GitHub limits anonymous API requests per address, and hosted runners share
# addresses. In GitHub Actions, send the workflow's token to the API host only;
# asset downloads keep the anonymous headers.
$apiHeaders = $headers.Clone()
if ($env:GITHUB_ACTIONS -eq 'true' -and $env:GITHUB_TOKEN) { $apiHeaders['Authorization'] = "Bearer $env:GITHUB_TOKEN" }
$versions = Get-Content (Join-Path $PSScriptRoot 'engine-versions.json') -Raw -Encoding UTF8 | ConvertFrom-Json

# Detect arch. On Windows we support x86_64 + arm64. Default to x86_64
# if anything else (32-bit hosts can't run the .NET 10 build anyway).
$arch = if ([Environment]::Is64BitOperatingSystem) {
    if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') { 'arm64' } else { 'x86_64' }
} else { 'x86_64' }

function Ensure-Dir([string] $path) {
    $dir = Split-Path -Parent $path
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
}

function Fetch-FromGitHubRelease {
    param(
        [string] $RepoSlug,         # e.g. 'garethgeorge/backrest'
        [string] $ReleaseTag,
        [string] $AssetPattern,     # regex matching the asset filename
        [string] $InnerBinaryName,  # filename inside the archive to extract
        [string] $OutPath
    )

    Write-Host "[fetch-backrest] Querying GitHub for $RepoSlug $ReleaseTag..."
    $release = Invoke-RestMethod `
        -Uri "https://api.github.com/repos/$RepoSlug/releases/tags/$ReleaseTag" `
        -Headers $apiHeaders -TimeoutSec 30

    $asset = $release.assets | Where-Object { $_.name -match $AssetPattern } | Select-Object -First 1
    if (-not $asset) {
        throw "Could not find an asset matching '$AssetPattern' in $RepoSlug $ReleaseTag."
    }

    Write-Host ("[fetch-backrest] Downloading {0} ({1:N1} MB)" -f $asset.name, ($asset.size / 1MB))

    Ensure-Dir $OutPath
    $tmpDir = Join-Path ([IO.Path]::GetTempPath()) ("backspace-fetch-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $tmpDir | Out-Null
    try {
        $archivePath = Join-Path $tmpDir $asset.name
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $archivePath -Headers $headers -TimeoutSec 600 -UseBasicParsing

        # Supply-chain check: stamp the downloaded archive's SHA-256
        # to the build log AND compare it against a pinned manifest
        # if one is present at fetch-manifest.json next to the
        # script. The manifest binds <repoSlug>+<assetName> to an
        # expected SHA-256; mismatch throws so a tampered or
        # unexpectedly-replaced upstream asset doesn't ship silently.
        # A missing manifest (or a missing entry inside it) logs a
        # warning but doesn't block — the bootstrap path for a brand
        # new dev is "no manifest yet; record the hash you got".
        $hash = (Get-FileHash -Path $archivePath -Algorithm SHA256).Hash
        Write-Host ("[fetch-backrest]   sha256 = {0}" -f $hash)
        $manifestPath = Join-Path $PSScriptRoot 'fetch-manifest.json'
        if (Test-Path $manifestPath) {
            try {
                $manifest = Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
                $expected = $manifest.PSObject.Properties[$RepoSlug + '/' + $asset.name]
                if ($expected) {
                    if ($expected.Value -ne $hash) {
                        throw ("Supply-chain integrity failure: $RepoSlug/$($asset.name) hash $hash does not match the pinned value $($expected.Value) in fetch-manifest.json. Refusing to use.")
                    }
                    Write-Host "[fetch-backrest]   sha256 matches pinned manifest entry"
                } else {
                    Write-Host "[fetch-backrest]   WARN: no manifest entry for $RepoSlug/$($asset.name); add the hash above to fetch-manifest.json to pin"
                }
            } catch {
                # A mismatch or malformed manifest must not become a warning.
                throw
            }
        } else {
            Write-Host "[fetch-backrest]   note: no fetch-manifest.json present; copy the sha256 above into one to enable pinning"
        }

        # Extract — handle .zip natively; .tar.gz / .bz2 not expected on Windows hosts.
        if ($asset.name -like '*.zip') {
            Expand-Archive -Path $archivePath -DestinationPath $tmpDir -Force
        } else {
            throw "Unsupported archive format for asset '$($asset.name)' on Windows host."
        }

        $inner = Get-ChildItem -Path $tmpDir -Recurse -Filter $InnerBinaryName | Select-Object -First 1
        if (-not $inner) { throw "Archive '$($asset.name)' did not contain '$InnerBinaryName'." }
        Copy-Item -Path $inner.FullName -Destination $OutPath -Force
        Write-Host ("[fetch-backrest] Wrote {0}" -f $OutPath)
    } finally {
        Remove-Item -Path $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Fetch-Rclone {
    param([string] $Arch, [string] $OutPath)

    # Use the reviewed version, never the moving "current" alias.
    $rcloneArch = if ($Arch -eq 'arm64') { 'arm64' } else { 'amd64' }
    $version = $versions.rclone
    $url = "https://downloads.rclone.org/$version/rclone-$version-windows-$rcloneArch.zip"

    Write-Host "[fetch-backrest] Downloading rclone from $url"

    Ensure-Dir $OutPath
    $tmpDir = Join-Path ([IO.Path]::GetTempPath()) ("backspace-fetch-rclone-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $tmpDir | Out-Null
    try {
        $archivePath = Join-Path $tmpDir 'rclone.zip'
        Invoke-WebRequest -Uri $url -OutFile $archivePath -Headers $headers -TimeoutSec 600 -UseBasicParsing
        Expand-Archive -Path $archivePath -DestinationPath $tmpDir -Force

        $inner = Get-ChildItem -Path $tmpDir -Recurse -Filter 'rclone.exe' | Select-Object -First 1
        if (-not $inner) { throw "rclone archive did not contain rclone.exe." }
        Copy-Item -Path $inner.FullName -Destination $OutPath -Force
        Write-Host ("[fetch-backrest] Wrote {0}" -f $OutPath)
    } finally {
        Remove-Item -Path $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if (-not (Test-Path $BackrestOutPath)) {
    Fetch-FromGitHubRelease `
        -RepoSlug 'garethgeorge/backrest' `
        -ReleaseTag $versions.backrest `
        -AssetPattern "^backrest_Windows_$arch\.zip$" `
        -InnerBinaryName 'backrest.exe' `
        -OutPath $BackrestOutPath
} else {
    Write-Host "[fetch-backrest] $BackrestOutPath already present, skipping."
}

if (-not (Test-Path $ResticOutPath)) {
    $resticArch = if ($arch -eq 'arm64') { 'arm64' } else { 'amd64' }
    Fetch-FromGitHubRelease `
        -RepoSlug 'restic/restic' `
        -ReleaseTag $versions.restic `
        -AssetPattern "^restic_[0-9.]+_windows_$resticArch\.zip$" `
        -InnerBinaryName 'restic_*.exe' `
        -OutPath $ResticOutPath
} else {
    Write-Host "[fetch-backrest] $ResticOutPath already present, skipping."
}

if (-not (Test-Path $RcloneOutPath)) {
    Fetch-Rclone -Arch $arch -OutPath $RcloneOutPath
} else {
    Write-Host "[fetch-backrest] $RcloneOutPath already present, skipping."
}
