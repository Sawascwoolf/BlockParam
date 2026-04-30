#!/bin/bash
# Publish the currently deployed BlockParam.addin files as a GitHub Release.
#
# Reads the version from src/BlockParam/BlockParam.csproj and uploads the .addin
# files that bump-version.sh put into the TIA Portal AddIns folders. Requires
# the gh CLI authenticated against the repo.
#
# Usage:
#   bash release.sh                       # both V20 and V21, version from csproj
#   bash release.sh 0.9.0                 # override version, both targets
#   bash release.sh 0.9.0 --tia=20        # only V20
#   bash release.sh 0.9.0 --tia=21        # only V21
#
# Asset naming: each .addin is uploaded as
#   BlockParam-v<version>-TIA-V<20|21>.addin
# so users can tell at a glance which file matches their TIA Portal version.

set -eo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
CSPROJ="$ROOT/src/BlockParam/BlockParam.csproj"
REPO="Sawascwoolf/BlockParam"

ADDIN_V20="C:\\Program Files\\Siemens\\Automation\\Portal V20\\AddIns\\BlockParam.addin"
ADDIN_V21="$APPDATA\\Siemens\\Automation\\Portal V21\\UserAddIns\\BlockParam.addin"

if ! command -v gh >/dev/null 2>&1; then
  echo "error: gh CLI not found in PATH" >&2
  exit 1
fi

# --- parse args (version is positional, --tia is a flag) -----------------------
VERSION=""
TIA_FLAG="--tia=both"
for arg in "$@"; do
  case "$arg" in
    --tia=*) TIA_FLAG="$arg" ;;
    *)       VERSION="$arg" ;;
  esac
done

if [ -z "$VERSION" ]; then
  VERSION="$(grep -oE '<Version>[^<]+</Version>' "$CSPROJ" | head -1 | sed -E 's|</?Version>||g')"
fi
if [ -z "$VERSION" ]; then
  echo "error: could not determine version" >&2
  exit 1
fi

case "$TIA_FLAG" in
  --tia=20)   PUB_V20=1; PUB_V21=0 ;;
  --tia=21)   PUB_V20=0; PUB_V21=1 ;;
  --tia=both) PUB_V20=1; PUB_V21=1 ;;
  *) echo "Unknown flag: $TIA_FLAG (use --tia=20 | --tia=21 | --tia=both)"; exit 1 ;;
esac

# APPDATA is needed to resolve the V21 source path. If unset (CI / sanitised
# env), the path collapses to '\Siemens\...' on the root of the working drive
# and the existence check below would silently miss the real artifact.
if [ "$PUB_V21" = "1" ]; then
  : "${APPDATA:?APPDATA not set — run from Git Bash on Windows, or pass --tia=20 to skip V21}"
fi

# --- collect & rename assets in a temp dir -------------------------------------
# gh release upload uses the on-disk filename as the asset name, so we stage
# copies under sprechende Namen before upload.
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

ASSETS=()

stage_asset() {
  local tia="$1"
  local src="$2"
  if [ ! -f "$src" ]; then
    echo "error: V$tia .addin not found at $src" >&2
    echo "  run 'bash bump-version.sh $VERSION --tia=$tia' first" >&2
    exit 1
  fi
  local dst="$STAGE/BlockParam-v$VERSION-TIA-V$tia.addin"
  cp "$src" "$dst"
  ASSETS+=("$dst")
  echo "  V$tia: $(basename "$dst")"
}

TAG="v$VERSION"

echo "=== Publishing $TAG ==="
[ "$PUB_V20" = "1" ] && stage_asset 20 "$ADDIN_V20"
[ "$PUB_V21" = "1" ] && stage_asset 21 "$ADDIN_V21"

# --- release notes header ------------------------------------------------------
# Static install instructions; the auto-generated commit list is appended via
# `gh release edit` after creation (gh CLI does not let --notes and
# --generate-notes coexist).
NOTES_HEADER_FILE="$STAGE/notes-header.md"
cat > "$NOTES_HEADER_FILE" <<EOF
## Download

| TIA Portal version | File |
|---|---|
| **V20** | [\`BlockParam-v$VERSION-TIA-V20.addin\`](https://github.com/$REPO/releases/download/$TAG/BlockParam-v$VERSION-TIA-V20.addin) |
| **V21** | [\`BlockParam-v$VERSION-TIA-V21.addin\`](https://github.com/$REPO/releases/download/$TAG/BlockParam-v$VERSION-TIA-V21.addin) |

## Installation

**TIA Portal V20** &mdash; copy the V20 file to:
\`\`\`
C:\Program Files\Siemens\Automation\Portal V20\AddIns\\
\`\`\`

**TIA Portal V21** &mdash; copy the V21 file to:
\`\`\`
%APPDATA%\Siemens\Automation\Portal V21\UserAddIns\\
\`\`\`

Restart TIA Portal and confirm the Add-In load prompt. Right-click any Data
Block in the project tree &rarr; **BlockParam&hellip;**
EOF

# --- create or update release --------------------------------------------------
if gh release view "$TAG" >/dev/null 2>&1; then
  echo "  Release $TAG already exists — re-uploading assets"
  gh release upload "$TAG" "${ASSETS[@]}" --clobber
else
  gh release create "$TAG" "${ASSETS[@]}" \
    --title "BlockParam $TAG" \
    --generate-notes

  # Prepend our install header to the auto-generated changelog.
  AUTO_BODY="$(gh release view "$TAG" --json body -q .body)"
  COMBINED="$STAGE/notes-combined.md"
  {
    cat "$NOTES_HEADER_FILE"
    printf '\n\n'
    printf '%s\n' "$AUTO_BODY"
  } > "$COMBINED"
  gh release edit "$TAG" --notes-file "$COMBINED"
fi

echo ""
echo "=== Released: https://github.com/$REPO/releases/tag/$TAG ==="
