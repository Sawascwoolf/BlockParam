#!/bin/bash
# Publish the currently deployed BlockParam.addin as a GitHub Release.
#
# Reads the version from src/BlockParam/BlockParam.csproj and uploads the .addin
# file that bump-version.sh put into the TIA Portal AddIns folder. Requires the
# gh CLI authenticated against the repo.
#
# Usage:
#   bash release.sh              # version from csproj
#   bash release.sh 0.9.0        # override version

set -e

ROOT="$(cd "$(dirname "$0")" && pwd)"
CSPROJ="$ROOT/src/BlockParam/BlockParam.csproj"
ADDIN="C:\Program Files\Siemens\Automation\Portal V20\AddIns\BlockParam.addin"

if ! command -v gh >/dev/null 2>&1; then
  echo "error: gh CLI not found in PATH" >&2
  exit 1
fi

if [ -n "$1" ]; then
  VERSION="$1"
else
  VERSION="$(grep -oE '<Version>[^<]+</Version>' "$CSPROJ" | head -1 | sed -E 's|</?Version>||g')"
fi

if [ -z "$VERSION" ]; then
  echo "error: could not determine version" >&2
  exit 1
fi

if [ ! -f "$ADDIN" ]; then
  echo "error: .addin not found at $ADDIN" >&2
  echo "  run 'bash bump-version.sh $VERSION' first" >&2
  exit 1
fi

TAG="v$VERSION"

echo "=== Publishing $TAG ==="
echo "  Asset: $ADDIN"

if gh release view "$TAG" >/dev/null 2>&1; then
  echo "  Release $TAG already exists — re-uploading asset"
  gh release upload "$TAG" "$ADDIN" --clobber
else
  gh release create "$TAG" "$ADDIN" \
    --title "BlockParam $TAG" \
    --generate-notes
fi

echo ""
echo "=== Released: https://github.com/Sawascwoolf/BlockParam/releases/tag/$TAG ==="
