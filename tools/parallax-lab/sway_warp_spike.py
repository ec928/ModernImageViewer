# Sway warp spike: the app's warp versus edge-following variants, at one sway amount, both peaks of the swing.
# usage: python -E -P warp_spike.py <depth.onnx> <photo> <sway %> <out prefix>
import sys, time
import numpy as np, cv2, onnxruntime as ort

T0 = time.time()
model, photo, sway, prefix = sys.argv[1], sys.argv[2], float(sys.argv[3]) / 100, sys.argv[4]
ort.set_default_logger_severity(3)
sess = ort.InferenceSession(model, providers=['CPUExecutionProvider'])
MEAN = np.array([0.485, 0.456, 0.406], np.float32); STD = np.array([0.229, 0.224, 0.225], np.float32)

bgr = cv2.imdecode(np.fromfile(photo, np.uint8), cv2.IMREAD_COLOR)
if bgr.shape[1] > 1920: bgr = cv2.resize(bgr, (1920, round(bgr.shape[0] * 1920 / bgr.shape[1])), interpolation=cv2.INTER_AREA)
H, W = bgr.shape[:2]
rgb = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
s = 518 / max(W, H); dw, dh = max(14, round(W * s / 14) * 14), max(14, round(H * s / 14) * 14)
x = (cv2.resize(rgb, (dw, dh), interpolation=cv2.INTER_AREA).astype(np.float32) / 255 - MEAN) / STD
raw = sess.run(None, {sess.get_inputs()[0].name: x.transpose(2, 0, 1)[None]})[0].reshape(dh, dw)
raw = (raw - raw.min()) / max(1e-6, raw.max() - raw.min())
g8 = np.round(raw * 255).astype(np.uint8)
grey8 = cv2.blur(cv2.dilate(g8, np.ones((3, 3), np.uint8)), (3, 3))
pivot = float(np.median(grey8)) / 255
grey = cv2.resize(grey8.astype(np.float32) / 255, (W, H), interpolation=cv2.INTER_CUBIC)  # the app's cubic upscale
rawup = cv2.resize(raw.astype(np.float32), (W, H), interpolation=cv2.INTER_CUBIC)

def guided(guide, p, r, eps):
    mI = cv2.boxFilter(guide, -1, (r, r)); mp = cv2.boxFilter(p, -1, (r, r))
    cov = cv2.boxFilter(guide * p, -1, (r, r)) - mI * mp
    var = cv2.boxFilter(guide * guide, -1, (r, r)) - mI * mI
    a = cov / (var + eps); b = mp - a * mI
    return cv2.boxFilter(a, -1, (r, r)) * guide + cv2.boxFilter(b, -1, (r, r))

gray = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY).astype(np.float32) / 255
r = max(5, round(W * 0.006)) | 1
snapped = np.clip(guided(gray, rawup, r, 1e-3), 0, 1)  # depth edges pulled onto the photo's own edges

xs, ys = np.meshgrid(np.arange(W, dtype=np.float32), np.arange(H, dtype=np.float32))

def warp_app(d, shift):
    # The app: each output pixel reads the photo at x - shift*(depth at the OUTPUT pixel - pivot).
    return cv2.remap(bgr, xs - shift * (d - pivot), ys, cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)

def warp_follow(d, shift, steps=4):
    # Depth is read where the pixel came from, so the outline moves with the picture.
    # Fixed point of src = x - shift*(d(src) - pivot); a few steps.
    src = xs - shift * (d - pivot)
    for _ in range(steps):
        dsrc = cv2.remap(d, src, ys, cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)
        src = xs - shift * (dsrc - pivot)
    return cv2.remap(bgr, src, ys, cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)

def warp_splat(d, shift):
    # Forward: each photo pixel goes to x + shift*(d - pivot). Nearer pixels are written last, so they cover farther
    # ones (no doubled outlines). A gap that opens is filled from whichever neighbour on the row is farther (background).
    dest = np.clip(np.rint(xs + shift * (d - pivot)), 0, W - 1).astype(np.int64)
    idx = (ys.astype(np.int64) * W + dest).ravel()
    order = np.argsort(d.ravel(), kind='stable')
    out = np.zeros((H * W, 3), np.uint8); dout = np.full(H * W, -1.0, np.float32)
    out[idx[order]] = bgr.reshape(-1, 3)[order]; dout[idx[order]] = d.ravel()[order]
    out = out.reshape(H, W, 3); dout = dout.reshape(H, W); hole = dout < 0
    col = np.arange(W)[None, :].repeat(H, 0)
    left = np.maximum.accumulate(np.where(hole, -1, col), axis=1)
    right = (W - 1 - np.maximum.accumulate(np.where(hole, -1, W - 1 - col)[:, ::-1], axis=1))[:, ::-1]
    right = np.where(right > W - 1, -1, right)
    rows = np.arange(H)[:, None].repeat(W, 1)
    lc, rc = np.clip(left, 0, W - 1), np.clip(right, 0, W - 1)
    dl = np.where(left >= 0, dout[rows, lc], 9); dr = np.where((right >= 0) & (right <= W - 1) & ~hole[rows, rc] | (right >= 0), dout[rows, rc], 9)
    dr = np.where(right >= 0, dr, 9)
    pick = np.where(dl <= dr, lc, rc)
    filled = out[rows, pick]
    out[hole] = filled[hole]
    return out

variants = [('app', lambda sh: warp_app(grey, sh)),
            ('splat', lambda sh: warp_splat(grey, sh)),
            ('splat-raw', lambda sh: warp_splat(rawup, sh)),
            ('follow', lambda sh: warp_follow(grey, sh))]
for name, fn in variants:
    for side in (1, -1):
        out = fn(side * sway * W)
        cv2.imwrite(f'{prefix}_{name}_{"R" if side > 0 else "L"}.png', out)
cv2.imwrite(f'{prefix}_still.png', bgr)
cv2.imwrite(f'{prefix}_depth.png', (grey * 255).astype(np.uint8))
cv2.imwrite(f'{prefix}_snapped.png', (snapped * 255).astype(np.uint8))
print('done', W, 'x', H, 'pivot', round(pivot, 2), round(time.time() - T0, 1), 's')

# Clip: one 4.25 s swing at 24 fps, app warp | splat warp, side by side. Splat is drawn at 2x width and scaled back.
if len(sys.argv) > 5 and sys.argv[5] == 'clip':
    from PIL import Image
    import math
    big = cv2.resize(bgr, (W * 2, H), interpolation=cv2.INTER_CUBIC)
    gbig = cv2.resize(grey, (W * 2, H), interpolation=cv2.INTER_LINEAR)
    xs2, ys2 = np.meshgrid(np.arange(W * 2, dtype=np.float32), np.arange(H, dtype=np.float32))
    def splat2(shift):
        global bgr, W, xs, ys
        sb, sW, sx, sy = bgr, W, xs, ys
        bgr, W, xs, ys = big, W * 2, xs2, ys2
        try: out = warp_splat(gbig, shift * 2)
        finally: bgr, W, xs, ys = sb, sW, sx, sy
        return cv2.resize(out, (W, H), interpolation=cv2.INTER_AREA)
    frames = []; n = 102
    for i in range(n):
        if time.time() - T0 > 110: print('DEADLINE at frame', i); break
        sh = math.sin(i / n * 2 * math.pi) * sway * W
        a = warp_app(grey, sh); b = splat2(sh)
        for im, label in ((a, 'now'), (b, 'splat')):
            cv2.putText(im, label, (12, 34), 0, 1.0, (255, 255, 255), 2)
        sheet = np.hstack([a, np.zeros((H, 8, 3), np.uint8), b])
        frames.append(Image.fromarray(cv2.cvtColor(sheet, cv2.COLOR_BGR2RGB)))
    frames[0].save(prefix + '_clip.webp', save_all=True, append_images=frames[1:], duration=1000 // 24, loop=0, quality=88)
    print('clip', len(frames), 'frames', round(time.time() - T0, 1), 's')
