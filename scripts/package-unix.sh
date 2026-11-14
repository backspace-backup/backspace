#!/usr/bin/env bash
# Builds the Linux or macOS release of Backspace (U245): one self-contained
# program (.NET included: those systems rarely have .NET 10; Windows releases
# never bundle it). restic and rclone are not included: on first start
# Backspace downloads them from their own projects and checks them against
# the SHA-256 built into it, as on Windows.
#
#   scripts/package-unix.sh <rid> <version> <out-dir>
#   rid: linux-x64 | linux-arm64 | osx-arm64 | osx-x64
#
# Linux:  <out-dir>/Backspace-linux-<arch>.tar.gz holding the Backspace program.
# macOS:  <out-dir>/Backspace-macos-<arch>.zip holding Backspace.app, signed
#         ad hoc (no Apple Developer ID yet: the first open needs
#         right-click > Open, or System Settings > Privacy & Security > Open Anyway).
set -euo pipefail

rid="${1:?rid}"; version="${2:?version}"; out="${3:?out-dir}"
root="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$out"; out="$(cd "$out" && pwd)"

case "$rid" in
  linux-x64)   export ENGINE_ARCH=amd64; name=linux-x64 ;;
  linux-arm64) export ENGINE_ARCH=arm64; name=linux-arm64 ;;
  osx-arm64)   export ENGINE_ARCH=arm64; name=macos-arm64 ;;
  osx-x64)     export ENGINE_ARCH=amd64; name=macos-x64 ;;
  *) echo "Unknown runtime: $rid" >&2; exit 2 ;;
esac

# The engines the build hashes into the program must be this processor's.
rm -f "$root/src/Backspace/Assets/restic" "$root/src/Backspace/Assets/rclone"
work="$(mktemp -d)"
dotnet publish "$root/src/Backspace" -c Release -f net10.0 -r "$rid" \
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:Version="$version" -o "$work/publish"

extra="$(ls "$work/publish" | grep -v -x -e Backspace -e '.*\.pdb' || true)"
if [ -n "$extra" ]; then echo "The published app must be one program; found also: $extra" >&2; exit 1; fi
chmod 755 "$work/publish/Backspace"

case "$rid" in
  linux-*)
    tar -C "$work/publish" -czf "$out/Backspace-$name.tar.gz" Backspace
    ;;
  osx-*)
    app="$work/Backspace.app"
    mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
    cp "$work/publish/Backspace" "$app/Contents/MacOS/Backspace"
    iconset="$work/Backspace.iconset"; mkdir -p "$iconset"
    for size in 16 32 128 256 512; do
      sips -z $size $size "$root/assets/brand/backspace-1024.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
      sips -z $((size*2)) $((size*2)) "$root/assets/brand/backspace-1024.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
    done
    iconutil -c icns "$iconset" -o "$app/Contents/Resources/Backspace.icns"
    cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Backspace</string>
  <key>CFBundleDisplayName</key><string>Backspace</string>
  <key>CFBundleIdentifier</key><string>io.github.backspace-backup</string>
  <key>CFBundleExecutable</key><string>Backspace</string>
  <key>CFBundleIconFile</key><string>Backspace</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST
    codesign --force --deep --sign - "$app"
    codesign --verify --deep --strict "$app"
    (cd "$work" && ditto -c -k --keepParent Backspace.app "$out/Backspace-$name.zip")
    ;;
esac
rm -rf "$work"
ls -l "$out"
