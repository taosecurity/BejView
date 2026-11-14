"""Draws the BejView icon (dark rounded tile, lines of text, one highlighted line, a line-number gutter) as PNG and ICO.

Run: python -I tools\\make_icon.py  (needs Pillow). Writes src/BejView.App/Assets/bejview.png and .ico.
With --linux it writes only the hicolor PNGs in packaging/linux/icons (the Linux .desktop icon).
"""
import os
import sys

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HERE, "..", "src", "BejView.App", "Assets")


def draw(size: int) -> Image.Image:
    s = size * 4  # draw large, then shrink for smooth edges
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=s * 0.22, fill=(30, 33, 48, 255))

    gutter_x = s * 0.18
    left = s * 0.30
    top = s * 0.20
    step = s * 0.12
    h = s * 0.055
    widths = [0.52, 0.40, 0.56, 0.30, 0.48]
    for i, w in enumerate(widths):
        y = top + i * step
        if i == 2:  # the highlighted (matched) line
            d.rounded_rectangle([s * 0.12, y - h * 0.9, s * 0.88, y + h * 1.9], radius=h, fill=(255, 196, 61, 255))
            colour = (30, 33, 48, 255)
        else:
            colour = (230, 233, 240, 255)
        d.ellipse([gutter_x - h * 0.5, y, gutter_x + h * 0.5, y + h], fill=(120, 130, 155, 255) if i != 2 else (30, 33, 48, 255))
        d.rounded_rectangle([left, y, left + s * w, y + h], radius=h / 2, fill=colour)
    return img.resize((size, size), Image.LANCZOS)


LINUX_ICONS = os.path.join(HERE, "..", "packaging", "linux", "icons", "hicolor")
LINUX_SIZES = (16, 22, 24, 32, 48, 64, 128, 256, 512)


def linux_icons() -> int:
    """hicolor PNGs for the Linux .desktop entry (step 4, 2026-11-13), each size drawn on its own for sharp edges."""
    for n in LINUX_SIZES:
        d = os.path.join(LINUX_ICONS, f"{n}x{n}", "apps")
        os.makedirs(d, exist_ok=True)
        draw(n).save(os.path.join(d, "bejview.png"))
    print("wrote", os.path.normpath(LINUX_ICONS), "sizes", ", ".join(map(str, LINUX_SIZES)))
    return 0


def main() -> int:
    if "--linux" in sys.argv[1:]:
        return linux_icons()
    os.makedirs(ASSETS, exist_ok=True)
    big = draw(256)
    big.save(os.path.join(ASSETS, "bejview.png"))
    big.save(os.path.join(ASSETS, "bejview.ico"), sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    print("wrote", os.path.normpath(os.path.join(ASSETS, "bejview.png")), "and .ico")
    return 0


if __name__ == "__main__":
    sys.exit(main())
