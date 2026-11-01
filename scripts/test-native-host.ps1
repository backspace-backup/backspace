param([Parameter(Mandatory=$true)][string]$OutputDirectory,[string]$Compiler=$env:BACKSPACE_NATIVE_COMPILER)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$output=[IO.Path]::GetFullPath($OutputDirectory)
& "$PSScriptRoot/build-native-host.ps1" -OutputDirectory $output -Compiler $Compiler
if(!$Compiler){$Compiler=Join-Path $repo 'src/Backspace.Host/obj/toolchain/zig-x86_64-windows-0.17.0/zig.exe'}
& $Compiler cc -target x86_64-windows-gnu -Os -municode -fno-sanitize=all -ffunction-sections -fdata-sections '-Wl,--gc-sections' -I $output (Join-Path $repo 'tests/Backspace.Host.Tests/verify-installer.c') -o "$output/verify-installer.exe" -lwinhttp -lbcrypt -lwintrust -lcrypt32 -lcomctl32 -lshell32 -lole32 -luuid -ladvapi32 -luser32
if($LASTEXITCODE){throw 'Native verification test failed to compile.'}
$pin=[IO.File]::ReadAllText((Join-Path $repo 'src/Backspace.Host/runtime.json'),[Text.Encoding]::UTF8)|ConvertFrom-Json
$fixture=Join-Path $output ('installer-test-'+[Guid]::NewGuid().ToString('N')+'.exe')
try{
    Invoke-WebRequest $pin.url -OutFile $fixture
    # The test verifies the genuine installer, holds a lock against replacement,
    # then damages this disposable copy and proves that it is rejected.
    # It NEVER executes the downloaded installer.
    & "$output/verify-installer.exe" $fixture
    if($LASTEXITCODE){throw 'Native installer verification checks failed.'}
}finally{
    if([IO.File]::Exists($fixture)){[IO.File]::Delete($fixture)}
}
