#!/usr/bin/env python3
"""Generate src/AlbumWall.App/Assets/app.ico and app-icon.png from icon.svg.

The SOURCE is Assets/icon.svg — a text file anyone can read, diff and edit — and
this renders the two files the app actually embeds. Drawn from source rather
than shipped as an opaque binary, the same arrangement as the station tools
(FlexPad, LP-100A Monitor, W2 Monitor, Shack Power). Want a different sleeve
color? It is one gradient at the top of the SVG; change it and run this.

Every size in the .ico is rendered FROM THE SVG AT THAT SIZE, not shrunk from
the big one: a 16 px frame resampled from 256 px is mush, while the renderer
drawing 16 px directly keeps the edges it can.

Requires ImageMagick built with librsvg (`magick`), at dev time only. The
outputs are committed, so nobody building the app needs this.
"""

import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HERE, "..", "src", "AlbumWall.App", "Assets")
SVG = os.path.join(ASSETS, "icon.svg")

# What Windows asks an .ico for, smallest to largest. 256 is also the Linux
# icon: it goes into the hicolor theme as app-icon.png.
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def render(size: int, out: str) -> None:
    # -density scales the SVG's own rasterization, so small sizes are drawn
    # small rather than drawn large and averaged down.
    density = max(72 * size / 512, 1)
    subprocess.run(
        ["magick", "-background", "none", "-density", f"{density:.4f}", SVG,
         "-resize", f"{size}x{size}", "-strip", out],
        check=True)


def main() -> int:
    if not shutil.which("magick"):
        print("ImageMagick (`magick`) is needed to render the icon.", file=sys.stderr)
        return 1

    with tempfile.TemporaryDirectory() as tmp:
        frames = []
        for size in SIZES:
            frame = os.path.join(tmp, f"{size}.png")
            render(size, frame)
            frames.append(frame)

        shutil.copyfile(frames[-1], os.path.join(ASSETS, "app-icon.png"))
        subprocess.run(["magick", *frames, os.path.join(ASSETS, "app.ico")], check=True)

    print(f"wrote app.ico ({len(SIZES)} sizes) and app-icon.png (256 px)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
