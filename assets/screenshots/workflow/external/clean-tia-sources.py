#!/usr/bin/env python3
"""Strip the recording environment out of the TIA painpoint screenshots (#198).

The screenshots under ``workflow/TiaScreenshots/`` were recorded on a real
desktop on 2026-04-27 and carry two things that must not ship on a sales page:

  1. the Windows taskbar (icons + a clock that dates the recording), visible
     on the 19 shots where the TIA window was maximised but not full-screen;
  2. TIA's status-bar notification, most often
     "A change of user interface language w…" — leftover session chrome.

Both are painted over with TIA's own status-bar background (#1c1c1c), which is
already the colour of the window's bottom border. Nothing is cropped, scaled or
stretched: every source stays 1920x1080, so the cursor coordinates in
``scripts/workflow_inline.json`` (given in the same 1920x1080 DIP space) keep
pointing at exactly the pixels they pointed at before.

Run from anywhere; paths resolve relative to this file:

    py -3 assets/screenshots/workflow/external/clean-tia-sources.py [--dry-run]

The script is idempotent — a second run reports "already clean" and writes
nothing. Re-run it after dropping a freshly recorded screenshot into
``TiaScreenshots/``, then re-render the external scenes:

    bash assets/screenshots/workflow/external/render-external.sh
"""

import sys
from pathlib import Path

from PIL import Image

# TIA's status-bar background, and the colour of the window's bottom border.
BG = (28, 28, 28)

# Column probed to find the status bar: far enough right of the "Portal view"
# tab strip and far enough left of the notification icons that the status bar
# is empty background there in every shot.
PROBE_X = 1000

# The notification area starts just right of the status bar's vertical
# separator at x=1590 (a 1px #585858 line spanning the full bar height). The
# separator itself is kept — it is TIA chrome, present with or without a
# message — so the wipe starts two pixels to its right.
NOTIFY_X0 = 1592

# Height of TIA's status bar, window border included — 29px in every shot of
# this session, whether TIA was full-screen or maximised behind the taskbar.
# Derived from the bar's TOP edge rather than its bottom, because after a first
# pass the painted-over taskbar is background too and a bottom-anchored measure
# would grow to include it (breaking the second run).
STATUS_BAR_HEIGHT = 29

# Sanity bound on what may sit below the status bar, so a screenshot with a
# different layout fails loudly instead of getting a large area painted over.
MAX_TASKBAR_HEIGHT = 80

SCRIPT_DIR = Path(__file__).resolve().parent
SOURCE_DIR = SCRIPT_DIR.parent / "TiaScreenshots"


def find_status_bar_top(im):
    """Return the first row of TIA's status bar.

    Walks up column ``PROBE_X`` from the bottom of the image: first past
    anything that is not status-bar background (the Windows taskbar, on the
    shots that carry one), then across the contiguous background run that forms
    the status bar — and, on an already-cleaned shot, the painted-over taskbar
    below it. Only the run's top edge is used, which is why a second pass sees
    the same value as the first.
    """
    _, h = im.size
    y = h - 1
    while y >= 0 and im.getpixel((PROBE_X, y)) != BG:
        y -= 1
    if y < 0:
        raise ValueError(f"no #{BG[0]:02x} status-bar background in column {PROBE_X}")
    while y >= 0 and im.getpixel((PROBE_X, y)) == BG:
        y -= 1
    return y + 1


def clean(path, dry_run=False):
    im = Image.open(path).convert("RGB")
    w, h = im.size

    top = find_status_bar_top(im)
    bottom = top + STATUS_BAR_HEIGHT - 1  # last row of the status bar itself
    taskbar_height = h - 1 - bottom

    if taskbar_height < 0:
        raise ValueError(
            f"status bar starts at row {top}, leaving only {h - top}px for a "
            f"{STATUS_BAR_HEIGHT}px bar — unexpected layout")
    if taskbar_height > MAX_TASKBAR_HEIGHT:
        raise ValueError(
            f"{taskbar_height}px below the status bar — expected at most "
            f"{MAX_TASKBAR_HEIGHT}; is the TIA window really maximised?")

    taskbar_box = (0, bottom + 1, w, h) if taskbar_height else None
    notify_box = (NOTIFY_X0, top, w, bottom + 1)

    actions = []
    if taskbar_box and not is_uniform(im, taskbar_box):
        actions.append(("taskbar", taskbar_box))
    if not is_uniform(im, notify_box):
        actions.append(("notification", notify_box))

    if not actions:
        return f"already clean (status bar {top}..{bottom})"

    if not dry_run:
        for _, box in actions:
            im.paste(BG, box)
        im.save(path)

    return ", ".join(
        f"{name} {box[1]}..{box[3] - 1}" if name == "taskbar" else f"{name} x>={box[0]}"
        for name, box in actions)


def is_uniform(im, box):
    """True when every pixel in ``box`` is already the fill colour."""
    return im.crop(box).getcolors(maxcolors=2) == [((box[2] - box[0]) * (box[3] - box[1]), BG)]


def main():
    dry_run = "--dry-run" in sys.argv[1:]
    paths = sorted(SOURCE_DIR.rglob("*.png"))
    if not paths:
        print(f"No screenshots under {SOURCE_DIR}", file=sys.stderr)
        return 1

    failures = 0
    for path in paths:
        rel = path.relative_to(SOURCE_DIR)
        try:
            print(f"  {str(rel):58s} {clean(path, dry_run)}")
        except (ValueError, OSError) as exc:
            print(f"  {str(rel):58s} ERROR: {exc}", file=sys.stderr)
            failures += 1

    if dry_run:
        print("(dry run — nothing written)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
