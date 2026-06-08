#!/usr/bin/env python3
"""Pack workflow_inline.json scenes into a single validation composite.

Same principle as build_masonry.py — PIL, greedy shortest-column masonry,
black label-box overlay on a light background — but reads scenes in
manifest (narrative) order so the composite reflects the video, and
labels each tile with the scene id (not the filename) so each tile maps
back to the manifest at a glance.

    python assets/screenshots/scripts/build_workflow_grid.py
    python assets/screenshots/scripts/build_workflow_grid.py --columns 5
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

from PIL import Image, ImageDraw

from build_masonry import _load_label_font


def build_montage_labeled(
    items: list[tuple[Path, str]],
    columns: int,
    col_width: int,
    gap: int,
    label_size: int,
    bg: tuple[int, int, int] = (245, 246, 248),
) -> Image.Image:
    """Row-major layout for validation: scene N+1 is always to the right of
    scene N (wrapping to the start of the next row), so the numbering reads
    left-to-right top-to-bottom and lines up with the manifest. Row height
    = tallest cell in that row (handles mixed aspect ratios between dialog,
    chapter, and external scenes). Same label-box + light-bg styling as
    build_masonry.py."""
    scaled: list[tuple[str, Image.Image]] = []
    for path, label in items:
        if path.exists():
            im = Image.open(path).convert("RGB")
            ratio = col_width / im.width
            new_h = max(1, int(round(im.height * ratio)))
            scaled.append((label, im.resize((col_width, new_h), Image.Resampling.LANCZOS)))
        else:
            placeholder = Image.new("RGB", (col_width, col_width * 9 // 16), (60, 20, 20))
            d = ImageDraw.Draw(placeholder)
            d.text((10, 10), "MISSING", fill=(255, 200, 200))
            scaled.append((label, placeholder))

    rows = [scaled[i:i + columns] for i in range(0, len(scaled), columns)]
    row_heights = [max(im.height for _, im in row) for row in rows]

    total_w = columns * col_width + (columns - 1) * gap
    total_h = sum(row_heights) + (len(rows) - 1) * gap if rows else 0

    canvas = Image.new("RGB", (total_w, total_h), bg)
    draw = ImageDraw.Draw(canvas, "RGBA")
    font = _load_label_font(label_size)

    y = 0
    for row, rh in zip(rows, row_heights):
        for ci, (name, im) in enumerate(row):
            x = ci * (col_width + gap)
            canvas.paste(im, (x, y))

            # Labels at bottom-left of each tile, not top-left — the top-left
            # of dialog screenshots is exactly where the Add-PLC dropdown /
            # pill popups appear, and a black overlay there obscures the very
            # UI states the grid is meant to verify. build_masonry.py keeps
            # top-left because its CI screenshots don't have that constraint.
            pad = max(4, label_size // 3)
            bbox = draw.textbbox((0, 0), name, font=font)
            tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
            box_h = th + pad * 2
            box_w = tw + pad * 2
            box_bottom = y + im.height - pad
            box_top = box_bottom - box_h
            box = (x + pad, box_top, x + pad + box_w, box_bottom)
            draw.rectangle(box, fill=(0, 0, 0, 170))
            draw.text((box[0] + pad, box[1] + pad), name, fill=(255, 255, 255), font=font)
        y += rh + gap

    return canvas


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--manifest", default="assets/screenshots/scripts/workflow_inline.json")
    ap.add_argument("--frames-dir", default="assets/screenshots/workflow")
    ap.add_argument("--out", default="assets/screenshots/workflow/_validation_grid.png")
    ap.add_argument("--columns", type=int, default=4,
                    help="Column count. Defaults preset so total width ≈ 2560px.")
    ap.add_argument("--col-width", type=int, default=None,
                    help="Per-column width in px; auto-sized from --columns + --gap when omitted.")
    ap.add_argument("--gap", type=int, default=20)
    ap.add_argument("--label-size", type=int, default=14)
    ap.add_argument("--target-width", type=int, default=2560,
                    help="Used only when --col-width is omitted.")
    args = ap.parse_args()

    if args.col_width is None:
        args.col_width = (args.target_width - (args.columns - 1) * args.gap) // args.columns

    manifest = json.loads(Path(args.manifest).read_text(encoding="utf-8"))
    frames_dir = Path(args.frames_dir)

    items: list[tuple[Path, str]] = []
    missing = 0
    for i, scene in enumerate(manifest["scenes"], start=1):
        p = frames_dir / scene["filename"]
        if not p.exists():
            missing += 1
        items.append((p, f"{i:>3}. {scene['id']}"))

    total_w = args.columns * args.col_width + (args.columns - 1) * args.gap
    print(f"Packing {len(items)} scenes ({missing} missing) -- "
          f"{args.columns} cols x {args.col_width}px (gap {args.gap}) -> {total_w}px wide")

    img = build_montage_labeled(
        items,
        columns=args.columns,
        col_width=args.col_width,
        gap=args.gap,
        label_size=args.label_size,
    )
    Path(args.out).parent.mkdir(parents=True, exist_ok=True)
    img.save(args.out, optimize=True)
    size_mb = Path(args.out).stat().st_size / (1024 * 1024)
    print(f"Wrote: {args.out}  ({img.width} x {img.height}, {size_mb:.1f} MB)")


if __name__ == "__main__":
    main()
