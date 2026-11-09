# Parallax suitability measures for every photo given: the subject (BiRefNet), the depth gap at the subject's own edge
# (subject against a ring of background just around it), and how much texture that ring has.
# usage: python -I judge.py <depth.onnx> <birefnet.onnx> <outdir> <folder>
import sys, os, json, time, glob
import numpy as np
import onnxruntime as ort
import cv2

depth_model, cut_model, outdir, folder = sys.argv[1:5]
os.makedirs(outdir, exist_ok=True)
prov = ['DmlExecutionProvider', 'CPUExecutionProvider']
ort.set_default_logger_severity(3)
dsess = ort.InferenceSession(depth_model, providers=prov)
csess = ort.InferenceSession(cut_model, providers=prov)
MEAN = np.array([0.485, 0.456, 0.406], np.float32); STD = np.array([0.229, 0.224, 0.225], np.float32)

def depth_of(rgb):
    h, w = rgb.shape[:2]; s = 518 / max(w, h)
    dw, dh = max(14, round(w * s / 14) * 14), max(14, round(h * s / 14) * 14)
    x = (cv2.resize(rgb, (dw, dh), interpolation=cv2.INTER_AREA).astype(np.float32) / 255 - MEAN) / STD
    d = dsess.run(None, {dsess.get_inputs()[0].name: x.transpose(2, 0, 1)[None]})[0].reshape(dh, dw)
    d = (d - d.min()) / max(1e-6, d.max() - d.min())
    return cv2.resize(d, (w, h), interpolation=cv2.INTER_CUBIC)

def cut_of(rgb):
    h, w = rgb.shape[:2]
    x = (cv2.resize(rgb, (1024, 1024), interpolation=cv2.INTER_AREA).astype(np.float32) / 255 - MEAN) / STD
    out = csess.run(None, {csess.get_inputs()[0].name: x.transpose(2, 0, 1)[None]})[-1].reshape(1024, 1024)
    return cv2.resize(1 / (1 + np.exp(-out)), (w, h), interpolation=cv2.INTER_LINEAR)

files = sorted(f for f in glob.glob(os.path.join(folder, '*')) if f.lower().endswith(('.jpg', '.jpeg', '.png')))
rows = []
t0 = time.time()
for path in files:
    bgr = cv2.imread(path)
    if bgr is None: continue
    h0, w0 = bgr.shape[:2]; s = 768 / max(w0, h0)
    bgr = cv2.resize(bgr, (round(w0 * s), round(h0 * s)), interpolation=cv2.INTER_AREA)
    h, w = bgr.shape[:2]
    rgb = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
    D = depth_of(rgb); M = cut_of(rgb)
    fg = M > 0.5
    area = float(fg.mean())
    vague = float(((M > 0.1) & (M < 0.9)).mean() / max(area, 1e-3))
    sides = float(np.mean([fg[0, :].mean(), fg[:, 0].mean(), fg[:, -1].mean()]))
    gap = edge = tex = 0.0
    if 0 < area < 0.98:
        r1, r2 = max(2, int(0.01 * w)), max(6, int(0.06 * w))
        near_ring = cv2.dilate(fg.astype(np.uint8), np.ones((2 * r1 + 1,) * 2, np.uint8)).astype(bool)
        far_ring = cv2.dilate(fg.astype(np.uint8), np.ones((2 * r2 + 1,) * 2, np.uint8)).astype(bool)
        ring = far_ring & ~near_ring
        inner = fg & ~cv2.erode(fg.astype(np.uint8), np.ones((2 * r2 + 1,) * 2, np.uint8)).astype(bool)
        if ring.any() and inner.any():
            edge = float(np.median(D[inner]) - np.median(D[ring]))     # the step at the subject's own edge
            gap = float(np.median(D[fg]) - np.median(D[~fg]))           # subject against all the rest
            grey = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY).astype(np.float32) / 255
            g = np.hypot(cv2.Sobel(grey, cv2.CV_32F, 1, 0), cv2.Sobel(grey, cv2.CV_32F, 0, 1))
            tex = float(np.mean(g[ring]))                               # how much detail moves behind it
    rows.append(dict(file=os.path.basename(path), area=round(area, 3), vague=round(vague, 3), sides=round(sides, 3),
                     edge=round(edge, 3), gap=round(gap, 3), tex=round(tex, 4)))
    th = cv2.resize(bgr, (320, round(320 * h / w)))
    mm = cv2.resize(M, (th.shape[1], th.shape[0]))[..., None]
    view = (th * (0.3 + 0.7 * mm)).astype(np.uint8)
    cv2.putText(view, f'a{area:.2f} e{edge:.2f} t{tex:.3f} s{sides:.2f} v{vague:.2f}', (4, 16), cv2.FONT_HERSHEY_SIMPLEX, 0.42, (0, 255, 255), 1)
    cv2.imwrite(os.path.join(outdir, os.path.splitext(os.path.basename(path))[0][:60] + '_cut.jpg'), view)
json.dump(rows, open(os.path.join(outdir, 'measures.json'), 'w'), indent=1)
print('done', len(rows), 'photos in', round(time.time() - t0), 's', flush=True)
