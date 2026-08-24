"""Renders the CRAFT QUEUE task bar icon.

A hexagon with a three-line work list inside, matching the shape and the muted
gold of the glyphs the bottom bar already has. Before this, the button showed
the cloned HIDEOUT glyph with a gold tint applied from code - which the
button's animator overwrites every frame, so it stayed ivory and looked like a
second HIDEOUT entry.

Three constraints are baked into the numbers below, each learned in the game:

1. Hexagon, not circle. A ring reads as ragged at the ~24 px the bar gives an
   icon, because a curve that size is all antialiasing; flat edges land on
   pixel boundaries.
2. The colour is in the PNG. Tinting from code loses against the animator.
3. Small beats large. The sprite is drawn near its real size rather than
   downsampled from something big, and the loader adds a mip chain.

Run:  python tools/build-icon.py
Out:  assets/task-bar-icon.png  (64x64 RGBA)
"""

import math
import os
from PIL import Image, ImageDraw

SIZE = 64            # close to the ~24-40 px the bar actually shows
SUPERSAMPLE = 16     # drawn 16x and downscaled: antialiasing without a vector library
GOLD = (194, 174, 110, 255)

W = SIZE * SUPERSAMPLE
STROKE = 0.085 * W   # hexagon wall, ~8.5% of the width; thinner vanishes at 24 px
INSET = 0.04         # gap between canvas edge and the hexagon's outer points

# The work list: three lines of decreasing length, as fractions of the canvas.
LINE_LEFT = 0.35
LINE_WIDTHS = [0.30, 0.24, 0.17]
LINE_CENTRES = [0.38, 0.50, 0.62]
LINE_HALF_HEIGHT = 0.035


def hexagon(radius, flat_top=False):
    """Six points around the centre. Pointy-top by default, like the game's."""
    centre = W / 2.0
    start = 0 if flat_top else -math.pi / 2
    return [
        (centre + radius * math.cos(start + i * math.pi / 3),
         centre + radius * math.sin(start + i * math.pi / 3))
        for i in range(6)
    ]


def draw_hexagon_outline(draw, flat_top=False):
    """Filled hexagon with a smaller one punched out of it.

    Drawing the outline as a closed line instead leaves a visible nib where the
    stroke starts and ends, and thickened joins overshoot the points. Punching
    the middle out gives exact corners: ImageDraw writes RGBA values straight
    into the buffer, so filling with a transparent colour clears those pixels
    rather than blending onto them.
    """
    outer = (0.5 - INSET) * W
    # Uniform wall thickness is measured edge to edge, not point to point: the
    # distance from the centre to an edge is radius * cos(30 deg).
    inner = outer - STROKE / math.cos(math.pi / 6)
    draw.polygon(hexagon(outer, flat_top), fill=GOLD)
    draw.polygon(hexagon(inner, flat_top), fill=(0, 0, 0, 0))


def draw_list(draw):
    for width, centre in zip(LINE_WIDTHS, LINE_CENTRES):
        draw.rectangle([
            LINE_LEFT * W,
            (centre - LINE_HALF_HEIGHT) * W,
            (LINE_LEFT + width) * W,
            (centre + LINE_HALF_HEIGHT) * W,
        ], fill=GOLD)


def render(flat_top=False):
    canvas = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    draw = ImageDraw.Draw(canvas)
    draw_hexagon_outline(draw, flat_top)
    draw_list(draw)
    return canvas.resize((SIZE, SIZE), Image.LANCZOS)


def main():
    icon = render()

    here = os.path.dirname(os.path.abspath(__file__))
    target = os.path.join(here, "..", "assets", "task-bar-icon.png")
    os.makedirs(os.path.dirname(target), exist_ok=True)
    icon.save(target, "PNG", optimize=True)

    print("wrote %s (%dx%d, %d bytes)" % (
        os.path.normpath(target), icon.width, icon.height, os.path.getsize(target)))


if __name__ == "__main__":
    main()
