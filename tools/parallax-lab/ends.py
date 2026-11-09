# First and middle (furthest) frame of a rendered clip, side by side. usage: python -I ends.py <review dir> <file> <out.jpg>
import json, os, sys
from PIL import Image
d, f, out = sys.argv[1:4]
card = next(c for c in json.load(open(os.path.join(d, 'cards.json'))) if c['file'] == f)
im = Image.open(os.path.join(d, card['anim']))
n = im.n_frames
im.seek(0); a = im.convert('RGB').copy()
im.seek(n // 2 - 1); b = im.convert('RGB').copy()
sheet = Image.new('RGB', (a.width * 2 + 8, a.height))
sheet.paste(a, (0, 0)); sheet.paste(b, (a.width + 8, 0))
sheet.save(out, quality=90)
print(f, n, 'frames')
