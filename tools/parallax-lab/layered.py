# Slide as two rigid layers, with only steps that port to C#: depth threshold (Otsu), a guided filter against the photo
# to put the cut's edge on the photo's edges, a push-pull fill for what the near layer hides, and the near layer moved
# as one piece over a background that holds still.
# usage: python -I layered.py <depth.onnx> <outdir> <travel> <photo> [...]
import sys, os
import numpy as np
import onnxruntime as ort
import cv2
from PIL import Image

model, outdir, T = sys.argv[1], sys.argv[2], float(sys.argv[3])
os.makedirs(outdir, exist_ok=True)
sess = ort.InferenceSession(model, providers=['DmlExecutionProvider', 'CPUExecutionProvider'])
name_in = sess.get_inputs()[0].name
MEAN = np.array([0.485, 0.456, 0.406], np.float32); STD = np.array([0.229, 0.224, 0.225], np.float32)

def depth(rgb):
    h, w = rgb.shape[:2]; s = 518 / max(w, h)
    dw, dh = max(14, round(w * s / 14) * 14), max(14, round(h * s / 14) * 14)
    x = (cv2.resize(rgb, (dw, dh), interpolation=cv2.INTER_AREA).astype(np.float32) / 255 - MEAN) / STD
    d = sess.run(None, {name_in: x.transpose(2, 0, 1)[None]})[0].reshape(dh, dw)
    return (d - d.min()) / max(1e-6, d.max() - d.min())

def otsu(d):
    hist, _ = np.histogram(d, 256, (0, 1)); p = hist / hist.sum(); best, cut = -1, 0.5
    w0 = np.cumsum(p); mu = np.cumsum(p * np.arange(256)); mt = mu[-1]
    for t in range(1, 255):
        if w0[t] in (0, 1): continue
        v = (mt * w0[t] - mu[t]) ** 2 / (w0[t] * (1 - w0[t]))
        if v > best: best, cut = v, t / 255
    return cut

def box(a, r): return cv2.blur(a, (2 * r + 1, 2 * r + 1))

def guided(I, p, r, eps):
    mI, mp = box(I, r), box(p, r)
    a = (box(I * p, r) - mI * mp) / (box(I * I, r) - mI * mI + eps)
    b = mp - a * mI
    return box(a, r) * I + box(b, r)

def push_pull(img, known):
    # Fill unknown pixels from a pyramid of known-weighted averages.
    levels = [(img * known[..., None], known)]
    while min(levels[-1][1].shape) > 4:
        c, k = levels[-1]
        c2 = cv2.resize(c, (max(1, c.shape[1] // 2), max(1, c.shape[0] // 2)), interpolation=cv2.INTER_AREA)
        k2 = cv2.resize(k, (max(1, k.shape[1] // 2), max(1, k.shape[0] // 2)), interpolation=cv2.INTER_AREA)
        levels.append((c2, k2))
    c, k = levels[-1]
    fill = c / np.maximum(k, 1e-4)[..., None]
    for c, k in reversed(levels[:-1]):
        up = cv2.resize(fill, (k.shape[1], k.shape[0]), interpolation=cv2.INTER_LINEAR)
        own = c / np.maximum(k, 1e-4)[..., None]
        kk = np.clip(k, 0, 1)[..., None]
        fill = own * kk + up * (1 - kk)
    return fill

N = 72
for path in sys.argv[4:]:
    tag = os.path.basename(path).split('OHR.')[-1].split('_')[0] if 'OHR.' in path else os.path.splitext(os.path.basename(path))[0]
    bgr = cv2.imread(path); h0, w0 = bgr.shape[:2]; sc = 1280 / max(w0, h0)
    bgr = cv2.resize(bgr, (round(w0 * sc), round(h0 * sc)), interpolation=cv2.INTER_AREA)
    h, w = bgr.shape[:2]
    d = depth(cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB))
    cut = otsu(d)
    D = cv2.resize(d, (w, h), interpolation=cv2.INTER_CUBIC)
    hard = np.clip((D - cut) / 0.04 + 0.5, 0, 1).astype(np.float32)
    grey = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY).astype(np.float32) / 255
    alpha = np.clip(guided(grey, hard, 8, 1e-3), 0, 1)
    alpha = np.clip((alpha - 0.5) * 1.6 + 0.5, 0, 1)        # firmer edge
    # The background: the photo with what the near layer covers (grown, so its fringe goes too) filled from around.
    # Only what the near layer hides needs filling, plus a thin fringe so its edge colour does not stay behind.
    covered = cv2.dilate((alpha > 0.15).astype(np.uint8), np.ones((7, 7), np.uint8)).astype(np.float32)
    plate = push_pull(bgr.astype(np.float32), 1 - covered)
    plate = cv2.GaussianBlur(plate, (0, 0), 1.0) * covered[..., None] + bgr.astype(np.float32) * (1 - covered[..., None])
    src = bgr.astype(np.float32)
    frames = []
    over = 1.03
    for k in range(N):
        p = 0.5 - 0.5 * np.cos(np.pi * k / (N - 1))
        sx = (p - 0.5) * T * w
        M = np.float32([[1, 0, sx], [0, 1, 0]])
        fg = cv2.warpAffine(src, M, (w, h), borderMode=cv2.BORDER_REPLICATE)
        fa = cv2.warpAffine(alpha, M, (w, h), borderMode=cv2.BORDER_CONSTANT)[..., None]
        out = fg * fa + plate * (1 - fa)
        Z = cv2.getRotationMatrix2D((w / 2, h / 2), 0, over)
        frames.append(np.clip(cv2.warpAffine(out, Z, (w, h)), 0, 255).astype(np.uint8))
    ims = [Image.fromarray(cv2.cvtColor(f, cv2.COLOR_BGR2RGB)) for f in frames]
    ims[0].save(os.path.join(outdir, f'{tag}_layered.webp'), save_all=True, append_images=ims[1:], duration=1000 // 24, loop=0, quality=85)
    cv2.imwrite(os.path.join(outdir, f'{tag}_ends.jpg'), np.hstack([frames[0], frames[-1]]), [cv2.IMWRITE_JPEG_QUALITY, 90])
    cv2.imwrite(os.path.join(outdir, f'{tag}_alpha.png'), (alpha * 255).astype(np.uint8))
    print(tag, 'cut', round(cut, 2), 'near share', round(float((alpha > 0.5).mean()), 2), flush=True)
print('done')
