# The app's judged lines beside the offline measures for the same photos.
import json, re, sys
rows = {r['file']: r for r in json.load(open(sys.argv[1]))}
pat = re.compile(r'judged (.+?): (\w+) \((.+?)\), subject ([\d.]+), edge (-?[\d.]+), texture ([\d.]+), sides ([\d.]+)')
for line in open(sys.argv[2], encoding='utf-8-sig'):
    m = pat.search(line)
    if not m: continue
    f, fit, why, area, edge, tex, sides = m.groups()
    o = rows.get(f)
    off = f"offline a{o['area']:.2f} edge{o['edge']:+.2f} tex{o['tex']:.3f} s{o['sides']:.2f}" if o else 'not measured offline'
    print(f"{f[:40]:40} app {fit:5} a{float(area):.2f} edge{float(edge):+.2f} tex{float(tex):.3f} s{float(sides):.2f} | {off}")
