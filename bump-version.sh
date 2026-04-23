#!/bin/bash
# Usage: ./bump-version.sh <major.minor.patch>
# Example: ./bump-version.sh 0.3.0
#
# Updates version in:
#   - BlockParam.csproj
#   - addin-publisher.xml
# Then rebuilds, packages and deploys to TIA Portal AddIns folder.

set -e

VERSION="${1:?Usage: $0 <version> (e.g. 0.3.0)}"
ROOT="$(cd "$(dirname "$0")" && pwd)"
CSPROJ="$ROOT/src/BlockParam/BlockParam.csproj"
PUBLISHER_XML="$ROOT/src/BlockParam/addin-publisher.xml"
PUBLISHER_EXE="C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V20.AddIn\Siemens.Engineering.AddIn.Publisher.exe"
ADDIN_TARGET="C:\Program Files\Siemens\Automation\Portal V20\AddIns\BlockParam.addin"

echo "=== Bumping version to $VERSION ==="

# Update csproj
sed -i "s|<Version>[^<]*</Version>|<Version>$VERSION</Version>|" "$CSPROJ"
echo "  Updated $CSPROJ"

# Update publisher config
sed -i "s|<Version>[^<]*</Version>|<Version>$VERSION</Version>|" "$PUBLISHER_XML"
echo "  Updated $PUBLISHER_XML"

# Build
echo "=== Building Release ==="
dotnet build "$ROOT/src/BlockParam" -c Release --nologo -v quiet
echo "  Build OK"

# Package
echo "=== Packaging .addin ==="
cp "$PUBLISHER_XML" "$ROOT/src/BlockParam/bin/Release/net48/"
"$PUBLISHER_EXE" \
  -f "$ROOT/src/BlockParam/bin/Release/net48/addin-publisher.xml" \
  -o "$ADDIN_TARGET" \
  -c 2>&1 | tail -1

echo ""
echo "=== v$VERSION deployed to TIA Portal AddIns ==="
echo "Restart TIA Portal to load the new version."
echo ""
echo "To publish this version as a public GitHub Release, run:"
echo "  bash release.sh"
