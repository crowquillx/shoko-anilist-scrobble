#!/usr/bin/env bash
# Exercise package version verification with a valid package and a stale
# release filename/manifest pair.
#
# Usage:
#   build/test-package-version.sh <valid-archive.zip> <valid-manifest.json>
set -euo pipefail

archive="${1:?usage: build/test-package-version.sh <valid-archive.zip> <valid-manifest.json>}"
manifest="${2:?usage: build/test-package-version.sh <valid-archive.zip> <valid-manifest.json>}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# The TAG-stamped package from CI must pass all checks.
bash "$root/build/verify-package.sh" "$archive" "$manifest" >/dev/null

# Publish with the csproj Version, then label the zip and manifest as 9.9.9.
# That mismatch is what verify-package.sh must reject, independent of whatever
# the project Version currently is.
claimed="9.9.9"
bad_dir="$tmp/publish"
bad_archive="$tmp/Shoko.AniListScrobble-${claimed}-any.zip"
bad_manifest="$tmp/manifest.json"
dotnet publish "$root/src/Shoko.AniListScrobble.csproj" \
  -c Release --no-restore --no-self-contained -o "$bad_dir" >/dev/null
(cd "$bad_dir" && zip -qr "$bad_archive" .)
bad_checksum="$(sha256sum "$bad_archive" | awk '{print $1}')"
jq \
  --arg version "$claimed" \
  --arg tag "v$claimed" \
  --arg url "https://example.invalid/releases/download/v${claimed}/$(basename "$bad_archive")" \
  --arg checksum "sha256:$bad_checksum" \
  '.releases[0].version = $version
   | .releases[0].tag = $tag
   | .releases[0].archives[0].url = $url
   | .releases[0].archives[0].checksum = $checksum' \
  "$manifest" > "$bad_manifest"

if bash "$root/build/verify-package.sh" "$bad_archive" "$bad_manifest" >/dev/null 2>&1; then
  echo "error: package whose assembly version does not match ${claimed} was accepted" >&2
  exit 1
fi

echo "OK: package version verification passed (valid package accepted; stale package rejected)"
