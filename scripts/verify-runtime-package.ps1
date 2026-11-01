param([Parameter(Mandatory=$true)][string]$Executable,[string]$ResultFile)
$ErrorActionPreference='Stop'
# Started from PowerShell 7 (as on GitHub's Windows runners), Windows
# PowerShell inherits a module path whose modules it cannot load; Get-FileHash
# then does not exist. Use the system module path and hash with .NET.
if($PSVersionTable.PSVersion.Major -lt 6){$env:PSModulePath=[Environment]::GetEnvironmentVariable('PSModulePath','Machine')}
function Get-Sha256([string]$path){$stream=[IO.File]::OpenRead($path);try{$sha=[Security.Cryptography.SHA256]::Create();try{([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')}finally{$sha.Dispose()}}finally{$stream.Dispose()}}
$path=[IO.Path]::GetFullPath($Executable)
$bytes=[IO.File]::ReadAllBytes($path)
[byte[]]$signature=0x8b,0x12,0x02,0xb9,0x6a,0x61,0x20,0x38,0x72,0x7b,0x93,0x02,0x14,0xd7,0xa0,0x32,0x13,0xf5,0xb9,0xe6,0xef,0xae,0x33,0x18,0xee,0x3b,0x2d,0xce,0x24,0xb3,0x6a,0xae
# The SDK places this locator in the native PE, not among the bundled DLLs.
$offset=$null
for($i=8;$i -lt [Math]::Min($bytes.Length-$signature.Length,2000000);$i++){
 if($bytes[$i] -ne $signature[0]){continue}
 $matches=$true;for($j=1;$j -lt $signature.Length;$j++){if($bytes[$i+$j] -ne $signature[$j]){$matches=$false;break}}
 if($matches){$offset=[BitConverter]::ToInt64($bytes,$i-8);break}
}
if(!$offset -or $offset -ge $bytes.Length){throw 'The candidate is not a recognized single-file bundle.'}
$stream=[IO.MemoryStream]::new($bytes,$false);$reader=[IO.BinaryReader]::new($stream)
try{
 $stream.Position=$offset;$major=$reader.ReadUInt32();$minor=$reader.ReadUInt32();$count=$reader.ReadInt32();$bundleId=$reader.ReadString()
 if($major -ne 6 -or $minor -ne 0 -or $count -lt 1 -or $count -gt 10000){throw 'Unexpected bundle format; update the package inspector before releasing.'}
 $null=$reader.ReadInt64();$null=$reader.ReadInt64();$configOffset=$reader.ReadInt64();$configSize=$reader.ReadInt64();$null=$reader.ReadUInt64()
 $entries=@();for($i=0;$i -lt $count;$i++){$entryOffset=$reader.ReadInt64();$size=$reader.ReadInt64();$compressed=$reader.ReadInt64();$kind=$reader.ReadByte();$name=$reader.ReadString();$entries+=@{Name=$name;Offset=$entryOffset;Size=$size;Compressed=$compressed;Kind=$kind}}
 $forbidden=@($entries|Where-Object{$_.Name -match '(^|[/\\])(coreclr|clrjit|hostfxr|hostpolicy)\.dll$|(^|[/\\])System\.Private\.CoreLib\.dll$'})
 if($forbidden.Count){throw ('The candidate bundles .NET runtime files: '+(($forbidden|ForEach-Object Name)-join ', '))}
 if($configOffset -lt 0 -or $configSize -lt 1 -or $configSize -gt 65536 -or $configOffset+$configSize -gt $bytes.Length){throw 'Invalid runtime configuration bounds.'}
 $configuration=[Text.Encoding]::UTF8.GetString($bytes,[int]$configOffset,[int]$configSize)|ConvertFrom-Json
 if($configuration.runtimeOptions.framework.name -ne 'Microsoft.NETCore.App' -or $configuration.runtimeOptions.framework.version -notlike '10.0.*' -or $configuration.runtimeOptions.includedFrameworks){throw 'The candidate does not declare the expected external .NET 10 runtime.'}
 $nativeText=[Text.Encoding]::Unicode.GetString($bytes,0,[int][Math]::Min($offset,1000000))
 if(!$nativeText.Contains('Backspace runtime setup/1')){throw 'The native runtime-setup host is missing.'}
 $result=@{Passed=$true;BundleVersion="$major.$minor";EntryCount=$count;Runtime=$configuration.runtimeOptions.framework;BundledRuntimeFiles=$forbidden;Bytes=$bytes.Length;Sha256=(Get-Sha256 $path)}
 if($ResultFile){$result|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $ResultFile -Encoding UTF8}
 $result|ConvertTo-Json -Depth 5
}finally{$reader.Dispose();$stream.Dispose()}
