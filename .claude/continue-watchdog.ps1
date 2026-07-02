# Continue-watchdog: external heartbeat so the user has a visible
# forcing function when the in-harness ScheduleWakeup is flaky.
#
# How to use:
#   1. Open a fresh PowerShell window (NOT the Claude Code one).
#   2. cd to this repo.
#   3. .\.claude\continue-watchdog.ps1
#   4. Leave the window open. Every minute it prints a timestamp +
#      reminder; every 5 minutes it bell-rings (Console.Beep) so an
#      idle window catches your eye.
#
# When the heartbeat keeps printing but no chat activity has happened
# for a while, paste "continue if not completed" into the Claude
# Code chat to re-prompt me. The harness can't be triggered remotely
# from this script, but the visible heartbeat is the forcing function.
#
# The script also writes a heartbeat file at
# .claude/continue-watchdog.heartbeat.txt that I check on every turn.

$ErrorActionPreference = 'Continue'

$scriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$heartbeat   = Join-Path $scriptDir 'continue-watchdog.heartbeat.txt'
$intervalSec = 60
$beepEvery   = 5  # beep on every Nth tick

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " Backspace continue-watchdog — heartbeat every $intervalSec s" -ForegroundColor Cyan
Write-Host " Heartbeat file: $heartbeat"                                   -ForegroundColor Gray
Write-Host " Stop with Ctrl-C."                                            -ForegroundColor Gray
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

$tick = 0
while ($true)
{
    $tick++
    $now  = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $line = "[$now] tick #$tick — paste 'continue if not completed' into Claude Code if I've gone silent."

    Write-Host $line -ForegroundColor Yellow
    try { $line | Out-File -FilePath $heartbeat -Encoding utf8 -Force } catch { }

    if (($tick % $beepEvery) -eq 0)
    {
        try { [Console]::Beep(880, 200) } catch { }
    }

    Start-Sleep -Seconds $intervalSec
}
