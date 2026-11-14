# Builds the application icon (src/app.ico): the ChatGPT and Claude halves from
# compare-mark.png on a dark tile, with the accent slash between them. Each
# size is composed on its own, so small sizes stay sharp.
#
#   python3 tools/make-artwork.py src [sheet.png]
#
# The optional second argument writes a sheet of every icon size to look at.
import sys
from PIL import Image, ImageDraw

SS = 8  # supersampling
TILE = (21, 22, 26)
INK = (244, 245, 247)
SLASH = (58, 131, 247)
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def app_icon(px, mark):
    n = px * SS
    pad = n * (.035 if px >= 48 else 0.0)
    tile = Image.new('L', (n, n), 0)
    ImageDraw.Draw(tile).rounded_rectangle((pad, pad, n - pad, n - pad), radius=(n - 2 * pad) * .235, fill=255)
    icon = Image.new('RGBA', (n, n), (0, 0, 0, 0))
    icon.paste(TILE + (255,), mask=tile)
    # The artwork fills more of a small icon, where every pixel counts.
    side = int((n - 2 * pad) * (.66 if px >= 32 else .78))
    origin = (n - side) // 2
    shape = mark.resize((side, side), Image.LANCZOS)
    icon.paste(INK + (255,), (origin, origin), shape)
    # The slash runs bottom-left to top-right through the gap in the artwork.
    reach = side * .37
    width = max(SS, int(side * (.075 if px >= 32 else .1)))
    line = Image.new('L', (n, n), 0)
    draw = ImageDraw.Draw(line)
    a, b = (n / 2 - reach, n / 2 + reach), (n / 2 + reach, n / 2 - reach)
    draw.line([a, b], fill=255, width=width)
    for x, y in (a, b):
        draw.ellipse((x - width / 2, y - width / 2, x + width / 2, y + width / 2), fill=255)
    icon.paste(SLASH + (255,), mask=line)
    return icon.resize((px, px), Image.LANCZOS)


if __name__ == '__main__':
    folder = sys.argv[1]
    mark = Image.open(folder + '/compare-mark.png').convert('RGBA').getchannel('A')
    images = [app_icon(size, mark) for size in SIZES]
    images[-1].save(folder + '/app.ico', format='ICO', sizes=[(s, s) for s in SIZES], append_images=images[:-1])
    if len(sys.argv) > 2:
        sheet = Image.new('RGBA', (1180, 300), (226, 229, 234, 255))
        ImageDraw.Draw(sheet).rectangle([590, 0, 1180, 300], fill=(38, 42, 50, 255))
        for ground in (0, 590):
            x = ground + 20
            for size in reversed(SIZES):
                if size in (40, 20):
                    continue
                sheet.alpha_composite(app_icon(size, mark), (x, 20))
                x += size + 14
        sheet.save(sys.argv[2])
