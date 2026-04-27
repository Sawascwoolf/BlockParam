---
name: recreate-workflow-video
description: Rebuilds the narrated workflow MP4 (assets/screenshots/workflow/workflow_inline.mp4) from the BlockParam DevLauncher capture script. Use when the user asks to "recreate the workflow video", "rebuild the workflow GIF/MP4", or after any UI change under src/BlockParam/ whose visuals should be reflected in the marketing video.
---

# Recreate workflow video

Rebuilds `assets/screenshots/workflow/workflow_inline.mp4` end-to-end: rebuild DevLauncher → capture frames → stitch MP4.

## Run from the repo root

```bash
dotnet build src/BlockParam.DevLauncher -c Debug

src/BlockParam.DevLauncher/bin/Debug/net48/BlockParam.DevLauncher.exe \
    --capture-script assets/screenshots/scripts/workflow_inline.json

bash assets/screenshots/workflow/chapters/render-chapters.sh

bash assets/screenshots/workflow/external/render-external.sh

bash assets/screenshots/scripts/build_workflow_video.sh
```

Output: `assets/screenshots/workflow/workflow_inline.mp4`. The stitch script auto-opens it in the default player.

## Why four steps

1. **DevLauncher capture** renders all dialog scenes (everything *not* `kind: "chapter"` or `kind: "external"`).
2. **render-chapters.sh** renders chapter title cards from `chapters/chapter-template.svg` (Inkscape) — driven by the same `workflow_inline.json` (chapter scenes have `kind: "chapter"`, `chapterTitle`, `chapterSubtitle`). Adding a chapter scene to the manifest auto-resizes the progress bar across all cards.
3. **render-external.sh** renders external/painpoint scenes (TIA Portal screenshots) with a synthetic cursor + click-ring overlay matching BlockParam's CursorOverlay style. Each `kind: "external"` scene declares `source` (path to source PNG), `cursor: { x, y }` in DIPs, and optional `click: "press" | "release"`.
4. **build_workflow_video.sh** stitches every scene's PNG into the MP4 with per-beat pacing.

Steps 1, 2, and 3 don't conflict — the DevLauncher capture loop skips both `chapter` and `external` scenes — so they can run in any order.

## Why the build step is not optional

`--capture-script` renders from the dialog assembly **embedded in the DevLauncher EXE**, not from the current sources. A stale binary silently captures the old UI — the fix or feature you just made will be missing from the video with no error.

`dotnet build` is incremental: on a clean tree it returns in a few seconds without recompiling, so running it unconditionally is cheaper than reasoning about whether it is needed.

## Scene / pacing changes

- Add / remove / reorder scenes: edit `assets/screenshots/scripts/workflow_inline.json`.
- Timing is driven by each scene's `beat` field. If you introduce a new beat name, add it to the `BEATS` map in `assets/screenshots/scripts/build_workflow_video.sh` — otherwise the stitch fails with `Unknown beat`.

## Troubleshooting

- **Capture fails with `IOException` on `devlauncher.log`.** Another DevLauncher instance on the same user account is holding the log file (`%TEMP%\BlockParam\devlauncher.log` is user-wide, not per-worktree — any parallel session in the main repo or another worktree collides). **Ask the user before killing it** — the other instance may be their active testing session. If they approve: `powershell -c "Stop-Process -Name BlockParam.DevLauncher -Force"`, then retry step 2.

## Do not

- Launch the DevLauncher via `cmd /c start` or `start` — WPF silently fails to show. Run the EXE path directly from bash (the `--capture-script` mode exits on its own, so it does not block the terminal).
