#!/usr/bin/env python3
"""Generate src/AlbumWall.App/Assets/app.ico and app-icon.png.

Drawn in code rather than shipped as an opaque binary, so anyone can see exactly
what it is and regenerate it — the same arrangement as the station tools
(FlexPad, LP-100A Monitor, W2 Monitor, Shack Power), whose icons all come from a
tools/make_icon.py like this one.

Motif: the app's own screen, reduced to what survives at 16 px. A dark rounded
plate; a row of three album covers; beneath it the panel that unfolds when one
is opened, tinted like the cover it belongs to and pointed at it by a small
arrow; then another row of covers carrying on below. It is the one interaction
the whole program is built around, and no other player's icon looks like it.

Requires Pillow (dev-time only; the outputs are committed, so nobody building
the app needs this).
"""

import os

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(HERE, "..", "src", "AlbumWall.App", "Assets")

# Draw huge, downscale for anti-aliasing.
S = 1024

PLATE = (30, 28, 27, 255)          # the wall's ground at its default lightness, a shade darker
COVERS = [                          # six covers: saturated and unlike each other, as a real wall is
    (196, 62, 48, 255),             # red
    (232, 170, 52, 255),            # amber   <- the open one
    (52, 110, 168, 255),            # blue
    (92, 148, 92, 255),             # green
    (150, 86, 160, 255),            # violet
    (214, 120, 60, 255),            # orange
]
OPEN = 1                            # index of the cover whose panel is unfolded
PANEL = (92, 70, 28, 255)           # the open cover's color, taken down — as the real panel is tinted
LINE = (238, 222, 190, 255)         # track-list lines; only visible at the larger sizes


def draw_master():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # Rounded-square plate. Radius as on the station tools, so the silhouette
    # still reads square at 16 px instead of collapsing into a circle.
    d.rounded_rectangle((0, 0, S - 1, S - 1), radius=S // 5, fill=PLATE)

    margin = S * 0.13
    gap = S * 0.055
    side = (S - 2 * margin - 2 * gap) / 3           # three covers across
    panel_h = S - 2 * margin - 2 * side - 2 * gap   # whatever two rows of covers leave

    def cover(index, row_top):
        col = index % 3
        x0 = margin + col * (side + gap)
        d.rectangle((x0, row_top, x0 + side, row_top + side), fill=COVERS[index])
        return x0

    # Top row.
    top = margin
    open_x = None
    for i in range(3):
        x0 = cover(i, top)
        if i == OPEN:
            open_x = x0

    # The panel, full width, with its arrow pointing up at the open cover.
    panel_top = top + side + gap
    d.rectangle((margin, panel_top, S - margin, panel_top + panel_h), fill=PANEL)
    ax = open_x + side / 2
    aw = side * 0.26
    d.polygon([(ax - aw, panel_top + 1), (ax + aw, panel_top + 1), (ax, panel_top - gap * 0.92)], fill=PANEL)

    # A few lines of track list. Gone by 32 px, which is fine: at that size the
    # panel reads as a bar, and the bar is the point.
    lx0 = margin + panel_h * 0.22
    for k, frac in enumerate((0.62, 0.48, 0.55)):
        y = panel_top + panel_h * (0.26 + 0.24 * k)
        d.rounded_rectangle((lx0, y - S * 0.011, lx0 + (S - 2 * margin) * frac, y + S * 0.011),
                            radius=S * 0.011, fill=LINE)

    # Bottom row: the wall carries on below the panel.
    bottom = panel_top + panel_h + gap
    for i in range(3, 6):
        cover(i, bottom)

    return img


def main():
    os.makedirs(ASSETS, exist_ok=True)
    master = draw_master()
    ico = os.path.join(ASSETS, "app.ico")
    png = os.path.join(ASSETS, "app-icon.png")
    sizes = [16, 24, 32, 48, 64, 128, 256]
    master.save(ico, format="ICO", sizes=[(s, s) for s in sizes])
    master.resize((256, 256), Image.LANCZOS).save(png, format="PNG")
    print(f"wrote {os.path.normpath(ico)} ({os.path.getsize(ico)} bytes, sizes {sizes})")
    print(f"wrote {os.path.normpath(png)} ({os.path.getsize(png)} bytes)")


if __name__ == "__main__":
    main()
