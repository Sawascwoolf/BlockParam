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
    [typingAChar]=0.15
    [clickDown]=0.18
    [clickRelease]=0.35
    [pointingWithMouse]=1.2
    [readingShort]=1.0
    [afterCommit]=1.2
    [intro]=1.4
    [readingError]=2.2
    [readingBulk]=1.8
    [chapterHold]=2.4
    [outro]=2.6
)

# Extract (filename, beat) pairs from the JSON in scene order.
# Python handles the JSON; bash does the lookup and ffmpeg concat formatting.
# JSON is piped on stdin to sidestep msys path translation of $scenes_json.
mapfile -t scene_rows < <(python -c "
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

# Launch in the OS default video player so each iteration is a single command.
# `cmd //c start` avoids msys path mangling; cygpath converts to a Windows path.
cmd //c start "" "$(cygpath -w "$out_mp4")"
