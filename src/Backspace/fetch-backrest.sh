#!/usr/bin/env bash
# Downloads the host-arch backrest + restic + rclone executables from
# their respective GitHub releases / official download URLs to the given
# out-paths. Invoked by FetchBackrest.targets on macOS and Linux hosts.
#
# Self-contained: assumes only `bash`, `curl`, `jq`, `tar`, `bunzip2`,
# `unzip` — all standard on macOS (jq via Homebrew or Xcode CLT, the
# rest are baseline) and Linux. Re-runs are idempotent: each binary is
# skipped if it already exists.

set -euo pipefail

BACKREST_OUT="${1:?usage: fetch-backrest.sh <backrest-out> <restic-out> <rclone-out>}"
RESTIC_OUT="${2:?usage: fetch-backrest.sh <backrest-out> <restic-out> <rclone-out>}"
RCLONE_OUT="${3:?usage: fetch-backrest.sh <backrest-out> <restic-out> <rclone-out>}"

# Detect host OS + arch.
case "$(uname -s)" in
  Darwin*)  OS_BACKREST="Darwin"; OS_RESTIC="darwin"; OS_RCLONE="osx" ;;
  Linux*)   OS_BACKREST="Linux";  OS_RESTIC="linux";  OS_RCLONE="linux" ;;
  *)
    echo "[fetch-backrest] Unsupported host: $(uname -s)" >&2
    exit 1
    ;;
esac

case "$(uname -m)" in
  x86_64|amd64)
    ARCH_BACKREST="x86_64"
    ARCH_RESTIC="amd64"
    ARCH_RCLONE="amd64"
    ;;
  arm64|aarch64)
    ARCH_BACKREST="arm64"
    ARCH_RESTIC="arm64"
    ARCH_RCLONE="arm64"
    ;;
  *)
    echo "[fetch-backrest] Unrecognised CPU $(uname -m); falling back to amd64" >&2
    ARCH_BACKREST="x86_64"
    ARCH_RESTIC="amd64"
    ARCH_RCLONE="amd64"
    ;;
esac

if ! command -v jq >/dev/null 2>&1; then
  echo "[fetch-backrest] 'jq' is required but not on PATH." >&2
  echo "[fetch-backrest]   macOS: brew install jq" >&2
  echo "[fetch-backrest]   Linux: sudo apt install jq" >&2
  exit 1
fi

fetch_github_asset() {
    # $1=repo_slug $2=asset_pattern (extended-grep) $3=out_archive_path
    local repo="$1"; local pattern="$2"; local out_archive="$3"

    echo "[fetch-backrest] Querying GitHub for $repo latest release..."
    local release_json
    release_json="$(curl -fsSL \
        -H 'User-Agent: Backspace-build' \
        -H 'Accept: application/vnd.github+json' \
        --connect-timeout 30 --max-time 60 \
        "https://api.github.com/repos/$repo/releases/latest")"

    local asset_url asset_name asset_size
    asset_url="$(echo "$release_json" | jq -r --arg p "$pattern" '.assets[] | select(.name | test($p)) | .browser_download_url' | head -n1)"
    asset_name="$(echo "$release_json" | jq -r --arg p "$pattern" '.assets[] | select(.name | test($p)) | .name' | head -n1)"
    asset_size="$(echo "$release_json" | jq -r --arg p "$pattern" '.assets[] | select(.name | test($p)) | .size' | head -n1)"

    if [[ -z "${asset_url:-}" || "$asset_url" == "null" ]]; then
        echo "[fetch-backrest] No asset matching '$pattern' in $repo latest release." >&2
        echo "[fetch-backrest] Available assets:" >&2
        echo "$release_json" | jq -r '.assets[].name' >&2
        return 1
    fi

    if [[ -n "${asset_size:-}" && "$asset_size" != "null" ]]; then
        printf "[fetch-backrest] Downloading %s (%.1f MB)\n" \
            "$asset_name" "$(awk "BEGIN { printf \"%.1f\", $asset_size / 1024 / 1024 }")"
    fi

    curl -fsSL --connect-timeout 30 --max-time 600 -o "$out_archive" "$asset_url"

    # Supply-chain check: stamp the downloaded archive's SHA-256 and
    # cross-check against fetch-manifest.json (next to this script)
    # when present. Mismatch aborts the build so a tampered or
    # unexpectedly-replaced upstream asset doesn't ship silently.
    local hash
    hash="$(shasum -a 256 "$out_archive" | awk '{print $1}')"
    echo "[fetch-backrest]   sha256 = $hash"
    local manifest="$(dirname "$0")/fetch-manifest.json"
    if [[ -f "$manifest" ]]; then
        local key="$repo/$asset_name"
        local expected
        expected="$(jq -r --arg k "$key" '.[$k] // empty' "$manifest")"
        if [[ -n "$expected" ]]; then
            if [[ "$expected" != "$hash" ]]; then
                echo "[fetch-backrest]   ERROR: hash mismatch for $key" >&2
                echo "[fetch-backrest]     expected: $expected" >&2
                echo "[fetch-backrest]     got:      $hash" >&2
                return 1
            fi
            echo "[fetch-backrest]   sha256 matches pinned manifest entry"
        else
            echo "[fetch-backrest]   WARN: no manifest entry for $key; add the hash above to fetch-manifest.json to pin"
        fi
    else
        echo "[fetch-backrest]   note: no fetch-manifest.json present; copy the sha256 above into one to enable pinning"
    fi
}

# Backrest: tar.gz, contains backrest binary + LICENSE etc. at top level.
if [[ ! -f "$BACKREST_OUT" ]]; then
    mkdir -p "$(dirname "$BACKREST_OUT")"
    tmpdir="$(mktemp -d)"
    trap 'rm -rf "$tmpdir"' EXIT

    archive="$tmpdir/backrest.tar.gz"
    fetch_github_asset "garethgeorge/backrest" \
        "^backrest_${OS_BACKREST}_${ARCH_BACKREST}\\.tar\\.gz\$" \
        "$archive"
    tar -xzf "$archive" -C "$tmpdir"
    inner="$(find "$tmpdir" -name backrest -type f -perm -u+x | head -n1)"
    if [[ -z "$inner" ]]; then
        echo "[fetch-backrest] Archive did not contain a backrest binary." >&2
        exit 1
    fi
    install -m 0755 "$inner" "$BACKREST_OUT"
    echo "[fetch-backrest] Wrote $BACKREST_OUT"
    rm -rf "$tmpdir"; trap - EXIT
else
    echo "[fetch-backrest] $BACKREST_OUT already present, skipping."
fi

# Restic: bz2-compressed single binary on Unix (one file inside).
if [[ ! -f "$RESTIC_OUT" ]]; then
    mkdir -p "$(dirname "$RESTIC_OUT")"
    tmpdir="$(mktemp -d)"
    trap 'rm -rf "$tmpdir"' EXIT

    archive="$tmpdir/restic.bz2"
    fetch_github_asset "restic/restic" \
        "^restic_[0-9.]+_${OS_RESTIC}_${ARCH_RESTIC}\\.bz2\$" \
        "$archive"
    bunzip2 -c "$archive" > "$RESTIC_OUT"
    chmod 0755 "$RESTIC_OUT"
    echo "[fetch-backrest] Wrote $RESTIC_OUT"
    rm -rf "$tmpdir"; trap - EXIT
else
    echo "[fetch-backrest] $RESTIC_OUT already present, skipping."
fi

# Rclone: stable "current" alias per OS+arch on downloads.rclone.org.
if [[ ! -f "$RCLONE_OUT" ]]; then
    mkdir -p "$(dirname "$RCLONE_OUT")"
    tmpdir="$(mktemp -d)"
    trap 'rm -rf "$tmpdir"' EXIT

    url="https://downloads.rclone.org/rclone-current-${OS_RCLONE}-${ARCH_RCLONE}.zip"
    archive="$tmpdir/rclone.zip"
    echo "[fetch-backrest] Downloading rclone from $url"
    curl -fsSL --connect-timeout 30 --max-time 600 -o "$archive" "$url"
    unzip -q "$archive" -d "$tmpdir"
    inner="$(find "$tmpdir" -name rclone -type f -perm -u+x | head -n1)"
    if [[ -z "$inner" ]]; then
        echo "[fetch-backrest] rclone archive did not contain a rclone binary." >&2
        exit 1
    fi
    install -m 0755 "$inner" "$RCLONE_OUT"
    echo "[fetch-backrest] Wrote $RCLONE_OUT"
    rm -rf "$tmpdir"; trap - EXIT
else
    echo "[fetch-backrest] $RCLONE_OUT already present, skipping."
fi
