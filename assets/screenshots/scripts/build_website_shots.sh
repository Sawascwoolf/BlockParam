#!/usr/bin/env bash
# Generate the 3 website hero screenshots into web/assets/.
#
# Maintenance model:
#   - bulk + inline: cropped from existing workflow_inline.mp4 source PNGs
#     (rendered by `--capture-script workflow_inline.json`). When the dialog
#     UI changes, regenerate the source frames first via the
#     `recreate-workflow-video` skill, then re-run this script.
#   - rules: rendered live by DevLauncher --capture-rules from
#     assets/fixtures/rules/. Always reflects the current ConfigEditorDialog.
#
# Tune the crops by editing the per-shot config blocks below. ffmpeg's
# `crop=W:H:X:Y` works in source-PNG pixels (3840x2160 for the workflow
# frames). `scale=W:H` is the final output size.
#
# Output ratios are intentionally per-shot for now; once the website column
# is finalized we'll standardize on one ratio across all three.

set -euo pipefail
cd "$(dirname "$0")/../../.."

WORKFLOW=assets/screenshots/workflow
OUT=web/assets
DEVLAUNCHER=src/BlockParam.DevLauncher/bin/Debug/net48/BlockParam.DevLauncher.exe

mkdir -p "$OUT"

# Build DevLauncher (cheap if up-to-date) so capture modes pick up any
# code changes since the last build.
echo "Building DevLauncher (incremental)..."
dotnet build src/BlockParam.DevLauncher -c Debug --nologo -v quiet > /dev/null

# Render website-only source frames (states the workflow video doesn't
# already capture — e.g. bulk-panel valveTag autocomplete with a partial
# query so the 3-column dropdown shows multiple suggestions).
echo "Rendering website-only source frames..."
"$DEVLAUNCHER" --capture-script assets/screenshots/scripts/website_shots_capture.json >/dev/null

# ─── 1) Bulk sidebar — crop from wf74_pending_hero ──────────────────────────
# Source frame state: deadband + valveType bulks staged, search cleared, so
# the pending list shows the full set of yellow rows.
#
# 4:3 landscape, sidebar-only focus on PENDING EDITS header + ~5 yellow
# rows. The full sidebar (Bulk Edit form + Pending list + action bar) is
# ~1:2.9 portrait and doesn't fit landscape sidebar-only without distortion;
# we prioritize the Pending list because it carries the visual "this saved
# me time" payload (yellow rows, "55 staged" badge, member paths with
# old → new values).
#
# Source crop is the sidebar's native width (720 px @ 4K, x∈[2960, 3680] —
# excludes table column + scrollbar). 720x540 is 4:3 at native resolution;
# upscaled 2.22× to 1600x1200 to match the other website shots.
BULK_SOURCE=$WORKFLOW/wf74_pending_hero.png
BULK_CROP="720:540:2960:850"     # W:H:X:Y in 4K source pixels
BULK_OUT_SIZE="1600:1200"        # 4:3 landscape (matches inline + rules)

# ─── 2) Inline autocomplete — crop from ws_inline_valveTag_autocomplete ─────
# Source frame is rendered fresh by website_shots_capture.json above (state:
# valveTag bulk panel with "V-101" partial query, V-10101 suggestion
# hovered). VALVE_TAGS has ~12 matching entries so the dropdown shows 4
# suggestions in the 3-column DisplayName · Value · Comment format with
# real comments (e.g. "Unit 1 Module 1 main inlet valve"). Validation
# message "Value must be a constant from the 'VALVE_TAGS' tag table."
# anchors the bottom of the crop.
#
# Why bulk-panel autocomplete instead of inline-cell autocomplete: the
# inline cell's dropdown is constrained to the cell width (~280 px) and
# can only show one of the three columns at a time (horizontal scrollbar).
# The bulk panel's NEW VALUE input is wider, so all three columns render
# simultaneously — the marketing payload of "rich tag-table-driven
# autocomplete with comments".
INLINE_SOURCE=$WORKFLOW/ws_inline_valveTag_autocomplete.png
INLINE_CROP="720:540:2960:380"   # W:H:X:Y in 4K source pixels (sidebar only)
INLINE_OUT_SIZE="1600:1200"      # 4:3 landscape (matches bulk + rules)

# ─── Run crops ──────────────────────────────────────────────────────────────

crop_one() {
    local source="$1" crop="$2" out_size="$3" out="$4"
    [[ -f "$source" ]] || { echo "Missing source: $source" >&2; exit 1; }
    ffmpeg -y -loglevel error -i "$source" \
        -vf "crop=${crop},scale=${out_size}:flags=lanczos" \
        "$out"
    echo "  $(basename "$out")  crop=${crop}  size=${out_size}"
}

echo "Cropping bulk + inline from workflow frames..."
crop_one "$BULK_SOURCE"   "$BULK_CROP"   "$BULK_OUT_SIZE"   "$OUT/workflow_bulk.png"
crop_one "$INLINE_SOURCE" "$INLINE_CROP" "$INLINE_OUT_SIZE" "$OUT/workflow_inline.png"

echo "Capturing rules editor via DevLauncher..."
"$DEVLAUNCHER" --capture-rules "$OUT/workflow_rules.png" >/dev/null
echo "  workflow_rules.png  (native 1600x1200 from 800x600 DIP @ 2x)"

echo "Wrote: $OUT/{workflow_bulk,workflow_inline,workflow_rules}.png"
