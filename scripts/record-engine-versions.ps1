param(
    [Parameter(Mandatory=$true)][string]$SourceDirectory,
    [Parameter(Mandatory=$true)][string]$ResultFile,
    [int]$TimeoutSeconds=30
)
$ErrorActionPreference = 'Stop'
$lines = [Collections.Generic.List[string]]::new()
foreach ($item in @(@('restic.exe','version'), @('backrest.exe','--windows-tray=false --version'), @('rclone.exe','version'))) {
    $file = Join-Path ([IO.Path]::GetFullPath($SourceDirectory)) $item[0]
    Write-Host "Recording version: $($item[0])"
    $start = [Diagnostics.ProcessStartInfo]::new($file)
    $start.Arguments = $item[1]
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw "Could not start $file" }
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill()
            throw "Version query timed out for $($item[0])."
        }
        $text = $output.GetAwaiter().GetResult().Trim()
        $errors = $errorOutput.GetAwaiter().GetResult().Trim()
        if ($process.ExitCode -ne 0 -or -not $text) { throw "Version query failed for $($item[0]): $errors" }
        $lines.Add($text)
        Write-Host $text
    } finally { $process.Dispose() }
}
[IO.File]::WriteAllLines([IO.Path]::GetFullPath($ResultFile), $lines, [Text.UTF8Encoding]::new($false))
