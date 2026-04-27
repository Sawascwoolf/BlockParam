#!/usr/bin/env bash
#
# Render external (TIA painpoint) scenes from workflow_inline.json into 4K
# PNGs that match the BlockParam dialog snapshots' resolution and visual
# style — same cursor arrow + click-ring as BulkChangeDialog.xaml's
# CursorOverlay.
#
# Source of truth: every scene with `kind: "external"` in
# assets/screenshots/scripts/workflow_inline.json. Adding/removing/
# reordering an external scene in the manifest auto-updates this run.
#
# Per-scene manifest fields:
#   source     relative path (from assets/screenshots/workflow/) to the
#              original screenshot (any resolution; SVG upscales to 4K).
#   cursor     { x, y } in DIPs (1920x1080 base) — multiplied by 2 here for
#              the 4K render. Required for external scenes.
#   click      optional, "press" or "release" — paints the BlockParam click
#              ring on top of the source frame, centered on the cursor.
#
# Outputs:
#   external/external-NN.svg   filled template, kept for inspection
#   workflow/<filename>        the canonical 4K PNG used by build_workflow_video.sh
#
# The cursor arrow geometry, click-ring sizes/colors, and shadow values are
# kept in lockstep with src/BlockParam/UI/BulkChangeDialog.xaml so a TIA
# pain frame is visually indistinguishable from a BlockParam capture in
# terms of the cursor/click style.

set -euo pipefail

cd "$(dirname "$0")"

TEMPLATE=external-template.svg
MANIFEST=../../scripts/workflow_inline.json
WORKFLOW_DIR=..

# Click-ring geometry — must mirror BulkChangeDialog.xaml.cs ShowClickRingAtLastCursor:
#   press   28 DIP diameter, opacity 0.9 → 4K radius 28 px
#   release 56 DIP diameter, opacity 0.55 → 4K radius 56 px
# Stroke 4 DIP → 8 px @ 4K, fill #1f6feb @ 20% alpha.
RING_STROKE='#1f6feb'
RING_STROKE_WIDTH=8
RING_FILL='#1f6feb'
RING_FILL_OPACITY=0.2

# Extract external scenes from the manifest. Output rows:
#   id<TAB>filename<TAB>source<TAB>cx<TAB>cy<TAB>click
# (click is empty string when not set.)
mapfile -t SCENE_ROWS < <(python -c "
import io, json, sys
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', newline='\n')
with open('$MANIFEST', encoding='utf-8') as f:
    data = json.load(f)
externals = [s for s in data['scenes'] if s.get('kind') == 'external']
for s in externals:
    sid = s.get('id', '')
    fn = s.get('filename', '')
    src = s.get('source', '')
    cur = s.get('cursor') or {}
    cx = cur.get('x', '')
    cy = cur.get('y', '')
    click = s.get('click', '') or ''
    if not src:
        sys.stderr.write(f'External scene {sid} missing source field\n')
        sys.exit(1)
    if cx == '' or cy == '':
        sys.stderr.write(f'External scene {sid} missing cursor.x/cursor.y\n')
        sys.exit(1)
    print('\t'.join([sid, fn, src, str(cx), str(cy), click]))
" | tr -d '\r')

if [[ ${#SCENE_ROWS[@]} -eq 0 ]]; then
  echo "No external scenes (kind=\"external\") found in $MANIFEST"
  exit 0
fi

build_click_ring() {
  local click="$1"
  local cx="$2"
  local cy="$3"
  case "$click" in
    press)
      printf '<circle cx="%s" cy="%s" r="28" stroke="%s" stroke-width="%s" fill="%s" fill-opacity="%s" opacity="0.9" filter="url(#ringShadow)"/>' \
        "$cx" "$cy" "$RING_STROKE" "$RING_STROKE_WIDTH" "$RING_FILL" "$RING_FILL_OPACITY"
      ;;
    release)
      printf '<circle cx="%s" cy="%s" r="56" stroke="%s" stroke-width="%s" fill="%s" fill-opacity="%s" opacity="0.55" filter="url(#ringShadow)"/>' \
        "$cx" "$cy" "$RING_STROKE" "$RING_STROKE_WIDTH" "$RING_FILL" "$RING_FILL_OPACITY"
      ;;
    "")
      printf ''
      ;;
    *)
      echo "Unknown click phase '$click' (expected press|release|empty)" >&2
      exit 1
      ;;
  esac
}

render_one() {
  local idx=$1
  local row="$2"
  IFS=$'\t' read -ra fields <<< "$row"
  local sid=${fields[0]}
  local fn=${fields[1]}
  local src=${fields[2]}
  local cx_dip=${fields[3]}
  local cy_dip=${fields[4]}
  local click=${fields[5]:-}

  local num
  num=$(printf "%02d" "$idx")

  # DIPs (1920x1080 base) -> 4K pixels (3840x2160)
  local cx_4k cy_4k
  cx_4k=$(python -c "print(int(round($cx_dip * 2)))")
  cy_4k=$(python -c "print(int(round($cy_dip * 2)))")

  # Source path is relative to workflow/, but the SVG is in workflow/external/
  # so its <image href> needs one extra `..` segment.
  local href="../$src"

  local out_svg="external-${num}.svg"
  local out_png="${WORKFLOW_DIR}/${fn}"

  local ring_tmp
  ring_tmp=$(mktemp)
  build_click_ring "$click" "$cx_4k" "$cy_4k" > "$ring_tmp"

  sed -e "s|{{SOURCE_HREF}}|$href|g" \
      -e "s|{{CURSOR_X_4K}}|$cx_4k|g" \
      -e "s|{{CURSOR_Y_4K}}|$cy_4k|g" \
      -e "/{{CLICK_RING}}/{
        r $ring_tmp
        d
      }" \
      "$TEMPLATE" > "$out_svg"

  rm -f "$ring_tmp"

  inkscape "$out_svg" \
    --export-type=png \
    --export-filename="$out_png" \
    --export-width=3840 >/dev/null

  printf "  %-32s  cursor=(%s,%s)  click=%-7s  %s\n" \
    "$fn" "$cx_dip" "$cy_dip" "${click:-none}" "$sid"
}

echo "Rendering ${#SCENE_ROWS[@]} external scene(s) at 3840x2160"
i=1
for row in "${SCENE_ROWS[@]}"; do
  render_one "$i" "$row"
  i=$((i + 1))
done
echo "Done."
