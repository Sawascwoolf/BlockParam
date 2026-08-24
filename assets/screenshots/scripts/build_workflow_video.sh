#!/usr/bin/env bash
# Stitches the wf*.png frames captured from workflow_inline.json into a
# narrated-pace MP4. Pacing is NOT duplicated here — each scene carries a
# `beat` field in the JSON, and this script maps beat names → seconds.
# Add/remove scenes in the JSON and re-run; no edits here needed unless you
# want to add a new beat name.
#
# Regenerate the frames first:
#   src/BlockParam.DevLauncher/bin/Debug/net48/BlockParam.DevLauncher.exe \
#       --capture-script assets/screenshots/scripts/workflow_inline.json
# from the repo root.
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
frames_dir="$script_dir/../workflow"
scenes_json="$script_dir/workflow_inline.json"
out_mp4="$frames_dir/workflow_inline.mp4"
concat_list="$frames_dir/.concat.txt"

# Beat name → seconds. Keep names semantic ("what the viewer is doing"),
# not numeric ("short/long") — so intent survives pacing tweaks.
declare -A BEATS=(
    [typingAChar]=0.5
    [clickDown]=0.18
    [clickRelease]=0.35
    [pointingWithMouse]=1.2
    [readingShort]=1.5
    [afterCommit]=1.2
    [intro]=1.4
    [readingError]=2.2
    [readingBulk]=1.8
    [chapterHold]=3.6
    [outro]=2.6
    [painEstablish]=2.0
    [painDwell]=3.0
    [tiaType]=0.8
    [clickHold]=0.7
)

# Extract (filename, beat) pairs from the JSON in scene order.
# Python handles the JSON; bash does the lookup and ffmpeg concat formatting.
# JSON is piped on stdin to sidestep msys path translation of $scenes_json.
# Resolve a usable Python. `python` on PATH is NOT trustworthy on Windows:
# it is commonly the Microsoft Store stub (exits 9009 without running) or
# Inkscape's bundled interpreter (real, but its PIL cannot load _imaging
# outside Inkscape's DLL directory). So don't trust a name — probe each
# candidate by running the exact import the caller needs, and keep the
# first one that survives it.
python_candidates() {
    printf '%s\n' "py -3" python3 python
    # The launcher is not always on PATH: a per-user CPython install puts it
    # in %LOCALAPPDATA%\Programs\Python\Launcher, which the installer only
    # adds to PATH when "Add python.exe to PATH" was ticked.
    if [[ -n "${LOCALAPPDATA:-}" ]]; then
        local base
        base="$(cygpath -u "$LOCALAPPDATA" 2>/dev/null || echo "$LOCALAPPDATA")"
        printf '%s\n' "$base/Programs/Python/Launcher/py.exe -3"
    fi
}

# $1 = python statement that must succeed. Echoes the winning command.
find_python() {
    local c
    while IFS= read -r c; do
        if $c -c "$1" >/dev/null 2>&1; then
            echo "$c"
            return 0
        fi
    done < <(python_candidates)
    return 1
}

PY="$(find_python 'import json, sys')" || {
    echo "No working Python found. Tried:" >&2
    python_candidates | sed 's/^/  /' >&2
    echo "Install Python 3 and ensure it (or the py launcher) is on PATH." >&2
    exit 1
}

mapfile -t scene_rows < <($PY -c "
import json, sys
data = json.load(sys.stdin)
for s in data['scenes']:
    print(f\"{s['filename']}\t{s.get('beat', 'readingShort')}\")
" < "$scenes_json" | tr -d '\r')

{
    for row in "${scene_rows[@]}"; do
        filename="${row%$'\t'*}"
        beat="${row##*$'\t'}"
        secs="${BEATS[$beat]:-}"
        if [[ -z "$secs" ]]; then
            echo "Unknown beat '$beat' for $filename (add it to BEATS)" >&2
            exit 1
        fi
        echo "file '$filename'"
        echo "duration $secs"
    done
    # ffmpeg concat quirk: the last `duration` is ignored, so repeat the
    # final frame once to guarantee the outro plays for its full beat.
    last_filename="${scene_rows[-1]%$'\t'*}"
    echo "file '$last_filename'"
} > "$concat_list"

cd "$frames_dir"

echo "Building MP4 -> $out_mp4"
ffmpeg -y -loglevel error \
    -f concat -safe 0 -i ".concat.txt" \
    -vf "fps=30,scale=1920:-1:flags=lanczos,format=yuv420p" \
    -c:v libx264 -crf 23 -movflags +faststart \
    "$out_mp4"

rm -f "$concat_list"

mp4_size=$(stat -c%s "$out_mp4" 2>/dev/null || stat -f%z "$out_mp4")
echo "MP4: $((mp4_size / 1024)) KB"

# Validation contact sheet, built from the exact same frames just stitched into
# the MP4 so the two never diverge. Skipped (with a one-line warning) when
# Python or PIL is missing — the MP4 is the primary artifact; the grid is a
# review aid that shouldn't block the pipeline.
grid_script="$script_dir/build_workflow_grid.py"
if [[ -f "$grid_script" ]]; then
    # Probe the real import the grid script performs, NOT a bare `import PIL`.
    # `import PIL` only executes PIL/__init__.py and succeeds even when the
    # compiled _imaging extension cannot load — exactly what Inkscape's
    # bundled Python does, which made this guard report green and then let
    # the grid die with "DLL load failed while importing _imaging".
    # Resolve independently of $PY: the interpreter that parses the manifest
    # need not be the one carrying a working Pillow.
    if PY_PIL="$(find_python 'from PIL import Image, ImageDraw')"; then
        echo "Building validation grid -> assets/screenshots/workflow/_validation_grid.png"
        ( cd "$script_dir/../../.." && $PY_PIL "$grid_script" )
    else
        echo "WARN: skipping validation grid — no Python with a working Pillow."
        echo "      Install: $PY -m pip install Pillow"
    fi
fi

# Opt-in player launch only. The default behavior used to be `cmd //c start`,
# which spawned a child cmd that never exits from this script's perspective —
# the spawning shell stays "running" until the player is closed, which hangs
# headless pipelines and any agent-driven rebuild. Set BLOCKPARAM_AUTO_OPEN=1
# (interactive use) to restore the old behavior.
if [[ "${BLOCKPARAM_AUTO_OPEN:-0}" == "1" ]]; then
    cmd //c start "" "$(cygpath -w "$out_mp4")"
fi
