# Worst-spot probe: does "sharp horizontal depth change AND fine photo detail in the same small patch" track Ed's sway maxima?
# usage: python -I worst_spot.py <depth.onnx> <pictures dir> <sway-max.tsv> <out.tsv>
import sys, os, time, glob
import numpy as np, cv2, onnxruntime as ort

T0 = time.time(); DEADLINE = 120
model, pics, marks, out = sys.argv[1:5]
ort.set_default_logger_severity(3)
sess = ort.InferenceSession(model, providers=['CPUExecutionProvider'])
MEAN = np.array([0.485, 0.456, 0.406], np.float32); STD = np.array([0.229, 0.224, 0.225], np.float32)
print('model loaded', round(time.time() - T0, 1), 's', flush=True)

def find(name):
    p = os.path.join(pics, name)
    if os.path.exists(p): return p
    for d in sorted(os.listdir(pics)):
        q = os.path.join(pics, d, name)
        if os.path.isdir(os.path.join(pics, d)) and os.path.exists(q): return q
    return None

def depth(rgb):
    h, w = rgb.shape[:2]; s = 518 / max(w, h)
    dw, dh = max(14, round(w * s / 14) * 14), max(14, round(h * s / 14) * 14)
    x = (cv2.resize(rgb, (dw, dh), interpolation=cv2.INTER_AREA).astype(np.float32) / 255 - MEAN) / STD
    d = sess.run(None, {sess.get_inputs()[0].name: x.transpose(2, 0, 1)[None]})[0].reshape(dh, dw)
    return (d - d.min()) / max(1e-6, d.max() - d.min())

def measures(path):
    bgr = cv2.imdecode(np.fromfile(path, np.uint8), cv2.IMREAD_COLOR)
    rgb = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
    raw = depth(rgb); dh, dw = raw.shape
    # The app's Grey: bytes, 3x3 max, 3x3 blur.
    g8 = np.round(raw * 255).astype(np.uint8)
    grey = cv2.blur(cv2.dilate(g8, np.ones((3, 3), np.uint8)), (3, 3)).astype(np.float32) / 255
    # Sway is horizontal: the stretch is the horizontal change of depth, per depth-map pixel.
    ddx = np.abs(np.zeros_like(grey)); ddx[:, 1:-1] = np.abs(grey[:, 2:] - grey[:, :-2]) * 0.5
    # Fine detail of the photo, at twice the depth resolution, kept as the strongest in each 2x2.
    small = cv2.cvtColor(cv2.resize(bgr, (dw * 2, dh * 2), interpolation=cv2.INTER_AREA), cv2.COLOR_BGR2GRAY).astype(np.float32)
    mag = np.hypot(cv2.Sobel(small, cv2.CV_32F, 1, 0), cv2.Sobel(small, cv2.CV_32F, 0, 1)) / (4 * 255)
    detail = mag.reshape(dh, 2, dw, 2).max(axis=(1, 3))
    k = max(3, round(dw * 0.02)) | 1  # patch about 2% of the width
    both = cv2.blur(ddx * detail, (k, k))
    only_depth = cv2.blur(ddx, (k, k))
    return dict(spot=float(both.max()), spot99=float(np.percentile(both, 99.9)), depthspot=float(only_depth.max()))

rows = []
for line in open(marks, encoding='utf-8').read().splitlines()[1:]:
    c = line.split('\t')
    if len(c) < 3 or not c[2]: continue
    if time.time() - T0 > DEADLINE: print('DEADLINE'); break
    p = find(c[1])
    if p is None: print('missing', c[1]); continue
    t = time.time(); m = measures(p)
    rows.append(dict(scene=int(c[0]), file=c[1], ed=float(c[2]), computed=float(c[3]), **m))
    print(c[0], c[1][:32], c[2], {k2: round(v, 4) for k2, v in m.items()}, round(time.time() - t, 1), 's', flush=True)

def rank(a): return np.argsort(np.argsort(a)).astype(float)
def evaluate(key):
    ed = np.array([r['ed'] for r in rows]); x = np.array([r[key] for r in rows])
    free = ed < 4.0  # 4.0 is the slider's top: Ed's real max may be higher
    kfit = float(np.median(ed[free] * x[free]))
    pred = np.clip(kfit / np.maximum(x, 1e-9), 0.4, 4.0)
    hit = np.where(ed >= 4.0, pred >= 3.7, np.abs(pred - ed) <= 0.3)
    rho = float(np.corrcoef(rank(ed), rank(-x))[0, 1])
    return pred, hit, rho
with open(out, 'w', encoding='utf-8') as f:
    res = {k2: evaluate(k2) for k2 in ('spot', 'spot99', 'depthspot')}
    ed = np.array([r['ed'] for r in rows]); comp = np.array([r['computed'] for r in rows])
    hitc = np.where(ed >= 4.0, comp >= 3.7, np.abs(comp - ed) <= 0.3)
    print('computed (current bin formula): hits', int(hitc.sum()), '/', len(rows), 'rank corr', round(float(np.corrcoef(rank(ed), rank(comp))[0, 1]), 2))
    for k2, (pred, hit, rho) in res.items():
        print(k2, ': hits', int(hit.sum()), '/', len(rows), 'rank corr', round(rho, 2))
    f.write('scene\tfile\ted\tcomputed\tspot pred\tspot\tdepth-only pred\n')
    for i, r in enumerate(rows):
        f.write(f"{r['scene']}\t{r['file']}\t{r['ed']}\t{r['computed']}\t{res['spot'][0][i]:.1f}\t{r['spot']:.4f}\t{res['depthspot'][0][i]:.1f}\n")
print('done', round(time.time() - T0, 1), 's')
