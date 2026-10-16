#!/usr/bin/env python3
"""Draws the Krupno icon (src/Krupno.App/Assets/Krupno.ico) and a preview of all sizes.

Motif: large source "pixels" at the bottom left → an arrow → the smooth frame corner of the upscaled
image at the top right. For 16–32 px a simplified version is drawn, without the arrow and with thicker
lines, so the icon stays legible in the taskbar.

Run: python tools/make_icon.py   (requires Pillow and numpy)
"""

import tempfile
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
TARGET = ROOT / "src" / "Krupno.App" / "Assets" / "Krupno.ico"
CANVAS = 1024
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
TOP_LEFT = np.array([0x7C, 0x5C, 0xFA], dtype=np.float32)      # purple
BOTTOM_RIGHT = np.array([0x25, 0x63, 0xEB], dtype=np.float32)  # blue


def background() -> Image.Image:
    """Rounded square with a diagonal gradient."""
    y, x = np.mgrid[0:CANVAS, 0:CANVAS].astype(np.float32)
    t = ((x + y) / (2 * CANVAS - 2))[..., None]
    rgb = TOP_LEFT * (1 - t) + BOTTOM_RIGHT * t
    rgba = np.concatenate([rgb, np.full((CANVAS, CANVAS, 1), 255, np.float32)], axis=2).astype(np.uint8)
    image = Image.fromarray(rgba)  # 4 uint8 channels → RGBA

    mask = Image.new("L", (CANVAS, CANVAS), 0)
    ImageDraw.Draw(mask).rounded_rectangle((40, 40, CANVAS - 40, CANVAS - 40), radius=220, fill=255)
    image.putalpha(mask)
    return image


def foreground(simple: bool) -> Image.Image:
    layer = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)
    white = lambda alpha: (255, 255, 255, int(255 * alpha))  # noqa: E731

    # Source "pixels": 2×2 squares of varying brightness.
    cell, gap = (150, 34) if simple else (124, 26)
    left, top = (200, 520) if simple else (220, 560)
    for row in range(2):
        for col in range(2):
            alpha = (1.0, 0.72, 0.72, 0.5)[row * 2 + col]
            x0, y0 = left + col * (cell + gap), top + row * (cell + gap)
            draw.rounded_rectangle((x0, y0, x0 + cell, y0 + cell), radius=cell // 6, fill=white(alpha))

    # Smooth frame corner of the upscaled image.
    stroke = 92 if simple else 64
    half = stroke // 2
    x_start, corner_x, corner_y, y_end = (520, 800, 224, 504) if simple else (560, 780, 250, 470)
    draw.rounded_rectangle((x_start - half, corner_y - half, corner_x + half, corner_y + half), radius=half, fill=white(1))
    draw.rounded_rectangle((corner_x - half, corner_y - half, corner_x + half, y_end + half), radius=half, fill=white(1))

    if not simple:
        # Upscale arrow from the pixels to the frame.
        direction = np.array([1.0, -1.0]) / np.sqrt(2)
        normal = np.array([1.0, 1.0]) / np.sqrt(2)
        tip = np.array([724.0, 306.0])
        base = tip - direction * 118
        draw.line([(512, 518), tuple(base + direction * 10)], fill=white(1), width=50)
        for end in ((512, 518),):
            draw.ellipse((end[0] - 25, end[1] - 25, end[0] + 25, end[1] + 25), fill=white(1))
        head = [tuple(tip), tuple(base + normal * 78), tuple(base - normal * 78)]
        draw.polygon(head, fill=white(1))
    return layer


def render(size: int) -> Image.Image:
    simple = size <= 32
    image = Image.alpha_composite(background(), foreground(simple))
    return image.resize((size, size), Image.LANCZOS)


def main() -> None:
    images = {size: render(size) for size in SIZES}
    TARGET.parent.mkdir(parents=True, exist_ok=True)
    largest = images[SIZES[-1]]
    largest.save(TARGET, format="ICO", sizes=[(s, s) for s in SIZES],
                 append_images=[images[s] for s in SIZES[:-1]])

    # Preview of all sizes on a light and a dark background, for the eye only; not part of the build.
    preview = Image.new("RGBA", (sum(SIZES) + 16 * (len(SIZES) + 1), 2 * 256 + 48), (0, 0, 0, 0))
    for row, color in enumerate(((243, 243, 243, 255), (32, 32, 32, 255))):
        y = 16 + row * (256 + 16)
        ImageDraw.Draw(preview).rectangle((0, y - 16, preview.width, y + 256 + 8), fill=color)
        x = 16
        for size in SIZES:
            preview.alpha_composite(images[size], (x, y + 256 - size))
            x += size + 16
    preview_path = Path(tempfile.gettempdir()) / "krupno-icon-preview.png"
    preview.save(preview_path)
    print(f"{TARGET} ({TARGET.stat().st_size // 1024} KB), sizes: {', '.join(map(str, SIZES))}")
    print(f"Preview: {preview_path}")


if __name__ == "__main__":
    main()
