# Render each photo playing Slide with the holistic foreground mask (BiRefNet subject + connected foreground above the far
# background plane, snapped to photo edges via guided filter) and pure rigid 4.2% horizontal background slide (constant
# overscan window, zero vertical zoom halo).
# Also filters out screenshots, vertical top-to-bottom pillars, and tabletop/floor/hillside/swamp photos that lack a true
# distant background plane.
# usage: python -I render_review.py <depth.onnx> <birefnet.onnx> <measures.json> <outdir> <folder> [marks.txt]
import sys, os, json, time, re
from concurrent.futures import ThreadPoolExecutor
import numpy as np
import onnxruntime as ort
import cv2
from PIL import Image

depth_model, cut_model, measures, outdir, folder = sys.argv[1:6]
marks_file = sys.argv[6] if len(sys.argv) > 6 else None
os.makedirs(outdir, exist_ok=True)
ort.set_default_logger_severity(3)
prov = ['DmlExecutionProvider', 'CPUExecutionProvider']
dsess = ort.InferenceSession(depth_model, providers=prov)
csess = ort.InferenceSession(cut_model, providers=prov)
MEAN = np.array([0.485, 0.456, 0.406], np.float32); STD = np.array([0.229, 0.224, 0.225], np.float32)
W_OUT = 720
FPS = 24

marks_r1 = {}
if marks_file and os.path.exists(marks_file):
    for line in open(marks_file, encoding='utf-8-sig'):
        m = re.match(r'(PASS|FAIL) \((\w+), played (\w+)\): (.+)$', line.strip())
        if m: marks_r1[m.group(4)] = (m.group(1), m.group(2), m.group(3))

def looks_like_screen(bgr):
    g = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)
    dx = np.abs(np.diff(g.astype(np.int16), axis=1))
    dy = np.abs(np.diff(g.astype(np.int16), axis=0))
    flat = ((dx == 0).mean() + (dy == 0).mean()) * 0.5
    return flat > 0.38

def depth_raw(rgb):
    h, w = rgb.shape[:2]; s = 518 / max(w, h)
    dw, dh = max(14, round(w * s / 14) * 14), max(14, round(h * s / 14) * 14)
    x = (cv2.resize(rgb, (dw, dh), interpolation=cv2.INTER_AREA).astype(np.float32) / 255 - MEAN) / STD
    d = dsess.run(None, {dsess.get_inputs()[0].name: x.transpose(2, 0, 1)[None]})[0].reshape(dh, dw)
    return (d - d.min()) / max(1e-6, d.max() - d.min())

def subject(rgb):
    h, w = rgb.shape[:2]
    x = (cv2.resize(rgb, (1024, 1024), interpolation=cv2.INTER_AREA).astype(np.float32) / 255 - MEAN) / STD
    out = csess.run(None, {csess.get_inputs()[0].name: x.transpose(2, 0, 1)[None]})[-1].reshape(1024, 1024)
    m1024 = 1 / (1 + np.exp(-out))
    top = float((m1024[0, :] > 0.5).mean())
    bottom = float((m1024[-1, :] > 0.5).mean())
    return cv2.resize(m1024, (w, h), interpolation=cv2.INTER_LINEAR).astype(np.float32), top, bottom

def guided(guide, p, r, eps=1e-3):
    k = (2 * r + 1, 2 * r + 1)
    mI = cv2.boxFilter(guide, -1, k); mp = cv2.boxFilter(p, -1, k)
    mIp = cv2.boxFilter(guide * p, -1, k); mII = cv2.boxFilter(guide * guide, -1, k)
    a = (mIp - mI * mp) / (mII - mI * mI + eps); b = mp - a * mI
    return cv2.boxFilter(a, -1, k) * guide + cv2.boxFilter(b, -1, k)

def holistic_alpha(bgr, d, m):
    h, w = bgr.shape[:2]
    fg_d = d[m > 0.5]
    bg_d = d[m < 0.2]
    bg_p20 = float(np.percentile(bg_d, 20)) if len(bg_d) else 0.05
    fg_p20 = float(np.percentile(fg_d, 20)) if len(fg_d) else 0.50
    thr, _ = cv2.threshold((d * 255).astype(np.uint8), 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    otsu = thr / 255.0
    grey = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY).astype(np.float32) / 255.0
    g = np.hypot(cv2.Sobel(grey, cv2.CV_32F, 1, 0), cv2.Sobel(grey, cv2.CV_32F, 0, 1))
    bg_tex = float(g[m < 0.2].mean()) if (m < 0.2).any() else 0.1
    if bg_p20 < 0.03 and bg_tex > 0.14:
        cut = min(otsu, bg_p20 + 0.06)
    else:
        cut = max(otsu, fg_p20 - 0.08)
    fg_bin = (d > cut).astype(np.uint8)
    num, labels = cv2.connectedComponents(fg_bin)
    anchor = (m > 0.2).copy()
    anchor[int(h * 0.92):, :] = True
    keep = np.isin(labels, np.unique(labels[anchor & (labels > 0)]))
    near_d = np.clip((d - cut) / 0.04 + 0.5, 0, 1).astype(np.float32) * cv2.dilate(keep.astype(np.uint8), np.ones((9, 9), np.uint8))
    alpha_d = np.clip((guided(grey, near_d, max(2, round(8 * w / 1280))) - 0.5) * 1.6 + 0.5, 0, 1)
    alpha = np.maximum(m, alpha_d)
    union_fg = alpha > 0.5
    u_area = float(union_fg.mean())
    rem_bg = d[~union_fg]
    bg_p90 = float(np.percentile(rem_bg, 90)) if len(rem_bg) else 1.0
    if u_area > 0.68:
        alpha = m
    return alpha, u_area, bg_p90

def push_pull(img, known):
    levels = [(img * known[..., None], known)]
    while min(levels[-1][1].shape) > 4:
        c, k = levels[-1]
        levels.append((cv2.resize(c, (c.shape[1] // 2, c.shape[0] // 2), interpolation=cv2.INTER_AREA),
                       cv2.resize(k, (k.shape[1] // 2, k.shape[0] // 2), interpolation=cv2.INTER_AREA)))
    c, k = levels[-1]; fill = c / np.maximum(k, 1e-4)[..., None]
    for c, k in reversed(levels[:-1]):
        up = cv2.resize(fill, (k.shape[1], k.shape[0]), interpolation=cv2.INTER_LINEAR)
        kk = np.clip(k, 0, 1)[..., None]
        fill = c / np.maximum(k, 0.05)[..., None] * kk + up * (1 - kk)
    return fill

def slide_frames(bgr, a, way, seconds=6.0):
    h, w = a.shape
    src = bgr.astype(np.float32)
    hidden = cv2.dilate((a > 0.08).astype(np.uint8), np.ones((11, 11), np.uint8)).astype(bool)
    plate = np.where(hidden[..., None], push_pull(src, (~hidden).astype(np.float32)), src)
    for y in range(h):
        row = hidden[y]; x = 0
        while x < w:
            if not row[x]: x += 1; continue
            s = x
            while x < w and row[x]: x += 1
            e = x
            for k in range(e - s):
                to = s + k if way > 0 else e - 1 - k
                fr = s - 1 - k if way > 0 else e + k
                if fr < 0 or fr >= w or hidden[y, fr]: break
                plate[y, to] = src[y, fr]
    T = 0.042
    pad = int(np.ceil(T * w)) + 2
    x0 = 0 if way > 0 else pad
    x1 = w - pad if way > 0 else w
    frames = []
    n = int(seconds * FPS)
    fa = a[..., None]
    for i in range(n):
        p = i / (n - 1); e = 0.5 - 0.5 * np.cos(p * np.pi)
        dx = -way * T * w * e
        B = np.float32([[1.0, 0, dx], [0, 1.0, 0]])
        back = cv2.warpAffine(plate, B, (w, h), borderMode=cv2.BORDER_REPLICATE)
        comp = np.clip(src * fa + back * (1 - fa), 0, 255).astype(np.uint8)
        frames.append(cv2.resize(comp[:, x0:x1], (w, h), interpolation=cv2.INTER_LINEAR))
    return frames + frames[::-1][1:]

def save_webp(frames, out_path):
    ims = [Image.fromarray(cv2.cvtColor(f, cv2.COLOR_BGR2RGB)) for f in frames]
    ims[0].save(out_path, save_all=True, append_images=ims[1:], duration=1000 // FPS, loop=0, quality=80)

rows = json.load(open(measures))
candidates = []
for r in rows:
    fn = r['file']
    in_r1_slide = fn in marks_r1 and marks_r1[fn][2] == 'Slide'
    base_cand = (0.04 <= r['area'] <= 0.65 and r['edge'] >= 0.30 and r['vague'] <= 0.085 and r['tex'] >= 0.03 and r['sides'] < 0.30)
    if base_cand or in_r1_slide:
        candidates.append(r)

cards = []
pool = ThreadPoolExecutor(max_workers=8)
futures = []
t0 = time.time()
for r in candidates:
    fn = r['file']
    path = os.path.join(folder, fn)
    bgr = cv2.imread(path)
    if bgr is None or looks_like_screen(bgr): continue
    h0, w0 = bgr.shape[:2]; s = W_OUT / max(w0, h0)
    bgr = cv2.resize(bgr, (round(w0 * s), round(h0 * s)), interpolation=cv2.INTER_AREA)
    rgb = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
    h, w = bgr.shape[:2]
    d = cv2.resize(depth_raw(rgb), (w, h), interpolation=cv2.INTER_CUBIC)
    m, top, bottom = subject(rgb)
    if top > 0.05 and bottom > 0.05:
        print('filtered to Rock (spans top-to-bottom):', fn, flush=True)
        continue
    a, u_area, bg_p90 = holistic_alpha(bgr, d, m)
    is_slide = (0.04 <= r['area'] <= 0.65 and r['edge'] >= 0.30 and r['vague'] <= 0.085 and r['tex'] >= 0.03 and r['sides'] < 0.30
                and bg_p90 <= 0.135 and not (u_area > 0.65 and r['area'] < 0.45))
    if not is_slide:
        print('filtered to Rock:', fn, f'u_area={u_area:.2f} bg_p90={bg_p90:.2f} v={r["vague"]:.2f}', flush=True)
        continue
    cxs = np.nonzero(m > 0.5)[1]
    way = 1 if (cxs.mean() if len(cxs) else w / 2) < w / 2 else -1
    frames = slide_frames(bgr, a, way)
    name = 'r%03d.webp' % len(cards)
    futures.append(pool.submit(save_webp, frames, os.path.join(outdir, name)))
    r1 = marks_r1.get(fn)
    group = 'Fixed' if (r1 and r1[0] == 'FAIL') else 'Slide'
    why = f'fg {(a > 0.5).mean():.0%} (was {r["area"]:.0%}) · step {r["edge"]:.2f} · far {bg_p90:.2f}'
    cards.append(dict(file=fn, fit=group, why=why, play='Slide', step=r['edge'], anim=name))
    print(len(cards), fn, group, flush=True)

for f in futures: f.result()
json.dump(cards, open(os.path.join(outdir, 'cards.json'), 'w'), indent=1)
print('done', len(cards), 'in', round(time.time() - t0), 's')
