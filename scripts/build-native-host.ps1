param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [ValidateSet('host')][string]$Target='host',
    [string]$Compiler=$env:BACKSPACE_NATIVE_COMPILER
)
$ErrorActionPreference='Stop'
# Started from PowerShell 7 (as on GitHub's Windows runners), Windows
# PowerShell inherits a module path whose modules it cannot load; Get-FileHash
# then does not exist. Use the system module path and hash with .NET.
if($PSVersionTable.PSVersion.Major -lt 6){$env:PSModulePath=[Environment]::GetEnvironmentVariable('PSModulePath','Machine')}
function Get-Sha256([string]$path){$stream=[IO.File]::OpenRead($path);try{$sha=[Security.Cryptography.SHA256]::Create();try{([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')}finally{$sha.Dispose()}}finally{$stream.Dispose()}}
$repo=Split-Path $PSScriptRoot -Parent
$source=Join-Path $repo 'src/Backspace.Host'
$output=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output|Out-Null
if(!$Compiler){
    $cache=Join-Path $source 'obj/toolchain'
    $Compiler=Join-Path $cache 'zig-x86_64-windows-0.17.0/zig.exe'
    if(!(Test-Path -LiteralPath $Compiler)){
        New-Item -ItemType Directory -Force -Path $cache|Out-Null
        $archive=Join-Path $cache 'zig-0.17.0.zip'
        $expected='b5663f69581dcf391293fbf16c06cb80d81d806545ce618b4d0bab7f0eb8c428'
        if(!(Test-Path -LiteralPath $archive)){Invoke-WebRequest 'https://ziglang.org/download/0.17.0/zig-x86_64-windows-0.17.0.zip' -OutFile $archive}
        if((Get-Sha256 $archive) -ne $expected){throw 'The native compiler download did not match its pinned hash.'}
        Expand-Archive -LiteralPath $archive -DestinationPath $cache -Force
    }
}
if(!(Test-Path -LiteralPath $Compiler)){throw 'The configured native compiler does not exist.'}
if(!$env:ZIG_GLOBAL_CACHE_DIR){$env:ZIG_GLOBAL_CACHE_DIR=Join-Path $output 'compiler-cache'}
$env:ZIG_LOCAL_CACHE_DIR=$env:ZIG_GLOBAL_CACHE_DIR
$arguments=@('cc','-target','x86_64-windows-gnu','-Os','-municode','-fno-sanitize=all')
if($Target -eq 'host'){
    $runtime=[IO.File]::ReadAllText((Join-Path $source 'runtime.json'),[Text.Encoding]::UTF8)|ConvertFrom-Json
    if($runtime.url -notmatch '^https://builds\.dotnet\.microsoft\.com/dotnet/Runtime/10\.0\.\d+/dotnet-runtime-10\.0\.\d+-win-x64\.exe$' -or $runtime.sha512 -notmatch '^[a-f0-9]{128}$'){throw 'Invalid pinned runtime manifest.'}
    $bytes=for($n=0;$n -lt 128;$n+=2){'0x'+$runtime.sha512.Substring($n,2)}
    $header="#define RUNTIME_URL L`"$($runtime.url)`"`nstatic const unsigned char RUNTIME_SHA512[64] = { $($bytes -join ',') };`n"
    [IO.File]::WriteAllText((Join-Path $output 'runtime-config.h'),$header,[Text.UTF8Encoding]::new($false))
    $arguments+=@('-I',$output,(Join-Path $source 'host.c'),'-o',(Join-Path $output 'apphost.exe'),'-lwinhttp','-lbcrypt','-lwintrust','-lcrypt32','-lcomctl32','-lshell32','-lole32','-luuid','-ladvapi32','-luser32')
}
& $Compiler @arguments
if($LASTEXITCODE -ne 0){throw "Native $Target compilation failed ($LASTEXITCODE)."}
