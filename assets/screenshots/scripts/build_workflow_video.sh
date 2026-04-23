#!/usr/bin/env bash
# Stitches the wf*_*.png frames from the workflow_inline capture into a
# narrated-pace MP4 walking a per-cell inline-edit workflow:
# search -> pick row1 -> type contains-infix -> hover & accept -> apply.
# One keystroke per frame so the viewer sees the filter follow every character,
# including the contains-match on short infixes like "101", "102", "103".
#
# Regenerate the frames first:
#   src/BlockParam.DevLauncher/bin/Debug/net48/BlockParam.DevLauncher.exe \
#       --capture-script assets/screenshots/scripts/workflow_inline.json
# from the repo root.
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
frames_dir="$script_dir/../workflow"
out_mp4="$frames_dir/workflow_inline.mp4"
concat_list="$frames_dir/.concat.txt"

typing=0.15        # one typed character
beat_short=0.7     # normal state beat
beat_hover=0.9     # pause on hover frame so the reader sees what's about to be picked
beat_commit=0.9    # after a suggestion is accepted (pending row appears)
beat_start=0.9     # initial Hero
beat_end=1.8       # final Apply hold before the loop

cat > "$concat_list" <<EOF
file 'wf01_hero.png'
duration $beat_start
file 'wf01b_search_hover.png'
duration $beat_hover
file 'wf02_search_v.png'
duration $typing
file 'wf03_search_va.png'
duration $typing
file 'wf04_search_val.png'
duration $typing
file 'wf05_search_valv.png'
duration $typing
file 'wf06_search_valve.png'
duration $typing
file 'wf07_search_valveT.png'
duration $typing
file 'wf08_search_valveTa.png'
duration $typing
file 'wf09_search_valveTag.png'
duration $beat_short
file 'wf09b_v1_cell_hover.png'
duration $beat_hover
file 'wf10_v1_1.png'
duration $typing
file 'wf11_v1_10.png'
duration $typing
file 'wf12_v1_101.png'
duration $beat_short
file 'wf13_v1_hover.png'
duration $beat_hover
file 'wf14_v1_accept.png'
duration $beat_commit
file 'wf14b_v2_cell_hover.png'
duration $beat_hover
file 'wf15_v2_1.png'
duration $typing
file 'wf16_v2_10.png'
duration $typing
file 'wf17_v2_102.png'
duration $beat_short
file 'wf18_v2_hover.png'
duration $beat_hover
file 'wf19_v2_accept.png'
duration $beat_commit
file 'wf19b_v3_cell_hover.png'
duration $beat_hover
file 'wf20_v3_1.png'
duration $typing
file 'wf21_v3_10.png'
duration $typing
file 'wf22_v3_103.png'
duration $beat_short
file 'wf23_v3_hover.png'
duration $beat_hover
file 'wf24_v3_accept.png'
duration $beat_commit
file 'wf25_apply_hover.png'
duration $beat_hover
file 'wf26_apply.png'
duration $beat_end
file 'wf26_apply.png'
EOF

cd "$frames_dir"

echo "Building MP4 -> $out_mp4"
ffmpeg -y -loglevel error \
    -f concat -safe 0 -i ".concat.txt" \
    -vf "fps=30,scale=960:-1:flags=lanczos,format=yuv420p" \
    -c:v libx264 -crf 23 -movflags +faststart \
    "$out_mp4"

rm -f "$concat_list"

mp4_size=$(stat -c%s "$out_mp4" 2>/dev/null || stat -f%z "$out_mp4")
echo "MP4: $((mp4_size / 1024)) KB"
