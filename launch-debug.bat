@echo off
setlocal DisableDelayedExpansion
REM Build without stopping other instances or replacing portable configuration.
REM Use --build-only to build without starting the app.
set "BUILD_ONLY=0"
if "%~1"=="--build-only" set "BUILD_ONLY=1"
if not "%~1"=="" if not "%~1"=="--build-only" goto USAGE
if not "%~2"=="" goto USAGE
cd /d "%~dp0" || exit /b 1

set "PROJ=src\Backspace\Backspace.csproj"
set "EXE=src\Backspace\bin\Debug\net10.0-windows\Backspace.exe"

echo [launch-debug] Building Debug...
dotnet build "%PROJ%" -c Debug -f net10.0-windows
if errorlevel 1 goto FAIL
if not exist "%EXE%" (
    echo [launch-debug] Build completed but "%EXE%" is missing.
    exit /b 1
)

echo [launch-debug] Built: "%CD%\%EXE%"
if "%BUILD_ONLY%"=="1" exit /b 0
echo [launch-debug] Launching with verbose logging...
set "BACKSPACE_DEBUG=1"
start "" "%EXE%"
exit /b %errorlevel%

:FAIL
echo [launch-debug] Build failed. No app was launched.
echo If files are in use, choose Quit in the app using this output, let its
echo current operations finish, and retry. See the build error above for details.
exit /b 1

:USAGE
echo Usage: %~nx0 [--build-only]
exit /b 2
