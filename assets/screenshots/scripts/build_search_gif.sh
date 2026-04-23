#!/usr/bin/env bash
# Stitches the search_gif_* frames into an MP4 for the landing page.
# Assumes the frames already exist — regenerate them via:
#   src/BlockParam.DevLauncher/bin/Debug/net48/BlockParam.DevLauncher.exe \
#       --capture-script assets/screenshots/scripts/search_gif.json
# from the repo root.
#
# MP4 is the primary web embed (<video autoplay loop muted playsinline>).
# No GIF is emitted — it was an order of magnitude larger for the same
# visual, which doesn't make sense once MP4 is known to be supported.
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
frames_dir="$script_dir/../search"
out_mp4="$frames_dir/search_filter.mp4"
concat_list="$frames_dir/.concat.txt"

# Per-frame durations (seconds). Typing frames need to be slow enough
# that the viewer can see each filter step land; holds sit at phase
# boundaries so the eye can read the final filtered state.
typing=0.15              # one typed character
pause_after_typing=1.30  # hold on the fully-typed term
pause_on_clear=0.45      # blink back to empty between phases
pause_at_start=0.80      # initial empty state
pause_at_end=1.50        # final "hiHiLimit" hold before loop

# Build the concat-demuxer list. Each PNG gets a duration; ffmpeg's
# concat-demuxer quirk requires the last file to be repeated without a
# duration line (otherwise the final frame's duration is ignored).
cat > "$concat_list" <<EOF
file 'gif_search_01_empty.png'
duration $pause_at_start
file 'gif_search_02_d.png'
duration $typing
file 'gif_search_03_de.png'
duration $typing
file 'gif_search_04_dea.png'
duration $typing
file 'gif_search_05_dead.png'
duration $typing
file 'gif_search_06_deadb.png'
duration $typing
file 'gif_search_07_deadba.png'
duration $typing
file 'gif_search_08_deadban.png'
duration $typing
file 'gif_search_09_deadband.png'
duration $pause_after_typing
file 'gif_search_10_empty.png'
duration $pause_on_clear
file 'gif_search_11_h.png'
duration $typing
file 'gif_search_12_hi.png'
duration $typing
file 'gif_search_13_hiH.png'
duration $typing
file 'gif_search_14_hiHi.png'
duration $typing
file 'gif_search_15_hiHiL.png'
duration $typing
file 'gif_search_16_hiHiLi.png'
duration $typing
file 'gif_search_17_hiHiLim.png'
duration $typing
file 'gif_search_18_hiHiLimi.png'
duration $typing
file 'gif_search_19_hiHiLimit.png'
duration $pause_at_end
file 'gif_search_19_hiHiLimit.png'
EOF

# Run ffmpeg from the frames dir so the concat-demuxer's relative
# filenames resolve without us having to emit Windows-compatible
# absolute paths from MSYS.
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
