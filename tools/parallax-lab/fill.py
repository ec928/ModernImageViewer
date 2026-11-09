# Slide's end frame with the subject cut out (BiRefNet fp16) and the uncovered strip filled two ways:
# P = push-pull (the app now), R = the real background beyond the subject's trailing edge, mirrored into it.
# usage: python -I fill.py <birefnet.onnx> <outdir> <travel> <photo> [...]
import sys, os
import numpy as np
import onnxruntime as ort
import cv2

cut_model, outdir, T = sys.argv[1], sys.argv[2], float(sys.argv[3])
os.makedirs(outdir, exist_ok=True)
sess = ort.InferenceSession(cut_model, providers=['DmlExecutionProvider', 'CPUExecutionProvider'])
cin = sess.get_inputs()[0].name
MEAN = np.array([0.485, 0.456, 0.406], np.float32); STD = np.array([0.229, 0.224, 0.225], np.float32)

def subject(rgb):
    h, w = rgb.shape[:2]
    x = (cv2.resize(rgb, (1024, 1024), interpolation=cv2.INTER_AREA).astype(np.float32) / 255 - MEAN) / STD
    out = sess.run(None, {cin: x.transpose(2, 0, 1)[None]})[-1].reshape(1024, 1024)
    return cv2.resize(1 / (1 + np.exp(-out)), (w, h), interpolation=cv2.INTER_LINEAR).astype(np.float32)

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
        fill = c / np.maximum(k, 1e-4)[..., None] * kk + up * (1 - kk)
    return fill

def mirror_fill(img, hidden, way):
    # Each row: a hidden run is uncovered from its trailing side (the side the subject moves away from). Fill it by
    # reflecting the visible background beyond that side into it; what reflection cannot reach stays for push-pull.
    out = img.copy(); done = ~hidden.copy()
    h, w = hidden.shape
    for y in range(h):
        row = hidden[y]
        x = 0
        while x < w:
            if not row[x]: x += 1; continue
            s = x
            while x < w and row[x]: x += 1
            e = x  # hidden run [s, e)
            if way > 0:   # moving right: uncovered from the left edge s
                for k in range(e - s):
                    src = s - 1 - k
                    if src < 0 or hidden[y, src]: break
                    out[y, s + k] = img[y, src]; done[y, s + k] = True
            else:         # moving left: uncovered from the right edge e
                for k in range(e - s):
                    src = e + k
                    if src >= w or hidden[y, src]: break
                    out[y, e - 1 - k] = img[y, src]; done[y, e - 1 - k] = True
    return out, done

for path in sys.argv[4:]:
    tag = path.split('OHR.')[-1].split('_')[0] if 'OHR.' in path else os.path.splitext(os.path.basename(path))[0]
    bgr = cv2.imread(path); h0, w0 = bgr.shape[:2]; sc = 1280 / max(w0, h0)
    bgr = cv2.resize(bgr, (round(w0 * sc), round(h0 * sc)), interpolation=cv2.INTER_AREA)
    h, w = bgr.shape[:2]
    a = subject(cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB))
    hidden = cv2.dilate((a > 0.15).astype(np.uint8), np.ones((7, 7), np.uint8)).astype(bool)
    src = bgr.astype(np.float32)
    pp = push_pull(src, (~hidden).astype(np.float32))
    plateP = np.where(hidden[..., None], pp, src)
    for way in (1,):
        mir, done = mirror_fill(src, hidden, way)
        plateR = np.where(done[..., None], mir, pp)
        plateR = np.where(hidden[..., None], plateR, src)
        dx = way * T * w
        M = np.float32([[1, 0, dx], [0, 1, 0]])
        fg = cv2.warpAffine(src, M, (w, h), borderMode=cv2.BORDER_REPLICATE)
        fa = cv2.warpAffine(a, M, (w, h))[..., None]
        endP = fg * fa + plateP * (1 - fa)
        endR = fg * fa + plateR * (1 - fa)
        cv2.imwrite(os.path.join(outdir, f'{tag}_P_R.jpg'), np.clip(np.vstack([endP, endR]), 0, 255).astype(np.uint8), [cv2.IMWRITE_JPEG_QUALITY, 90])
    print(tag, flush=True)
print('done')
