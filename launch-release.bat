@echo off
REM Backspace - Release launcher
REM
REM Publishes a self-contained single-file .exe into .\dist\, then launches
REM it. The file in dist\ is what you'd share with other Windows users —
REM no .NET runtime required on their end.
REM
REM To build without launching, run the publish step directly:
REM   dotnet publish src\Backspace -c Release -f net10.0-windows10.0.19041.0 ^
REM       -r win-x64 --self-contained -p:PublishSingleFile=true ^
REM       -p:IncludeNativeLibrariesForSelfExtract=true -o dist

REM === Toggles ============================================================
REM Set to 1 to copy a pre-populated Backspace.config\ folder into dist\
REM before launching the exe — gives the freshly-published instance some
REM test data (configured backups, destinations, history) so you don't have
REM to re-create them on every Release rebuild. Set to 0 to launch with a
REM virgin config (the exe will create an empty Backspace.config\ on
REM first run).
set "SEED_TEST_DATA=1"
set "SEED_SOURCE=%~dp0Backspace.config"
REM ========================================================================

setlocal EnableDelayedExpansion
cd /d "%~dp0"

set "PROJ=src\Backspace\Backspace.csproj"
set "OUT=dist"
set "EXE=%OUT%\Backspace.exe"

REM No self-elevation. Backspace.exe is now asInvoker (see app.manifest
REM for the rationale — Kopia-style "run as user; Mount drives go into
REM the user's session and are visible to Explorer; backups that need
REM VSS use Task Scheduler's highest-available privileges or a
REM right-click → Run as administrator override"). taskkill from a
REM non-elevated shell can terminate an asInvoker Backspace just fine
REM since they share an integrity level. If you DID launch Backspace
REM elevated (right-click → Run as admin to get VSS for an interactive
REM backup), re-run this script elevated too — otherwise the kill step
REM below will see "Access denied" and bail.

REM ----------------------------------------------------------------------
REM 1. Kill every Backspace-related process. The Backspace.exe parent
REM    is easy; the tricky one is backrest.exe (and the rclone/restic
REM    children Backrest spawns) — older builds without job-object
REM    parenting leak the engine binary as an orphan after Backspace
REM    quits, and it keeps a handle on dist\Backspace.config\backrest\
REM    sqlite + log files. If we don't kill that orphan FIRST, the
REM    rd /s /q below fails on file-in-use and the publish quietly
REM    leaves the old binaries behind.
REM
REM    `taskkill /T` kills the process tree, /F is force; we do it
REM    once per name in case the process is a sibling rather than a
REM    descendant. Errors are suppressed because /IM against a name
REM    that doesn't currently exist returns non-zero.
REM ----------------------------------------------------------------------
echo [launch-release] Stopping any running Backspace + engine processes ...
taskkill /F /T /IM Backspace.exe >nul 2>&1
taskkill /F /T /IM backrest.exe  >nul 2>&1
taskkill /F /T /IM restic.exe    >nul 2>&1
taskkill /F /T /IM rclone.exe    >nul 2>&1

REM Verify they're actually gone before continuing. tasklist exits 0
REM when at least one matching process exists; we poll for up to 10 s.
set /a "KILL_TRIES=0"
:WAIT_KILL
set "STILL_RUNNING="
for %%P in (Backspace.exe backrest.exe restic.exe rclone.exe) do (
    tasklist /FI "IMAGENAME eq %%P" 2>nul | find /I "%%P" >nul && set "STILL_RUNNING=%%P"
)
if not defined STILL_RUNNING goto KILL_DONE
set /a "KILL_TRIES+=1"
if !KILL_TRIES! GEQ 10 goto KILL_ESCALATE
REM Re-issue the kill in case the process spawned a child between
REM our last call and this poll.
taskkill /F /T /IM !STILL_RUNNING! >nul 2>&1
timeout /t 1 /nobreak >nul
goto WAIT_KILL

:KILL_ESCALATE
echo.
echo [launch-release] taskkill couldn't end !STILL_RUNNING! after 10 s.
echo                    Trying PowerShell Stop-Process as a last resort ...
powershell -NoProfile -Command "Get-Process -Name 'Backspace','backrest','restic','rclone' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 800"
set "STILL_RUNNING="
for %%P in (Backspace.exe backrest.exe restic.exe rclone.exe) do (
    tasklist /FI "IMAGENAME eq %%P" 2>nul | find /I "%%P" >nul && set "STILL_RUNNING=%%P"
)
if not defined STILL_RUNNING goto KILL_DONE
echo.
echo [launch-release] Still alive after PowerShell fallback.
echo                    The Backspace process is in a zombie state that
echo                    even elevated TerminateProcess cannot end. Two
echo                    options:
echo                      1. Reboot - the next launch will be clean.
echo                      2. Open Task Manager Ctrl+Shift+Esc, find the
echo                         Backspace.exe entry, End Task. Re-run this.
goto FAIL

:KILL_DONE

REM ----------------------------------------------------------------------
REM 2. Wipe the previous dist\. Windows can take a beat to release
REM    handles after taskkill returns (anti-virus, indexer, etc.),
REM    so retry the delete with backoff. The publish step needs an
REM    EMPTY dist\ to avoid the single-file extractor seeing a
REM    stale BACKSP~1.CON entry it can't overwrite.
REM ----------------------------------------------------------------------
echo [launch-release] Clearing %OUT%\ ...
set /a "RM_TRIES=0"
:RM_RETRY
if not exist "%OUT%" goto RM_DONE
rd /s /q "%OUT%" 2>nul
if not exist "%OUT%" goto RM_DONE
set /a "RM_TRIES+=1"
if %RM_TRIES% GEQ 6 (
    echo [launch-release] FAILED to clear %OUT%\ after 6 retries.
    echo                    Something still has a handle open — try a fresh shell or reboot.
    goto FAIL
)
echo [launch-release]   wipe retry %RM_TRIES% / 6 (sleeping 2 s) ...
timeout /t 2 /nobreak >nul
goto RM_RETRY
:RM_DONE

echo [launch-release] Publishing single-file Release to %OUT%\ ...
REM Backspace multi-targets net10.0-windows10.0.19041.0 (full Win32
REM stack) and net10.0 (cross-platform fallback). dotnet publish requires
REM an explicit -f when more than one TFM is declared.
dotnet publish "%PROJ%" -c Release -f net10.0-windows10.0.19041.0 ^
    -r win-x64 --self-contained ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "%OUT%"
if errorlevel 1 goto FAIL

if not exist "%EXE%" (
    echo [launch-release] Publish succeeded but exe is missing at "%EXE%".
    goto FAIL
)

echo.
echo [launch-release] Built: %CD%\%EXE%

REM Seed dist\Backspace.config\ with test data BEFORE launch so the
REM newly-started exe reads a populated config on first init.
if "%SEED_TEST_DATA%"=="1" (
    if exist "%SEED_SOURCE%" (
        echo [launch-release] Seeding %OUT%\Backspace.config\ from %SEED_SOURCE% ...
        REM /E       - copy subfolders, including empty ones
        REM /I       - assume destination is a folder
        REM /Y       - overwrite existing files without prompting
        REM /Q       - quiet (suppress per-file listing)
        xcopy "%SEED_SOURCE%" "%OUT%\Backspace.config" /E /I /Y /Q >nul
        if errorlevel 1 echo [launch-release] WARN: seed copy reported errors; continuing.
    ) else (
        echo [launch-release] SEED_TEST_DATA=1 but source folder not found at "%SEED_SOURCE%" — skipping.
    )
)

echo [launch-release] Launching...
start "" "%EXE%"

echo.
echo [launch-release] Distribute dist\Backspace.exe to share.
endlocal
exit /b 0

:FAIL
echo.
echo [launch-release] FAILED. See output above.
pause
endlocal
exit /b 1
