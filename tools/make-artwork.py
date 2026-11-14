# Builds the sidebar's generated artwork into the given folder:
#
#   desk-mark.png     the split-square mark shown when the rail is folded
#   app.ico           the same mark as the application icon, every size drawn sharp
#   compare-mark.png  the comparison button: the ChatGPT and Claude logos either
#                     side of an open diagonal, where the code draws the accent slash
#
#   python3 tools/make-artwork.py src [sheet.png]
#
# The optional second argument writes a sheet of every icon size to look at.
import sys
from PIL import Image, ImageDraw, ImageChops

SS = 8  # supersampling

def rounded(size, box, radius):
    m = Image.new('L', (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle(box, radius=radius, fill=255)
    return m

def halves(size, box, radius, gap):
    """Two masks: the square's upper-left and lower-right halves, split along
    the bottom-left to top-right diagonal with a gap between them."""
    sq = rounded(size, box, radius)
    x0, y0, x1, y1 = box
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    big = size * 2
    up = Image.new('L', (size, size), 0)
    # Points with (x-cx)+(y-cy) < -gap/sqrt2*... : shift the diagonal by gap/2.
    d = gap / 2 * 2 ** .5
    ImageDraw.Draw(up).polygon([(cx - big, cy + big - d), (cx + big, cy - big - d), (cx - big, cy - big - d)], fill=255)
    lo = Image.new('L', (size, size), 0)
    ImageDraw.Draw(lo).polygon([(cx - big, cy + big + d), (cx + big, cy - big + d), (cx + big, cy + big + d)], fill=255)
    return ImageChops.multiply(sq, up), ImageChops.multiply(sq, lo)

def glyph_mask(px, weak=.5):
    """Alpha mask: strong upper-left half, weaker lower-right half."""
    n = px * SS
    side = n * .74
    o = (n - side) / 2
    up, lo = halves(n, (o, o, o + side, o + side), side * .27, side * .105)
    a = ImageChops.add(up, lo.point(lambda v: int(v * weak)))
    return a.resize((px, px), Image.LANCZOS)

def desk_mark(px=144):
    a = glyph_mask(px)
    im = Image.new('RGBA', (px, px), (0, 0, 0, 0))
    im.putalpha(a)
    return im

def app_icon(px, slash=None):
    n = px * SS
    pad = n * (.035 if px >= 48 else 0.0)
    tile = rounded(n, (pad, pad, n - pad, n - pad), (n - 2 * pad) * .235)
    side = (n - 2 * pad) * (.52 if px >= 32 else .58)
    o = (n - side) / 2
    gap = side * (.105 if px >= 32 else .14)
    up, lo = halves(n, (o, o, o + side, o + side), side * .27, gap)
    im = Image.new('RGBA', (n, n), (0, 0, 0, 0))
    im.paste((21, 22, 26, 255), mask=tile)
    im.paste((244, 245, 247, 255), mask=up)
    im.paste((244, 245, 247, 255), mask=lo.point(lambda v: int(v * .5)))
    if slash:
        s = Image.new('L', (n, n), 0)
        w = gap * .42
        e = side * .5 * .98
        ImageDraw.Draw(s).line([(n / 2 - e, n / 2 + e), (n / 2 + e, n / 2 - e)], fill=255, width=max(1, int(w)))
        im.paste(slash + (255,), mask=s)
    return im.resize((px, px), Image.LANCZOS)

# The comparison artwork is 32 design units square. Each logo sits wholly on
# its own side of the bottom-left to top-right diagonal, so the diagonal stays
# clear for the slash and the two can slide apart when the pointer is over them.
COMPARE_UNIT = 8
COMPARE_LOGO = 15.4     # size of each logo, in design units
COMPARE_OFFSET = 6.3    # how far each logo's centre is from the middle, on both axes


def compare_mark(folder):
    n = 32 * COMPARE_UNIT
    canvas = Image.new('L', (n, n), 0)
    for name, direction in (('chatgpt.png', -1), ('claude-mark.png', 1)):
        art = Image.open(folder + '/' + name).convert('RGBA').getchannel('A')
        art = art.crop(art.getbbox())
        scale = COMPARE_LOGO * COMPARE_UNIT / max(art.size)
        art = art.resize((round(art.size[0] * scale), round(art.size[1] * scale)), Image.LANCZOS)
        centre = n / 2 + direction * COMPARE_OFFSET * COMPARE_UNIT
        canvas.paste(art, (round(centre - art.size[0] / 2), round(centre - art.size[1] / 2)))
    pixels = canvas.load()
    for x in range(n):
        assert pixels[x, n - 1 - x] == 0, 'a logo reaches the diagonal'
    mark = Image.new('RGBA', (n, n), (0, 0, 0, 0))
    mark.putalpha(canvas)
    return mark


if __name__ == '__main__':
    out = sys.argv[1]
    desk_mark().save(out + '/desk-mark.png')
    compare_mark(out).save(out + '/compare-mark.png')
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    images = [app_icon(s) for s in sizes]
    images[-1].save(out + '/app.ico', format='ICO', sizes=[(s, s) for s in sizes], append_images=images[:-1])
    if len(sys.argv) > 2:
        # A sheet to look at.
        sheet = Image.new('RGBA', (1180, 420), (226, 229, 234, 255))
        ImageDraw.Draw(sheet).rectangle([590, 0, 1180, 420], fill=(38, 42, 50, 255))
        for i, bg in enumerate([0, 590]):
            x = bg + 20
            for s in [256, 128, 64, 48, 32, 24, 16]:
                sheet.alpha_composite(app_icon(s), (x, 20)); x += s + 14
            x = bg + 20
            for s in [128, 64, 48, 32, 24, 16]:
                sheet.alpha_composite(app_icon(s, (58, 131, 247)), (x, 290)); x += s + 14
        sheet.save(sys.argv[2])
