#!/usr/bin/env python3
"""Pack a folder of PNGs into masonry composites (desktop + mobile).

Reads every *.png from --input-dir (excluding the output files themselves),
resizes each to a fixed per-layout column width while preserving aspect,
then greedily places each on the currently-shortest column. The result
is a single PNG per layout — handy for previewing a whole screenshot
set without opening 20-odd files.

Used by .github/workflows/ci.yml (screenshots job) after DevLauncher
runs every --capture-* mode, but also runnable locally:

    python assets/screenshots/scripts/build_masonry.py \
        --input-dir ci-output \
        --desktop-out ci-output/overview-desktop.png \
        --mobile-out  ci-output/overview-mobile.png
"""
from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


def _load_label_font(size: int) -> ImageFont.ImageFont:
    for name in ("arial.ttf", "DejaVuSans.ttf", "LiberationSans-Regular.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except (OSError, IOError):
            continue
    return ImageFont.load_default()


def build_montage(
    images: list[Path],
    columns: int,
    col_width: int,
    gap: int,
    label_size: int,
    bg: tuple[int, int, int] = (245, 246, 248),
) -> Image.Image:
    scaled: list[tuple[str, Image.Image]] = []
    for path in images:
        im = Image.open(path).convert("RGB")
        ratio = col_width / im.width
        new_h = max(1, int(round(im.height * ratio)))
        scaled.append((path.name, im.resize((col_width, new_h), Image.Resampling.LANCZOS)))

    col_items: list[list[tuple[str, Image.Image]]] = [[] for _ in range(columns)]
    col_heights = [0] * columns
    for name, im in scaled:
        target = min(range(columns), key=lambda i: col_heights[i])
        col_items[target].append((name, im))
        col_heights[target] += im.height + gap

    total_h = max(col_heights) - gap if max(col_heights) > 0 else 0
    total_w = columns * col_width + (columns - 1) * gap

    canvas = Image.new("RGB", (total_w, total_h), bg)
    draw = ImageDraw.Draw(canvas, "RGBA")
    font = _load_label_font(label_size)

    for ci, col in enumerate(col_items):
        x = ci * (col_width + gap)
        y = 0
        for name, im in col:
            canvas.paste(im, (x, y))

            pad = max(4, label_size // 3)
            bbox = draw.textbbox((0, 0), name, font=font)
            tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
            box = (x + pad, y + pad, x + pad + tw + pad * 2, y + pad + th + pad * 2)
            draw.rectangle(box, fill=(0, 0, 0, 170))
            draw.text((box[0] + pad, box[1] + pad), name, fill=(255, 255, 255), font=font)

            y += im.height + gap

    return canvas


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--input-dir", required=True)
    ap.add_argument("--desktop-out", required=True)
    ap.add_argument("--mobile-out", required=True)
    args = ap.parse_args()

    in_dir = Path(args.input_dir)
    out_paths = {Path(args.desktop_out).resolve(), Path(args.mobile_out).resolve()}

    images = sorted(p for p in in_dir.glob("*.png") if p.resolve() not in out_paths)
    if not images:
        raise SystemExit(f"No PNGs in {in_dir}")

    print(f"Packing {len(images)} images")
    for p in images:
        print(f"  {p.name}")

    print(f"\nDesktop: 3 cols x 640px -> {args.desktop_out}")
    build_montage(images, columns=3, col_width=640, gap=16, label_size=16).save(
        args.desktop_out, optimize=True
    )

    print(f"Mobile:  2 cols x 360px -> {args.mobile_out}")
    build_montage(images, columns=2, col_width=360, gap=8, label_size=11).save(
        args.mobile_out, optimize=True
    )

    print("Done")


if __name__ == "__main__":
    main()
