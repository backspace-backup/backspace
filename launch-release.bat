@echo off
setlocal DisableDelayedExpansion
REM Publish in place: existing portable configuration and unrelated files stay.
REM Use --build-only to publish without starting the app.
set "BUILD_ONLY=0"
if "%~1"=="--build-only" set "BUILD_ONLY=1"
if not "%~1"=="" if not "%~1"=="--build-only" goto USAGE
if not "%~2"=="" goto USAGE
cd /d "%~dp0" || exit /b 1

set "PROJ=src\Backspace\Backspace.csproj"
set "OUT=dist"
set "EXE=%OUT%\Backspace.exe"

echo [launch-release] Publishing single-file Release to %OUT%\ ...
dotnet publish "%PROJ%" -c Release -f net10.0-windows10.0.19041.0 ^
    -r win-x64 --self-contained ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "%OUT%"
if errorlevel 1 goto FAIL
if not exist "%EXE%" (
    echo [launch-release] Publish completed but "%EXE%" is missing.
    exit /b 1
)

echo [launch-release] Built: "%CD%\%EXE%"
if "%BUILD_ONLY%"=="1" exit /b 0
echo [launch-release] Launching...
start "" "%EXE%"
exit /b %errorlevel%

:FAIL
echo [launch-release] Publish failed. No app was launched.
echo If files are in use, choose Quit in the app using this output, let its
echo current operations finish, and retry. See the build error above for details.
exit /b 1

:USAGE
echo Usage: %~nx0 [--build-only]
exit /b 2
