#!/usr/bin/env bash
# Publish in place, preserving portable configuration and unrelated files.
# Usage: ./launch-release.sh [--build-only] [osx-arm64|osx-x64|linux-arm64|linux-x64]
set -euo pipefail
cd "$(dirname "$0")"

BUILD_ONLY=0
RID=""
for arg in "$@"; do
  case "$arg" in
    --build-only) BUILD_ONLY=1 ;;
    osx-arm64|osx-x64|linux-arm64|linux-x64)
      if [[ -n "$RID" ]]; then echo "Specify only one runtime." >&2; exit 2; fi
      RID="$arg" ;;
    *) echo "Usage: $0 [--build-only] [osx-arm64|osx-x64|linux-arm64|linux-x64]" >&2; exit 2 ;;
  esac
done
if [[ -z "$RID" ]]; then
  case "$(uname -s)" in
    Darwin) HOST_OS_TAG="osx" ;;
    Linux) HOST_OS_TAG="linux" ;;
    *) echo "[launch-release] Unsupported host; specify a runtime for cross-publishing." >&2; exit 1 ;;
  esac
  case "$(uname -m)" in
    x86_64|amd64) HOST_ARCH_TAG="x64" ;;
    arm64|aarch64) HOST_ARCH_TAG="arm64" ;;
    *) echo "[launch-release] Unsupported host architecture." >&2; exit 1 ;;
  esac
  RID="${HOST_OS_TAG}-${HOST_ARCH_TAG}"
fi

OUT="dist"
EXE="${OUT}/Backspace"
echo "[launch-release] Publishing self-contained Release for ${RID} -> ${OUT}/ ..."
if ! dotnet publish src/Backspace/Backspace.csproj -c Release -f net10.0 -r "$RID" --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$OUT"; then
  echo "[launch-release] Publish failed. No app was launched." >&2
  echo "If files are in use, choose Quit in the app using this output, let its current operations finish, and retry." >&2
  exit 1
fi
if [[ ! -f "$EXE" ]]; then
  echo "[launch-release] Publish completed but '${EXE}' is missing." >&2
  exit 1
fi
chmod +x "$EXE"
echo "[launch-release] Built: $(pwd)/${EXE}"
if [[ "$BUILD_ONLY" == 1 ]]; then exit 0; fi
echo "[launch-release] Launching..."
"./$EXE" &
disown 2>/dev/null || true
