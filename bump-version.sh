#!/bin/bash
# Usage: ./bump-version.sh <major.minor.patch> [--tia=20|21|both]
# Example: ./bump-version.sh 0.3.0
#          ./bump-version.sh 0.3.0 --tia=21
#
# Default: build & deploy BOTH V20 and V21 artifacts.
#
# Updates version in:
#   - src/BlockParam/BlockParam.csproj
#   - src/BlockParam/addin-publisher-v20.xml
#   - src/BlockParam/addin-publisher-v21.xml
# Then rebuilds, packages and deploys each requested target.
#
# V20 deploy target: C:\Program Files\Siemens\Automation\Portal V20\AddIns\
#                    (machine-wide, requires admin write access)
# V21 deploy target: %APPDATA%\Siemens\Automation\Portal V21\UserAddIns\
#                    (per-user; Portal V21\AddIns\ does not exist by default)

set -e

VERSION="${1:?Usage: $0 <version> [--tia=20|21|both]}"
TIA_FLAG="${2:---tia=both}"

case "$TIA_FLAG" in
  --tia=20)   BUILD_V20=1; BUILD_V21=0 ;;
  --tia=21)   BUILD_V20=0; BUILD_V21=1 ;;
  --tia=both) BUILD_V20=1; BUILD_V21=1 ;;
  *) echo "Unknown flag: $TIA_FLAG (use --tia=20 | --tia=21 | --tia=both)"; exit 1 ;;
esac

ROOT="$(cd "$(dirname "$0")" && pwd)"
CSPROJ="$ROOT/src/BlockParam/BlockParam.csproj"
PUBLISHER_XML_V20="$ROOT/src/BlockParam/addin-publisher-v20.xml"
PUBLISHER_XML_V21="$ROOT/src/BlockParam/addin-publisher-v21.xml"

PUBLISHER_EXE_V20="C:\\Program Files\\Siemens\\Automation\\Portal V20\\PublicAPI\\V20.AddIn\\Siemens.Engineering.AddIn.Publisher.exe"
PUBLISHER_EXE_V21="C:\\Program Files\\Siemens\\Automation\\Portal V21\\PublicAPI\\V21\\Siemens.Engineering.AddIn.Publisher.exe"

ADDIN_TARGET_V20="C:\\Program Files\\Siemens\\Automation\\Portal V20\\AddIns\\BlockParam.addin"
ADDIN_TARGET_V21="$APPDATA\\Siemens\\Automation\\Portal V21\\UserAddIns\\BlockParam.addin"

echo "=== Bumping version to $VERSION ==="

# Update csproj <Version> only (avoid hitting the per-package <Version> attrs).
sed -i "s|<Version>[^<]*</Version>|<Version>$VERSION</Version>|" "$CSPROJ"
echo "  Updated $CSPROJ"

# Publisher manifests have a <Product><Version> we update.
# AddInVersion ('1.0.0' / 'V21') and the xmlns are NOT touched.
sed -i "s|<Version>[^<]*</Version>|<Version>$VERSION</Version>|" "$PUBLISHER_XML_V20"
sed -i "s|<Version>[^<]*</Version>|<Version>$VERSION</Version>|" "$PUBLISHER_XML_V21"
echo "  Updated addin-publisher-v20.xml and addin-publisher-v21.xml"

build_and_package() {
  local tia="$1"
  local publisher_xml="$2"
  local publisher_exe="$3"
  local addin_target="$4"
  local out_dir

  if [ "$tia" = "20" ]; then
    out_dir="$ROOT/src/BlockParam/bin/Release/net48"
  else
    out_dir="$ROOT/src/BlockParam/bin/Release/net48/v21"
  fi

  echo "=== [V$tia] Building Release ==="
  dotnet build "$ROOT/src/BlockParam" -c Release -p:TiaVersion="$tia" --nologo -v quiet
  echo "  Build OK -> $out_dir"

  echo "=== [V$tia] Packaging .addin ==="
  local manifest_basename="$(basename "$publisher_xml")"
  cp "$publisher_xml" "$out_dir/$manifest_basename"
  "$publisher_exe" \
    -f "$out_dir/$manifest_basename" \
    -o "$addin_target" \
    -c 2>&1 | tail -1

  echo "  Deployed -> $addin_target"
}

if [ "$BUILD_V20" = "1" ]; then
  build_and_package 20 "$PUBLISHER_XML_V20" "$PUBLISHER_EXE_V20" "$ADDIN_TARGET_V20"
fi

if [ "$BUILD_V21" = "1" ]; then
  build_and_package 21 "$PUBLISHER_XML_V21" "$PUBLISHER_EXE_V21" "$ADDIN_TARGET_V21"
fi

echo ""
echo "=== v$VERSION deployed ==="
[ "$BUILD_V20" = "1" ] && echo "  V20: $ADDIN_TARGET_V20"
[ "$BUILD_V21" = "1" ] && echo "  V21: $ADDIN_TARGET_V21"
echo "Restart TIA Portal to load the new version."
echo ""
echo "To publish this version as a public GitHub Release, run:"
echo "  bash release.sh"
