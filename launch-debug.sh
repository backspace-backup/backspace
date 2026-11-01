#!/usr/bin/env bash
# Build without stopping instances or replacing portable configuration.
# Usage: ./launch-debug.sh [--build-only]
set -euo pipefail
cd "$(dirname "$0")"
BUILD_ONLY=0
if [[ $# == 1 && "$1" == "--build-only" ]]; then
  BUILD_ONLY=1
elif [[ $# != 0 ]]; then
  echo "Usage: $0 [--build-only]" >&2
  exit 2
fi

EXE="src/Backspace/bin/Debug/net10.0/Backspace"
echo "[launch-debug] Building Debug for net10.0 ..."
if ! dotnet build src/Backspace/Backspace.csproj -c Debug -f net10.0; then
  echo "[launch-debug] Build failed. No app was launched." >&2
  echo "If files are in use, choose Quit in the app using this output, let its current operations finish, and retry." >&2
  exit 1
fi
if [[ ! -x "$EXE" ]]; then
  echo "[launch-debug] Expected executable at '${EXE}' but it is missing or not executable." >&2
  exit 1
fi
echo "[launch-debug] Built: $(pwd)/${EXE}"
if [[ "$BUILD_ONLY" == 1 ]]; then exit 0; fi
echo "[launch-debug] Launching with verbose logging..."
BACKSPACE_DEBUG=1 "$EXE" &
disown 2>/dev/null || true
