"""Generate GCalendarSync's tile art: a sync arc around a calendar page.

Deliberately the same family as GContactsSync - identical Google blue, identical white sync arc,
identical placement - with the person swapped for a calendar. The two apps are siblings and should
look like it on the start screen.

Placement is copied from the measured originals, not invented: the medium tile's art sits high
(centre y 0.358) so the arc clears the name strip WP8.1 draws along the bottom.
"""
from PIL import Image, ImageDraw
import math
import os

BLUE = (26, 115, 232, 255)
WHITE = (255, 255, 255, 255)
SS = 8                      # supersample, for antialiasing


def polar(cx, cy, radius, degrees):
    a = math.radians(degrees)
    return cx + radius * math.cos(a), cy + radius * math.sin(a)


def draw_icon(draw, cx, cy, r):
    """The whole glyph, centred on (cx, cy) with the arc at radius r."""
    stroke = r * 0.155

    # ---- the sync arc: almost a full circle, with a gap at about 4 o'clock ----
    box = [cx - r, cy - r, cx + r, cy + r]
    draw.arc(box, start=38, end=4, fill=WHITE, width=int(round(stroke)))

    # ---- the arrowhead, on the clockwise end of the arc, pointing outward ----
    tip = polar(cx, cy, r * 1.34, 12)
    back_outer = polar(cx, cy, r * 1.02, -16)
    back_inner = polar(cx, cy, r * 0.70, 10)
    draw.polygon([tip, back_outer, back_inner], fill=WHITE)

    # ---- the calendar page ----
    half_w = r * 0.50
    top = cy - r * 0.42
    bottom = cy + r * 0.52
    radius = r * 0.09

    draw.rounded_rectangle([cx - half_w, top, cx + half_w, bottom],
                           radius=radius, fill=WHITE)

    # Two tabs over the top edge, the thing that says "calendar" rather than "page".
    tab_w = r * 0.13
    tab_top = top - r * 0.26
    for dx in (-r * 0.28, r * 0.28):
        draw.rounded_rectangle([cx + dx - tab_w / 2, tab_top, cx + dx + tab_w / 2, top + r * 0.10],
                               radius=tab_w / 2, fill=WHITE)

    # The header band, cut out of the page in the background colour.
    band_top = top + r * 0.26
    draw.rectangle([cx - half_w, band_top, cx + half_w, band_top + r * 0.12], fill=BLUE)

    # The grid: three columns, two rows. Sized to leave a margin inside the page on every side -
    # at 106 pixels a cell that touches the edge turns the page into a smudge.
    cell = r * 0.15
    gap = r * 0.08
    grid_w = 3 * cell + 2 * gap
    x0 = cx - grid_w / 2
    y0 = cy + r * 0.08
    for row in range(2):
        for col in range(3):
            x = x0 + col * (cell + gap)
            y = y0 + row * (cell + gap)
            draw.rectangle([x, y, x + cell, y + cell], fill=BLUE)


def render(path, width, height, centre_x, centre_y, art_width):
    """art_width is the glyph's full width as a fraction of the image width."""
    im = Image.new('RGBA', (width * SS, height * SS), BLUE)
    draw = ImageDraw.Draw(im)

    # art_width spans the arrowhead, which reaches 1.34r, so the circle is smaller than it looks.
    r = (art_width * width * SS) / 2.0 / 1.17
    draw_icon(draw, centre_x * width * SS, centre_y * height * SS, r)

    im = im.resize((width, height), Image.LANCZOS)
    im.save(path, 'PNG')
    print('  %-44s %dx%d' % (path, width, height))


if __name__ == '__main__':
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'icons')
    if not os.path.isdir(out):
        os.makedirs(out)

    # Sizes, placement and art width all taken from the measured originals.
    render(os.path.join(out, 'Logo.scale-240.png'), 360, 360, 0.503, 0.358, 0.494)
    render(os.path.join(out, 'Square71x71Logo.scale-240.png'), 170, 170, 0.506, 0.500, 0.753)
    render(os.path.join(out, 'SmallLogo.scale-240.png'), 106, 106, 0.500, 0.500, 0.760)
    render(os.path.join(out, 'StoreLogo.scale-240.png'), 120, 120, 0.500, 0.500, 0.760)
    render(os.path.join(out, 'SplashScreen.scale-240.png'), 1152, 1920, 0.503, 0.449, 0.278)
