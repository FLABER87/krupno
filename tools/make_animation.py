#!/usr/bin/env python3
"""Assembles window frames into the animation at the top of the README (docs/images/split-*.webp).

tests/readme_screenshot.ps1 records the frames and runs this script. The folder holds PNG frames and frames.txt, one
"<file> <milliseconds>" line per frame, in order. The frames are scaled down to --width and saved as a looping lossy
WebP; the encoder stores only what changes from frame to frame, so the moving divider costs little.

Run: python tools/make_animation.py <frames folder> <output .webp> [--width 1400] [--quality 80]   (requires Pillow)
"""

import argparse
from pathlib import Path

from PIL import Image


def main() -> None:
    parser = argparse.ArgumentParser(description="Assembles window frames into an animated WebP.")
    parser.add_argument("frames", type=Path, help="folder with the PNG frames and frames.txt")
    parser.add_argument("output", type=Path, help="the .webp to write")
    parser.add_argument("--width", type=int, default=1400, help="width of the animation in pixels")
    parser.add_argument("--quality", type=int, default=80, help="WebP quality, 0-100")
    args = parser.parse_args()

    frames, durations = [], []
    for line in (args.frames / "frames.txt").read_text(encoding="utf-8").splitlines():
        name, milliseconds = line.split()
        with Image.open(args.frames / name) as image:
            width = min(args.width, image.width)
            size = (width, round(image.height * width / image.width))
            frames.append(image.convert("RGB").resize(size, Image.LANCZOS))
        durations.append(int(milliseconds))

    frames[0].save(args.output, save_all=True, append_images=frames[1:], duration=durations, loop=0,
                   quality=args.quality, method=6)
    print(f"{args.output}: {len(frames)} frames, {sum(durations) / 1000:.1f} s, "
          f"{args.output.stat().st_size // 1024} KB")


if __name__ == "__main__":
    main()
