# Ed's marks against the measures: does any single measure, or pair, separate pass from fail?
import json, re, sys, itertools
rows = {r['file']: r for r in json.load(open(sys.argv[1]))}
marks = {}
for line in open(sys.argv[2], encoding='utf-8-sig'):
    m = re.match(r'(PASS|FAIL) \((\w+), played (\w+)\): (.+)$', line.strip())
    if m: marks[m.group(4)] = (m.group(1) == 'PASS', m.group(3))
feats = ['area', 'vague', 'sides', 'edge', 'gap', 'tex']
data = [(rows[f], ok, play) for f, (ok, play) in marks.items() if f in rows]
print(len(data), 'marked;', sum(ok for _, ok, _ in data), 'pass')
for play in ('Slide', 'Rock'):
    sub = [(r, ok) for r, ok, p in data if p == play]
    print(f'--- played {play}: {len(sub)}, {sum(ok for _, ok in sub)} pass')
    for f in feats:
        P = sorted(r[f] for r, ok in sub if ok); F = sorted(r[f] for r, ok in sub if not ok)
        if P and F: print(f'  {f:6} pass {P[0]:+.2f}..{P[-1]:+.2f} median {P[len(P)//2]:+.2f} | fail {F[0]:+.2f}..{F[-1]:+.2f} median {F[len(F)//2]:+.2f}')
    # Best single threshold per feature (either direction): how many marks it gets right.
    best = []
    for f in feats:
        vals = sorted(set(r[f] for r, _ in sub))
        for t in vals:
            for d in (1, -1):
                right = sum((d * (r[f] - t) >= 0) == ok for r, ok in sub)
                best.append((right, f, t, '>=' if d > 0 else '<='))
    best.sort(reverse=True)
    for b in best[:5]: print(f'  keep if {b[1]} {b[3]} {b[2]:.3f}: {b[0]} of {len(sub)} right')
print('--- failures, played Slide')
for r, ok, p in data:
    if not ok: print(f"  {r['file'][:50]:50} a{r['area']:.2f} s{r['sides']:.2f} edge{r['edge']:+.2f} gap{r['gap']:+.2f} tex{r['tex']:.3f} v{r['vague']:.2f}")
