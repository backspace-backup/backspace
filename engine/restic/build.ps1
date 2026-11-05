# Builds Backspace's Windows restic: the official restic release named in
# src/Backspace/engine-versions.json ("restic"), with the patches in
# engine/restic/patches applied, reported as "<version>-backspace.<n>".
# Used by .github/workflows/build-restic.yml; also runs locally with Go and Git.
param(
    [string] $OutDir = (Join-Path $PSScriptRoot 'out'),
    [string] $Arch = 'amd64'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$versions = Get-Content (Join-Path $repo 'src/Backspace/engine-versions.json') -Raw | ConvertFrom-Json
$tag = $versions.restic                      # e.g. v0.19.1
$build = $versions.resticBackspace           # e.g. v0.19.1-backspace.1
if (-not $build -or -not $build.StartsWith($tag + '-backspace.')) { throw "engine-versions.json: resticBackspace ($build) must be $tag-backspace.<n>" }
$version = $build.Substring(1)

$work = Join-Path ([IO.Path]::GetTempPath()) ('restic-build-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    # The official source at the release tag, verified by Git's object ids.
    git -c advice.detachedHead=false -c core.autocrlf=false clone --quiet --config core.autocrlf=false --depth 1 --branch $tag https://github.com/restic/restic.git $work
    if ($LASTEXITCODE -ne 0) { throw "git clone $tag failed" }
    Push-Location $work
    try {
        foreach ($patch in Get-ChildItem (Join-Path $PSScriptRoot 'patches') -Filter *.patch | Sort-Object Name) {
            git apply --whitespace=nowarn $patch.FullName
            if ($LASTEXITCODE -ne 0) { throw "patch $($patch.Name) does not apply to $tag" }
        }
        # Mark the build in "restic version" (Backrest reads only the x.y.z part).
        $global = Join-Path $work 'internal/global/global.go'
        $text = [IO.File]::ReadAllText($global)
        $marked = $text -replace ('const Version = "' + [regex]::Escape($tag.Substring(1)) + '"'), ('const Version = "' + $version + '"')
        if ($marked -eq $text) { throw "could not set the version in $global" }
        [IO.File]::WriteAllText($global, $marked)
        go test ./internal/filter/
        if ($LASTEXITCODE -ne 0) { throw 'restic filter tests failed' }
        $env:CGO_ENABLED = '0'; $env:GOOS = 'windows'; $env:GOARCH = $Arch
        New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
        $name = "restic_${version}_windows_$Arch"
        $exe = Join-Path $OutDir "$name.exe"
        go build -trimpath -ldflags '-s -w' -o $exe ./cmd/restic
        if ($LASTEXITCODE -ne 0) { throw 'go build failed' }
    }
    finally { Pop-Location }
    $zip = Join-Path $OutDir "$name.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path $exe -DestinationPath $zip
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path "$zip.sha256" -Value "$hash  $name.zip" -Encoding ascii
    & $exe version
    Write-Host "Built $zip ($hash)"
}
finally { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
