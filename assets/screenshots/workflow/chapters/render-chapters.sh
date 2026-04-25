#!/usr/bin/env bash
#
# Render the workflow video's chapter title cards.
#
# Source of truth: assets/screenshots/scripts/workflow_inline.json — every
# scene with `kind: "chapter"` becomes a card. Adding/removing/reordering a
# chapter scene in the manifest automatically:
#   - regenerates the right number of PNGs,
#   - resizes the progress bar (N segments fill SPAN=1760),
#   - colors the segments per chapter (1..current = dark, rest = light).
#
# Outputs per chapter:
#   chapters/chapter-NN.svg            substituted template (kept for inspection)
#   workflow/<manifest-filename>.png   the canonical pipeline file (e.g. wf01_ch_inline.png)
#
# Subtitle in the manifest uses " • " (Unicode U+2022) between phrases. The
# script splits on that and emits the design-token "·" (U+00B7) in cyan
# between phrases.

set -euo pipefail

cd "$(dirname "$0")"

TEMPLATE=chapter-template.svg
MANIFEST=../../scripts/workflow_inline.json
WORKFLOW_DIR=..

DARK="#0e2140"
LIGHT="#d6dde6"
SPAN=1760
GAP=16
BAR_H=4
BAR_RX=2
ACCENT="#06b6c7"
SUBTITLE_FILL="#5a6678"

# ---- Read viewport/dpi-derived export width from the manifest ----
# Chapter PNGs must match the dialog snapshots' resolution exactly — the
# stitch pipeline uses ffmpeg's concat demuxer, which requires identical
# stream parameters across inputs. Dialog snapshots come out at viewport ×
# (dpi/96), so chapters use the same factor.
read EXPORT_W EXPORT_H < <(python -c "
with open('$MANIFEST', encoding='utf-8') as f:
    import json
    d = json.load(f)
vw = d.get('viewport', {}).get('width', 1920)
vh = d.get('viewport', {}).get('height', 1080)
scale = d.get('dpi', 96) / 96.0
print(int(vw * scale), int(vh * scale))
")

# ---- Extract chapter scenes from the manifest ----
# Output rows: idx<TAB>total<TAB>basefilename<TAB>title<TAB>phrase1<TAB>phrase2<TAB>...
# Python opens the manifest with explicit utf-8 (Windows defaults to cp1252,
# which mangles the U+2022 bullet character used between subtitle phrases) and
# also forces utf-8 on stdout so the bullets survive the bash round-trip.
mapfile -t CHAPTER_ROWS < <(python -c "
import io, json, sys
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', newline='\n')
with open('$MANIFEST', encoding='utf-8') as f:
    data = json.load(f)
chapters = [s for s in data['scenes'] if s.get('kind') == 'chapter']
total = len(chapters)
for i, s in enumerate(chapters, start=1):
    base = s.get('filename', '').rsplit('.', 1)[0]
    title = s.get('chapterTitle', '')
    subtitle = s.get('chapterSubtitle', '') or ''
    phrases = [p.strip() for p in subtitle.split('•') if p.strip()]
    print('\t'.join([str(i), str(total), base, title, *phrases]))
" | tr -d '\r')

if [[ ${#CHAPTER_ROWS[@]} -eq 0 ]]; then
  echo "No chapter scenes (kind=\"chapter\") found in $MANIFEST" >&2
  exit 1
fi

TOTAL=${#CHAPTER_ROWS[@]}
BAR_W=$(( (SPAN - (TOTAL - 1) * GAP) / TOTAL ))

build_progress_rects() {
  local current=$1
  local x=0
  for ((i=1; i<=TOTAL; i++)); do
    local fill="$LIGHT"
    [[ $i -le $current ]] && fill="$DARK"
    printf '    <rect x="%d" y="0" width="%d" height="%d" rx="%d" fill="%s"/>\n' \
      "$x" "$BAR_W" "$BAR_H" "$BAR_RX" "$fill"
    x=$(( x + BAR_W + GAP ))
  done
}

# Build the inner content of the subtitle <text> as a tspan chain:
#   <tspan>phrase1</tspan>
#   <tspan dx="14" fill="cyan">·</tspan><tspan dx="14" fill="gray">phrase2</tspan>
#   ...
build_subtitle_tspans() {
  local first=true
  for phrase in "$@"; do
    if [[ "$first" == "true" ]]; then
      printf '<tspan>%s</tspan>' "$phrase"
      first=false
    else
      printf '<tspan dx="14" fill="%s">·</tspan><tspan dx="14" fill="%s">%s</tspan>' \
        "$ACCENT" "$SUBTITLE_FILL" "$phrase"
    fi
  done
}

render_one() {
  local row="$1"
  IFS=$'\t' read -ra fields <<< "$row"
  local current=${fields[0]}
  local total=${fields[1]}    # equals TOTAL; carried for clarity
  local basename=${fields[2]}
  local title=${fields[3]}
  local phrases=("${fields[@]:4}")

  local num
  num=$(printf "%02d" "$current")

  local out_svg="chapter-${num}.svg"
  local out_png="${WORKFLOW_DIR}/${basename}.png"

  # Stage replacement contents in temp files so sed can `r`-include them
  # verbatim — avoids escaping XML/quotes/newlines through sed substitution.
  local rects_tmp tspans_tmp
  rects_tmp=$(mktemp)
  tspans_tmp=$(mktemp)
  build_progress_rects "$current" > "$rects_tmp"
  build_subtitle_tspans "${phrases[@]}" > "$tspans_tmp"

  sed -e "s|{{CHAPTER_NUMBER}}|$num|g" \
      -e "s|{{TITLE}}|$title|g" \
      -e "/{{SUBTITLE_TSPANS}}/{
        r $tspans_tmp
        d
      }" \
      -e "/{{PROGRESS_RECTS}}/{
        r $rects_tmp
        d
      }" \
      "$TEMPLATE" > "$out_svg"

  rm -f "$rects_tmp" "$tspans_tmp"

  inkscape "$out_svg" \
    --export-type=png \
    --export-filename="$out_png" \
    --export-width="$EXPORT_W" >/dev/null

  printf "  %-28s  ch %d/%d  %s\n" "$basename.png" "$current" "$total" "$title"
}

echo "Rendering $TOTAL chapter card(s) at ${EXPORT_W}x${EXPORT_H} — bar width ${BAR_W}px, gap ${GAP}px"
for row in "${CHAPTER_ROWS[@]}"; do
  render_one "$row"
done
echo "Done."
