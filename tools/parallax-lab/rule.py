# Apply the proposed rule to the measures and tile what it passes (Slide, Rock) so they can be looked at.
import json, sys, os, cv2, numpy as np
rows = json.load(open(sys.argv[1])); cuts = sys.argv[2]; out = sys.argv[3]
def verdict(r):
    if r['area'] < 0.04 or r['vague'] >= 0.35: return 'out: no clear subject'
    if r['edge'] < 0.40: return 'out: no depth gap at its edge'
    if r['tex'] < 0.03: return 'out: plain background'
    return 'Slide' if r['area'] <= 0.65 and r['sides'] < 0.3 else 'Rock'
counts = {}
passed = []
for r in rows:
    v = verdict(r); counts[v] = counts.get(v, 0) + 1
    if v in ('Slide', 'Rock'): passed.append((v, r['file']))
print(counts)
W, H, cols = 320, 220, 6
rowsN = (len(passed) + cols - 1) // cols
sheet = np.zeros((rowsN * H, cols * W, 3), np.uint8)
for i, (v, f) in enumerate(passed):
    p = os.path.join(cuts, os.path.splitext(f)[0][:60] + '_cut.jpg')
    im = cv2.imread(p)
    if im is None: continue
    s = min(W / im.shape[1], (H - 20) / im.shape[0]); im = cv2.resize(im, (int(im.shape[1] * s), int(im.shape[0] * s)))
    y, x = (i // cols) * H, (i % cols) * W
    sheet[y:y + im.shape[0], x:x + im.shape[1]] = im
    cv2.putText(sheet, f'{v}: {f[:30]}', (x + 3, y + H - 5), cv2.FONT_HERSHEY_SIMPLEX, 0.4, (0, 255, 0) if v == 'Slide' else (255, 200, 0), 1)
cv2.imwrite(out, sheet, [cv2.IMWRITE_JPEG_QUALITY, 80])
print(len(passed), 'passed')
