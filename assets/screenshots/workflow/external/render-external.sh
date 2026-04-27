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
#   caption    optional, a short painpoint string (e.g. "TIA: no search").
#              Renders as a top-left rounded pill (dark-navy backdrop,
#              brick-red text). Pill width is sized to the caption length.
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

# Extract external scenes from the manifest. Output rows separated by
# \x1f (Unit Separator) — a non-whitespace delimiter so that empty middle
# fields (e.g. an empty `click` between non-empty `cy` and `caption`) are
# preserved. Bash's `read -ra` with whitespace IFS collapses consecutive
# tabs into one, eating empty fields and shifting later columns left.
mapfile -t SCENE_ROWS < <(python -c "
import io, json, sys
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', newline='\n')
with open('$MANIFEST', encoding='utf-8') as f:
    data = json.load(f)
externals = [s for s in data['scenes'] if s.get('kind') == 'external']
SEP = '\x1f'
for s in externals:
    sid = s.get('id', '')
    fn = s.get('filename', '')
    src = s.get('source', '')
    cur = s.get('cursor') or {}
    cx = cur.get('x', '')
    cy = cur.get('y', '')
    click = s.get('click', '') or ''
    caption = s.get('caption', '') or ''
    if not src:
        sys.stderr.write(f'External scene {sid} missing source field\n')
        sys.exit(1)
    if cx == '' or cy == '':
        sys.stderr.write(f'External scene {sid} missing cursor.x/cursor.y\n')
        sys.exit(1)
    print(SEP.join([sid, fn, src, str(cx), str(cy), click, caption]))
" | tr -d '\r')

if [[ ${#SCENE_ROWS[@]} -eq 0 ]]; then
  echo "No external scenes (kind=\"external\") found in $MANIFEST"
  exit 0
fi

# Painpoint badge: top-left rounded pill, dark-navy backdrop @ 92% opacity,
# brick-red bold text. Pill width is fixed across all scenes so the badge
# is the same size on every frame — sized to fit the longest expected
# caption (~45 chars at Segoe UI 56px bold).
BADGE_WIDTH=1500

build_caption_badge() {
  local caption="$1"
  if [[ -z "$caption" ]]; then
    return
  fi
  # Escape XML entities so captions can contain & < > " without breaking the
  # SVG parse. Order matters: & first, otherwise later replacements get
  # double-escaped.
  caption="${caption//&/&amp;}"
  caption="${caption//</&lt;}"
  caption="${caption//>/&gt;}"
  caption="${caption//\"/&quot;}"
  printf '<g transform="translate(80, 80)"><rect x="0" y="0" width="%d" height="140" rx="70" fill="#0e2140" fill-opacity="0.92"/><text x="60" y="93" font-family="Segoe UI, Arial, sans-serif" font-size="56" font-weight="700" fill="#ff6464">%s</text></g>' \
    "$BADGE_WIDTH" "$caption"
}

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
  local row="$1"
  IFS=$'\x1f' read -ra fields <<< "$row"
  local sid=${fields[0]}
  local fn=${fields[1]}
  local src=${fields[2]}
  local cx_dip=${fields[3]}
  local cy_dip=${fields[4]}
  local click=${fields[5]:-}
  local caption=${fields[6]:-}

  # DIPs (1920x1080 base) -> 4K pixels (3840x2160)
  local cx_4k cy_4k
  cx_4k=$(python -c "print(int(round($cx_dip * 2)))")
  cy_4k=$(python -c "print(int(round($cy_dip * 2)))")

  # Source path is relative to workflow/, but the SVG is in workflow/external/
  # so its <image href> needs one extra `..` segment.
  local href="../$src"

  # SVG kept for inspection; named after the manifest filename (e.g.
  # tia01_click_search.svg) so it greps cleanly against scene ids.
  local out_svg="${fn%.png}.svg"
  local out_png="${WORKFLOW_DIR}/${fn}"

  local ring_tmp badge_tmp
  ring_tmp=$(mktemp)
  badge_tmp=$(mktemp)
  build_click_ring "$click" "$cx_4k" "$cy_4k" > "$ring_tmp"
  build_caption_badge "$caption" > "$badge_tmp"

  sed -e "s|{{SOURCE_HREF}}|$href|g" \
      -e "s|{{CURSOR_X_4K}}|$cx_4k|g" \
      -e "s|{{CURSOR_Y_4K}}|$cy_4k|g" \
      -e "/{{CAPTION_BADGE}}/{
        r $badge_tmp
        d
      }" \
      -e "/{{CLICK_RING}}/{
        r $ring_tmp
        d
      }" \
      "$TEMPLATE" > "$out_svg"

  rm -f "$ring_tmp" "$badge_tmp"

  inkscape "$out_svg" \
    --export-type=png \
    --export-filename="$out_png" \
    --export-width=3840 >/dev/null

  printf "  %-32s  cursor=(%s,%s)  click=%-7s  caption=%s\n" \
    "$fn" "$cx_dip" "$cy_dip" "${click:-none}" "${caption:-(none)}"
}

echo "Rendering ${#SCENE_ROWS[@]} external scene(s) at 3840x2160"
for row in "${SCENE_ROWS[@]}"; do
  render_one "$row"
done
echo "Done."
